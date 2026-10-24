using System.Net.Http.Headers;
using System.Text.Json;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Client.Cameras;

/// <summary>Captures camera frames periodically (only while connected) and on request, and uploads them.</summary>
public sealed class CameraManager(
    PluginRegistry registry,
    IServerLink link,
    ServerHttp http,
    IDeviceReporter reporter,
    IServiceProvider services,
    TimeProvider time,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CameraManager>();
    private readonly TimeProvider _time = time;
    private readonly IServerLink _link = link;
    private readonly ServerHttp _http = http;
    private readonly IDeviceReporter _reporter = reporter;
    private readonly Dictionary<Guid, CameraRunner> _runners = [];
    private readonly Lock _gate = new();

    public async Task ApplyAsync(IReadOnlyList<CameraConfig> cameras, List<string> errors)
    {
        var desired = cameras.Where(c => c.Enabled).ToDictionary(c => c.Id);
        List<CameraRunner> removed;
        lock (_gate)
        {
            removed = _runners.Values
                .Where(r => !desired.TryGetValue(r.Config.Id, out var cfg) || !ReferenceEquals(registry.FindCamera(cfg.PluginId), r.Plugin) || !Same(cfg, r.Config))
                .ToList();
            foreach (var r in removed) _runners.Remove(r.Config.Id);
        }
        foreach (var r in removed) await r.DisposeAsync();

        foreach (var cfg in desired.Values)
        {
            lock (_gate)
            {
                if (_runners.ContainsKey(cfg.Id)) continue;
            }
            var plugin = registry.FindCamera(cfg.PluginId);
            if (plugin is null)
            {
                errors.Add($"Camera '{cfg.Name}' ({cfg.Id}): unknown or unavailable plugin '{cfg.PluginId}'.");
                continue;
            }
            try
            {
                var logger = loggerFactory.CreateLogger($"SlopGarden.Plugin.{plugin.PluginId}");
                var camera = plugin.Create(new PluginContext(cfg.Id, cfg.Name, cfg.Settings.DeepClone().AsObject(), services, logger));
                lock (_gate) _runners[cfg.Id] = new CameraRunner(this, cfg, plugin, camera);
            }
            catch (Exception ex)
            {
                errors.Add($"Camera '{cfg.Name}' ({cfg.Id}): {plugin.PluginId} failed to start: {ex.Message}");
                _logger.LogError(ex, "Camera {CameraName} ({CameraId}) could not be created", cfg.Name, cfg.Id);
            }
        }
    }

    /// <summary>Handles a server CaptureFrame request.</summary>
    public async Task CaptureOnRequestAsync(CaptureFrameRequest request, CancellationToken ct)
    {
        CameraRunner? runner;
        lock (_gate) _runners.TryGetValue(request.CameraId, out runner);
        if (runner is null)
        {
            _reporter.Event(EventSeverities.Warning, EventCategories.Camera, $"Capture requested for unknown or disabled camera {request.CameraId}", cameraId: request.CameraId);
            return;
        }
        var (width, height) = RequestedSize(request.Width, request.Height, runner.Config.CaptureWidth, runner.Config.CaptureHeight);
        await runner.CaptureAndUploadAsync(width, height, request.RequestId, ct);
    }

    /// <summary>Fills a missing requested dimension from the configured aspect ratio, so a width-only request is not cropped.</summary>
    internal static (int Width, int Height) RequestedSize(int? width, int? height, int configWidth, int configHeight)
    {
        if (width is > 0 && height is > 0) return (width.Value, height.Value);
        if (configWidth <= 0 || configHeight <= 0) return (width ?? configWidth, height ?? configHeight);
        if (width is > 0) return (width.Value, Math.Max(1, (int)Math.Round(width.Value * (double)configHeight / configWidth)));
        if (height is > 0) return (Math.Max(1, (int)Math.Round(height.Value * (double)configWidth / configHeight)), height.Value);
        return (configWidth, configHeight);
    }

    private static bool Same(CameraConfig a, CameraConfig b) =>
        JsonSerializer.Serialize(a, ClientJson.Options) == JsonSerializer.Serialize(b, ClientJson.Options);

    public async ValueTask DisposeAsync()
    {
        List<CameraRunner> all;
        lock (_gate)
        {
            all = _runners.Values.ToList();
            _runners.Clear();
        }
        foreach (var r in all) await r.DisposeAsync();
    }

    private sealed class CameraRunner : IAsyncDisposable
    {
        private readonly CameraManager _owner;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _captureGate = new(1, 1);
        private readonly Task _loop;

        public CameraRunner(CameraManager owner, CameraConfig config, ICameraPlugin plugin, ICamera camera)
        {
            _owner = owner;
            Config = config;
            Plugin = plugin;
            Camera = camera;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public CameraConfig Config { get; }
        public ICameraPlugin Plugin { get; }
        public ICamera Camera { get; }

        private async Task RunAsync(CancellationToken ct)
        {
            if (Config.FrameIntervalSec <= 0) return;
            var interval = TimeSpan.FromSeconds(Config.FrameIntervalSec);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 1500)), _owner._time, ct);
                while (!ct.IsCancellationRequested)
                {
                    if (_owner._link.IsConnected) await CaptureAndUploadAsync(Config.CaptureWidth, Config.CaptureHeight, null, ct);
                    await Task.Delay(interval, _owner._time, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        public async Task CaptureAndUploadAsync(int? width, int? height, Guid? requestId, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(60));
            await _captureGate.WaitAsync(linked.Token);
            try
            {
                var frame = await Camera.CaptureAsync(width is > 0 ? width : null, height is > 0 ? height : null, linked.Token);
                var server = _owner._link.ServerUrl ?? throw new InvalidOperationException("no server configured");
                var token = _owner._link.DeviceToken ?? throw new InvalidOperationException("device is not approved yet");
                await _owner._http.UploadFrameAsync(server, token, Config.Id, frame, requestId, linked.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning("Camera {CameraName}: capture/upload failed: {Error}", Config.Name, ex.Message);
                _owner._reporter.EventThrottled($"camera:{Config.Id}", TimeSpan.FromMinutes(10), EventSeverities.Warning, EventCategories.Camera,
                    $"Camera '{Config.Name}' capture/upload failed: {ex.Message}", cameraId: Config.Id);
            }
            finally
            {
                _captureGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                await Camera.DisposeAsync();
            }
            catch (Exception)
            {
                // Plugin dispose failures must not block reconfiguration.
            }
        }
    }
}

/// <summary>HTTP calls to the server outside the hub: registration and frame uploads.</summary>
public sealed class ServerHttp : IDisposable
{
    private readonly HttpClient _client;

    public ServerHttp()
    {
        _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SlopGardenClient", ClientInfo.Version));
    }

    public HttpClient Client => _client;

    public async Task UploadFrameAsync(Uri server, string token, Guid cameraId, CameraFrame frame, Guid? requestId, CancellationToken ct)
    {
        var query = $"?width={frame.Width}&height={frame.Height}" + (requestId is { } r ? $"&requestId={r}" : "");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, $"api/device-api/cameras/{cameraId}/frames{query}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(frame.Data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(frame.ContentType) ? "image/jpeg" : frame.ContentType);
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"frame upload returned {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }

    public async Task UploadLogAsync(Uri server, string token, string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/device-api/logs"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain");
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"log upload returned {(int)response.StatusCode}", null, response.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}
