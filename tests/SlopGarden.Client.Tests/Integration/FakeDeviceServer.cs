using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using Xunit.Abstractions;

namespace SlopGarden.Client.Tests.Integration;

public sealed record ReceivedFrame(Guid CameraId, Guid? RequestId, string ContentType, byte[] Data, int? Width, int? Height);

/// <summary>Everything the fake server received, plus knobs the tests turn.</summary>
public sealed class FakeServerState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Guid DeviceId, string Secret)> _devices = [];
    private int _tokenCounter;

    public bool AutoApprove { get; set; } = true;
    public Func<DeviceConfigDocument>? Config { get; set; }
    public string? ValidToken { get; private set; }

    public ConcurrentQueue<DeviceRegistrationRequest> Registrations { get; } = new();
    public ConcurrentQueue<(DeviceHello Hello, string Token)> Hellos { get; } = new();
    public ConcurrentQueue<ReadingSample> Readings { get; } = new();
    public ConcurrentQueue<ActuatorStateReport> States { get; } = new();
    public ConcurrentQueue<ActuatorRunReport> Runs { get; } = new();
    public ConcurrentQueue<CommandAck> Acks { get; } = new();
    public ConcurrentQueue<DeviceEventReport> Events { get; } = new();
    public ConcurrentQueue<ConfigAppliedReport> ConfigsApplied { get; } = new();
    public ConcurrentQueue<DiscoveredHardwareReport> Discoveries { get; } = new();
    public ConcurrentQueue<ReceivedFrame> Frames { get; } = new();
    public ConcurrentDictionary<string, HubCallerContext> Connections { get; } = new();

    public Guid Register(DeviceRegistrationRequest request)
    {
        Registrations.Enqueue(request);
        lock (_gate)
        {
            var id = _devices.TryGetValue(request.HardwareId, out var existing) ? existing.DeviceId : Guid.NewGuid();
            _devices[request.HardwareId] = (id, request.RegistrationSecret);
            return id;
        }
    }

    public DeviceRegistrationStatus? Poll(Guid deviceId, string secret)
    {
        lock (_gate)
        {
            if (!_devices.Values.Any(d => d.DeviceId == deviceId && d.Secret == secret)) return null;
            if (!AutoApprove) return new DeviceRegistrationStatus(deviceId, DeviceStatuses.Pending, null, "waiting for approval");
            ValidToken ??= $"token-{++_tokenCounter}-{Guid.NewGuid():N}";
            return new DeviceRegistrationStatus(deviceId, DeviceStatuses.Approved, ValidToken, null);
        }
    }

    /// <summary>Invalidates the current token (like "revoke and re-approve") and drops live connections.</summary>
    public void RotateToken()
    {
        lock (_gate) ValidToken = null;
        foreach (var c in Connections.Values) c.Abort();
    }

    public IReadOnlyList<string> AckStatuses(Guid commandId) => Acks.Where(a => a.CommandId == commandId).Select(a => a.Status).ToList();
}

public sealed class FakeDeviceHub(FakeServerState state) : Hub
{
    private string Token => Context.GetHttpContext()?.Items["token"] as string ?? "";

    public override Task OnConnectedAsync()
    {
        state.Connections[Context.ConnectionId] = Context;
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.Connections.TryRemove(Context.ConnectionId, out _);
        return Task.CompletedTask;
    }

    public async Task Hello(DeviceHello hello)
    {
        state.Hellos.Enqueue((hello, Token));
        if (state.Config is { } config) await Clients.Caller.SendAsync(DeviceHubMethods.ApplyConfig, config());
    }

    public ReadingBatchAck ReportReadings(ReadingBatch batch)
    {
        foreach (var r in batch.Readings) state.Readings.Enqueue(r);
        return new ReadingBatchAck(batch.BatchId, batch.Readings.Count);
    }

    public void ReportActuatorState(ActuatorStateReport report) => state.States.Enqueue(report);
    public void ReportActuatorRun(ActuatorRunReport run) => state.Runs.Enqueue(run);
    public void AckCommand(CommandAck ack) => state.Acks.Enqueue(ack);
    public void ReportEvent(DeviceEventReport e) => state.Events.Enqueue(e);
    public void ReportConfigApplied(ConfigAppliedReport report) => state.ConfigsApplied.Enqueue(report);
    public void ReportDiscoveredHardware(DiscoveredHardwareReport report) => state.Discoveries.Enqueue(report);
}

/// <summary>Minimal stand-in for the SlopGarden server's device API (registration, hub, frame upload).</summary>
public sealed class FakeDeviceServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeDeviceServer(WebApplication app, FakeServerState state)
    {
        _app = app;
        State = state;
    }

    public FakeServerState State { get; }
    public Uri Url { get; private set; } = null!;
    public IHubContext<FakeDeviceHub> Hub => _app.Services.GetRequiredService<IHubContext<FakeDeviceHub>>();

    public static async Task<FakeDeviceServer> StartAsync(ITestOutputHelper output, FakeServerState? state = null, int port = 0)
    {
        state ??= new FakeServerState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders().AddProvider(new XunitLoggerProvider(output, LogLevel.Warning));
        builder.Services.AddSingleton(state);
        builder.Services.ConfigureHttpJsonOptions(o => SlopJson.Configure(o.SerializerOptions));
        builder.Services.AddSignalR().AddJsonProtocol(o => SlopJson.Configure(o.PayloadSerializerOptions));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var needsToken = context.Request.Path.StartsWithSegments("/hubs/device") || context.Request.Path.StartsWithSegments("/api/device-api/cameras");
            if (needsToken)
            {
                var header = context.Request.Headers.Authorization.ToString();
                var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : context.Request.Query["access_token"].ToString();
                if (string.IsNullOrEmpty(token) || token != state.ValidToken)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                context.Items["token"] = token;
            }
            await next(context);
        });
        app.MapPost("/api/device-api/register", (DeviceRegistrationRequest request) =>
        {
            var id = state.Register(request);
            return new DeviceRegistrationResponse(id, state.AutoApprove ? DeviceStatuses.Approved : DeviceStatuses.Pending, null);
        });
        app.MapGet("/api/device-api/register/{deviceId:guid}", (Guid deviceId, string secret) =>
            state.Poll(deviceId, secret) is { } status ? Results.Ok(status) : Results.NotFound());
        app.MapPost("/api/device-api/cameras/{cameraId:guid}/frames", async (Guid cameraId, Guid? requestId, int? width, int? height, HttpRequest request) =>
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            state.Frames.Enqueue(new ReceivedFrame(cameraId, requestId, request.ContentType ?? "", ms.ToArray(), width, height));
            return Results.Ok();
        });
        app.MapHub<FakeDeviceHub>("/hubs/device");

        await app.StartAsync();
        var server = new FakeDeviceServer(app, state) { Url = new Uri(app.Urls.First().TrimEnd('/') + "/") };
        return server;
    }

    public Task SendAsync(string method, object arg) => Hub.Clients.All.SendAsync(method, arg);

    public async ValueTask DisposeAsync()
    {
        foreach (var c in State.Connections.Values) c.Abort();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
