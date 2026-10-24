using System.Text.Json;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Server;
using SlopGarden.Client.Storage;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Client.Sensors;

/// <summary>Creates sensors from config and samples each one on its own interval.</summary>
public sealed class SensorManager(
    PluginRegistry registry,
    Outbox outbox,
    IChannelValues values,
    IDeviceReporter reporter,
    IServiceProvider services,
    TimeProvider time,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private static readonly TimeSpan ErrorEventInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<SensorManager>();
    private readonly Dictionary<Guid, SensorRunner> _runners = [];

    internal TimeProvider Time { get; } = time;

    public int ActiveCount => _runners.Count;

    public async Task ApplyAsync(IReadOnlyList<SensorConfig> sensors, List<string> errors)
    {
        var desired = sensors.Where(s => s.Enabled).ToDictionary(s => s.Id);
        foreach (var (id, runner) in _runners.ToList())
        {
            var plugin = desired.TryGetValue(id, out var cfg) ? registry.FindSensor(cfg.PluginId) : null;
            if (cfg is not null && plugin is not null && ReferenceEquals(plugin, runner.Plugin) && Same(cfg, runner.Config)) continue;
            _runners.Remove(id);
            await runner.DisposeAsync();
        }
        foreach (var cfg in desired.Values)
        {
            if (_runners.ContainsKey(cfg.Id)) continue;
            var plugin = registry.FindSensor(cfg.PluginId);
            if (plugin is null)
            {
                errors.Add($"Sensor '{cfg.Name}' ({cfg.Id}): unknown or unavailable plugin '{cfg.PluginId}'.");
                continue;
            }
            try
            {
                var logger = loggerFactory.CreateLogger($"SlopGarden.Plugin.{plugin.PluginId}");
                var sensor = plugin.Create(new PluginContext(cfg.Id, cfg.Name, cfg.Settings.DeepClone().AsObject(), services, logger));
                _runners[cfg.Id] = new SensorRunner(this, cfg, plugin, sensor);
            }
            catch (Exception ex)
            {
                errors.Add($"Sensor '{cfg.Name}' ({cfg.Id}): {plugin.PluginId} failed to start: {ex.Message}");
                _logger.LogError(ex, "Sensor {SensorName} ({SensorId}) could not be created", cfg.Name, cfg.Id);
            }
        }
    }

    private static bool Same(SensorConfig a, SensorConfig b) =>
        JsonSerializer.Serialize(a, ClientJson.Options) == JsonSerializer.Serialize(b, ClientJson.Options);

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _runners.Values) await r.DisposeAsync();
        _runners.Clear();
    }

    internal async Task SampleAsync(SensorRunner runner, CancellationToken ct)
    {
        var cfg = runner.Config;
        IReadOnlyList<RawReading> raw;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, cfg.ReadIntervalSec)));
            raw = await runner.Sensor.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "read timed out" : ex.Message;
            ReportError(cfg, $"Sensor '{cfg.Name}' read failed: {message}", ex);
            return;
        }

        var now = Time.GetUtcNow();
        var samples = new List<ReadingSample>(raw.Count);
        foreach (var r in raw)
        {
            var channel = cfg.Channels.FirstOrDefault(c => c.Key == r.ChannelKey);
            if (channel is null) continue;
            try
            {
                var (value, quality) = Calibration.Process(channel, r.Value, r.Quality);
                if (quality == ReadingQuality.Error) throw new InvalidOperationException($"value {r.Value} is not a finite number after calibration");
                samples.Add(new ReadingSample(channel.Id, now, value, Calibration.IsIdentity(channel.Calibration) ? null : r.Value, quality));
                values.Set(channel.Id, new ChannelValue(value, now, quality));
            }
            catch (Exception ex)
            {
                ReportError(cfg, $"Sensor '{cfg.Name}' channel '{channel.Key}': {ex.Message}", ex);
            }
        }
        var unknown = raw.Select(r => r.ChannelKey).Except(cfg.Channels.Select(c => c.Key)).ToList();
        if (unknown.Count > 0 && cfg.Channels.Count > 0)
        {
            reporter.EventThrottled($"sensor-unmapped:{cfg.Id}", TimeSpan.FromHours(1), EventSeverities.Info, EventCategories.Sensor,
                $"Sensor '{cfg.Name}' returned channels without configuration: {string.Join(", ", unknown)}", sensorId: cfg.Id);
        }
        if (samples.Count > 0) outbox.AppendReadings(samples);
    }

    private void ReportError(SensorConfig cfg, string message, Exception ex)
    {
        _logger.LogWarning("{Message}", message);
        _logger.LogDebug(ex, "Sensor {SensorId} error details", cfg.Id);
        reporter.EventThrottled($"sensor-error:{cfg.Id}", ErrorEventInterval, EventSeverities.Warning, EventCategories.Sensor, message, sensorId: cfg.Id);
    }

    internal sealed class SensorRunner : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public SensorRunner(SensorManager owner, SensorConfig config, ISensorPlugin plugin, ISensor sensor)
        {
            Config = config;
            Plugin = plugin;
            Sensor = sensor;
            _loop = Task.Run(() => RunAsync(owner, _cts.Token));
        }

        public SensorConfig Config { get; }
        public ISensorPlugin Plugin { get; }
        public ISensor Sensor { get; }

        private async Task RunAsync(SensorManager owner, CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, Config.ReadIntervalSec));
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Min(1000, interval.TotalMilliseconds / 4))), owner.Time, ct);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await owner.SampleAsync(this, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        owner._logger.LogError(ex, "Sampling sensor {SensorId} failed", Config.Id);
                    }
                    var jitter = interval.TotalMilliseconds * (Random.Shared.NextDouble() - 0.5) * 0.1;
                    await Task.Delay(interval + TimeSpan.FromMilliseconds(jitter), owner.Time, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
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
                await Sensor.DisposeAsync();
            }
            catch (Exception)
            {
                // A misbehaving plugin must not prevent reconfiguration.
            }
            _cts.Dispose();
        }
    }
}
