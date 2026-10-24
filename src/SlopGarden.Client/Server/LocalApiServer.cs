using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Runtime;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

/// <summary>Tiny LAN API: <c>GET /status</c> and <c>POST /pair</c> (used by the server's "add device by IP").</summary>
public sealed class LocalApiServer(
    IOptions<ClientOptions> options,
    IIdentityStore identity,
    IServerLink link,
    DeviceConnection connection,
    DeviceRuntime runtime,
    PluginRegistry plugins,
    ILogger<LocalApiServer> logger) : BackgroundService
{
    private const int MaxBodyBytes = 16 * 1024;

    public ClientLocalStatus Status()
    {
        var id = identity.Current;
        return new ClientLocalStatus(id.HardwareId, ClientInfo.Hostname(options.Value), ClientInfo.Version, link.State, id.ServerUrl, id.DeviceId,
            link.IsConnected, runtime.AppliedVersion, plugins.Describe());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.LocalApiPort;
        if (port <= 0) return;
        using var listener = Start(port);
        if (listener is null) return;
        using var registration = stoppingToken.Register(listener.Stop);
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Local API stopped unexpectedly");
                return;
            }
            _ = Task.Run(() => HandleAsync(context), stoppingToken);
        }
    }

    private HttpListener? Start(int port)
    {
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                logger.LogInformation("Local API listening on {Prefix}", prefix);
                return listener;
            }
            catch (HttpListenerException ex)
            {
                logger.LogDebug("Local API cannot bind {Prefix}: {Error}", prefix, ex.Message);
                listener.Close();
            }
        }
        logger.LogWarning("Local API port {Port} is unavailable (in use or not permitted); continuing without it. Set Client:LocalApiPort to change it", port);
        return null;
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            switch (request.HttpMethod, path)
            {
                case ("GET", "" or "/status"):
                    await WriteJsonAsync(response, HttpStatusCode.OK, Status());
                    break;
                case ("POST", "/pair"):
                    await PairAsync(request, response);
                    break;
                default:
                    await WriteJsonAsync(response, HttpStatusCode.NotFound, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local API request {Method} {Url} failed", request.HttpMethod, request.Url);
            try
            {
                await WriteJsonAsync(response, HttpStatusCode.InternalServerError, new { error = "internal error" });
            }
            catch (Exception)
            {
                // The client may already have disconnected.
            }
        }
        finally
        {
            response.Close();
        }
    }

    private async Task PairAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            await WriteJsonAsync(response, HttpStatusCode.RequestEntityTooLarge, new { error = "body too large" });
            return;
        }
        ManualPairRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ManualPairRequest>(request.InputStream, ClientJson.Options);
        }
        catch (JsonException)
        {
            body = null;
        }
        var server = ServerUrls.TryNormalize(body?.ServerUrl);
        if (server is null)
        {
            await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { error = "serverUrl must be an absolute http(s) URL" });
            return;
        }
        if (connection.ServerUrlIsConfigured)
        {
            await WriteJsonAsync(response, HttpStatusCode.Conflict, new { error = "the server URL is fixed by the client configuration (Client:ServerUrl)" });
            return;
        }
        var current = identity.Current;
        var sameServer = ServerUrls.TryNormalize(current.ServerUrl) == server;
        var force = string.Equals(request.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase) && IPAddress.IsLoopback(request.RemoteEndPoint.Address);
        // An approved device only re-pairs from the device itself, so a LAN peer cannot hijack it.
        if (!string.IsNullOrEmpty(current.DeviceToken) && !sameServer && !force)
        {
            await WriteJsonAsync(response, HttpStatusCode.Conflict, new
            {
                error = $"already paired with {current.ServerUrl}; run 'slopgarden-client pair --server <url>' or 'reset' on the device to re-pair",
            });
            return;
        }
        if (!sameServer || string.IsNullOrEmpty(current.DeviceToken)) connection.PairManually(server);
        await WriteJsonAsync(response, HttpStatusCode.Accepted, Status() with { ServerUrl = server.ToString() });
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, HttpStatusCode status, T body)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, body, ClientJson.Indented);
    }
}
