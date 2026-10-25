using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Cameras;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Runtime;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Updates;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

/// <summary>Finds the server, registers and waits for approval, then keeps the device hub connected forever.</summary>
public sealed class DeviceConnection(
    IOptions<ClientOptions> options,
    IIdentityStore identity,
    ServerLink link,
    ServerDiscovery discovery,
    RegistrationClient registration,
    PluginRegistry plugins,
    DeviceRuntime runtime,
    ActuatorController actuators,
    CameraManager cameras,
    HardwareDiscoveryRunner hardware,
    ContainerUpdater updates,
    Outbox outbox,
    TimeProvider time,
    ILogger<DeviceConnection> logger) : BackgroundService
{
    public static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RejectedPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Lock _sessionGate = new();
    private CancellationTokenSource? _session;

    /// <summary>Points the client at a server given by the user/server ("add device by IP") and restarts registration.</summary>
    public void PairManually(Uri serverUrl, string via = DiscoveredVia.ManualIp)
    {
        identity.Update(i => i with { ServerUrl = serverUrl.ToString(), DiscoveredVia = via, DeviceId = null, DeviceToken = null });
        logger.LogInformation("Pairing with {ServerUrl} (manual)", serverUrl);
        lock (_sessionGate) _session?.Cancel();
    }

    public bool ServerUrlIsConfigured => ServerUrls.TryNormalize(options.Value.ServerUrl) is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sessionGate) _session = session;
            try
            {
                await RunSessionAsync(session.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                failures = 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Server connection failed");
                await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], time, stoppingToken);
            }
            finally
            {
                lock (_sessionGate) _session = null;
                session.Dispose();
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var (server, via) = await ResolveServerAsync(ct);
        var token = await EnsureApprovedAsync(server, via, ct);
        await RunHubAsync(server, token, ct);
    }

    private async Task<(Uri Server, string Via)> ResolveServerAsync(CancellationToken ct)
    {
        if (ServerUrls.TryNormalize(options.Value.ServerUrl) is { } configured)
        {
            identity.Update(i => SameServer(i.ServerUrl, configured)
                ? i with { DiscoveredVia = DiscoveredVia.Configured }
                : i with { ServerUrl = configured.ToString(), DiscoveredVia = DiscoveredVia.Configured, DeviceId = null, DeviceToken = null });
            return (configured, DiscoveredVia.Configured);
        }
        var current = identity.Current;
        if (ServerUrls.TryNormalize(current.ServerUrl) is { } known) return (known, current.DiscoveredVia ?? DiscoveredVia.Broadcast);

        link.State = ClientStates.Discovering;
        var (found, via) = await discovery.DiscoverAsync(ct);
        identity.Update(i => i with { ServerUrl = found.ToString(), DiscoveredVia = via, DeviceId = null, DeviceToken = null });
        return (found, via);
    }

    private static bool SameServer(string? a, Uri b) => ServerUrls.TryNormalize(a) is { } ua && Uri.Compare(ua, b, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private async Task<string> EnsureApprovedAsync(Uri server, string via, CancellationToken ct)
    {
        var failures = 0;
        string? lastStatus = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var id = identity.Current;
            if (!string.IsNullOrEmpty(id.DeviceToken)) return id.DeviceToken;
            TimeSpan wait;
            try
            {
                if (id.DeviceId is null)
                {
                    link.State = ClientStates.Registering;
                    var o = options.Value;
                    var request = new DeviceRegistrationRequest(id.HardwareId, ClientInfo.Hostname(o), ClientInfo.Os, ClientInfo.Architecture, ClientInfo.Version,
                        id.RegistrationSecret, via, ClientInfo.LocalEndpoint(server, o.LocalApiPort), ClientInfo.DescribeEnvironment(o, time), plugins.Describe());
                    var response = await registration.RegisterAsync(server, request, ct);
                    identity.Update(i => i with { DeviceId = response.DeviceId });
                    logger.LogInformation("Registered with {ServerUrl} as device {DeviceId} ({Status}){Message}", server, response.DeviceId, response.Status,
                        response.Message is { Length: > 0 } m ? $": {m}" : "");
                    failures = 0;
                    continue;
                }

                var status = await registration.GetStatusAsync(server, id.DeviceId.Value, id.RegistrationSecret, ct);
                failures = 0;
                if (status is null)
                {
                    logger.LogWarning("Server no longer knows device {DeviceId}; registering again", id.DeviceId);
                    identity.Update(i => i with { DeviceId = null, DeviceToken = null });
                    continue;
                }
                if (status.Status != lastStatus)
                {
                    logger.LogInformation("Registration status: {Status}{Message}", status.Status, status.Message is { Length: > 0 } m ? $" ({m})" : "");
                    lastStatus = status.Status;
                }
                switch (status.Status)
                {
                    case DeviceStatuses.Approved when !string.IsNullOrEmpty(status.DeviceToken):
                        identity.Update(i => i with { DeviceToken = status.DeviceToken });
                        logger.LogInformation("Device approved by the server");
                        continue;
                    case DeviceStatuses.Approved:
                        logger.LogWarning("Device is approved but its token was already delivered and is not stored here; requesting a new registration");
                        identity.Update(i => i with { DeviceId = null, DeviceToken = null });
                        wait = PendingPollInterval;
                        break;
                    case DeviceStatuses.Rejected or DeviceStatuses.Revoked:
                        link.State = ClientStates.Rejected;
                        wait = RejectedPollInterval;
                        break;
                    default:
                        link.State = ClientStates.PendingApproval;
                        wait = PendingPollInterval;
                        break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                wait = Backoff[Math.Min(failures++, Backoff.Length - 1)] * 2;
                logger.LogWarning("Server {ServerUrl} unreachable during registration: {Error}; retrying in {Delay}s", server, ex.Message, wait.TotalSeconds);
            }
            await Task.Delay(wait, time, ct);
        }
    }

    private async Task RunHubAsync(Uri server, string token, CancellationToken ct)
    {
        var policy = new ForeverRetryPolicy();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server, "hubs/device"), o => o.AccessTokenProvider = () => Task.FromResult(identity.Current.DeviceToken))
            .WithAutomaticReconnect(policy)
            .AddJsonProtocol(o => SlopJson.Configure(o.PayloadSerializerOptions))
            .Build();
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterHandlers(hub, ct);
        hub.Reconnecting += ex =>
        {
            link.SetReady(false);
            link.State = ClientStates.Reconnecting;
            logger.LogWarning("Connection to server lost ({Error}); reconnecting", ex?.Message ?? "closed");
            return Task.CompletedTask;
        };
        hub.Reconnected += _ => OnConnectedAsync(hub, ct);
        hub.Closed += ex =>
        {
            closed.TrySetResult(ex);
            return Task.CompletedTask;
        };

        link.Attach(hub);
        try
        {
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                link.State = ClientStates.Connecting;
                try
                {
                    await hub.StartAsync(ct);
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    DropToken(ex.StatusCode);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
                    if (attempt <= 3 || attempt % 10 == 0)
                        logger.LogWarning("Could not connect to {ServerUrl}: {Error}; retrying in {Delay}s", server, ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, time, ct);
                }
            }

            logger.LogInformation("Connected to {ServerUrl}", server);
            await OnConnectedAsync(hub, ct);
            var reason = await closed.Task.WaitAsync(ct);
            if (policy.Unauthorized || reason is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                DropToken(HttpStatusCode.Unauthorized);
            else
                logger.LogWarning("Hub connection closed: {Error}", reason?.Message ?? "by server");
        }
        finally
        {
            link.Attach(null);
            link.State = ClientStates.Connecting;
            await hub.DisposeAsync();
        }
    }

    private void DropToken(HttpStatusCode? status)
    {
        logger.LogWarning("Server rejected the device token ({Status}); discarding it and checking registration", (int?)status);
        identity.Update(i => i with { DeviceToken = null });
    }

    private void RegisterHandlers(HubConnection hub, CancellationToken ct)
    {
        hub.On<DeviceConfigDocument>(DeviceHubMethods.ApplyConfig, doc => Guard(DeviceHubMethods.ApplyConfig, () => runtime.ApplyAsync(doc)));
        hub.On<ActuatorCommandMessage>(DeviceHubMethods.ExecuteCommand, cmd => Guard(DeviceHubMethods.ExecuteCommand, () => actuators.ExecuteCommandAsync(cmd)));
        hub.On<EmergencyStopState>(DeviceHubMethods.EmergencyStop, state => Guard(DeviceHubMethods.EmergencyStop, () => runtime.SetEmergencyStopAsync(state)));
        hub.On<CaptureFrameRequest>(DeviceHubMethods.CaptureFrame, request =>
        {
            _ = Guard(DeviceHubMethods.CaptureFrame, () => cameras.CaptureOnRequestAsync(request, ct));
            return Task.CompletedTask;
        });
        hub.On(DeviceHubMethods.RequestDiscovery, () =>
        {
            _ = Guard(DeviceHubMethods.RequestDiscovery, () => hardware.RunAndReportAsync(ct));
            return Task.CompletedTask;
        });
        hub.On<ApplyUpdateCommand>(DeviceHubMethods.ApplyUpdate, command =>
        {
            _ = Guard(DeviceHubMethods.ApplyUpdate, () => updates.ApplyAsync(command, ct));
            return Task.CompletedTask;
        });
    }

    private async Task Guard(string method, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling {HubMethod} failed", method);
        }
    }

    private async Task OnConnectedAsync(HubConnection hub, CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            var hello = new DeviceHello(ClientInfo.Version, ClientInfo.Os, ClientInfo.Architecture, ClientInfo.Hostname(o), runtime.AppliedVersion,
                ClientInfo.DescribeEnvironment(o, time), plugins.Describe(), (int)Math.Min(int.MaxValue, outbox.ReadingCount), DockerEngine.CanUpdate);
            await hub.InvokeAsync(DeviceHubMethods.Hello, hello, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Hello was not accepted: {Error}", ex.Message);
        }
        link.SetReady(true);
        link.State = ClientStates.Connected;
        foreach (var state in actuators.CurrentStates()) link.Post(DeviceHubMethods.ReportActuatorState, state);
        outbox.MessagesAvailable.Set();
        _ = Guard(DeviceHubMethods.ReportDiscoveredHardware, () => hardware.RunAndReportAsync(ct));
    }

    /// <summary>Reconnects forever with capped backoff, except when the server rejects the token.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public volatile bool Unauthorized;

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.RetryReason is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                Unauthorized = true;
                return null;
            }
            var baseDelay = Backoff[(int)Math.Min(retryContext.PreviousRetryCount, Backoff.Length - 1)];
            return retryContext.PreviousRetryCount == 0 ? TimeSpan.Zero : baseDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        }
    }
}
