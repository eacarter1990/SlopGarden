using System.Text.Json;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Cameras;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Scheduling;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Server;
using SlopGarden.Client.Storage;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Runtime;

/// <summary>Persists the last desired configuration (config.json) so the device runs offline after a restart.</summary>
public sealed class ConfigStore(ClientPaths paths, ILogger<ConfigStore> logger)
{
    public DeviceConfigDocument? Load()
    {
        if (!File.Exists(paths.ConfigFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeviceConfigDocument>(File.ReadAllText(paths.ConfigFile), ClientJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "Cached configuration {ConfigFile} is unreadable; starting without configuration", paths.ConfigFile);
            return null;
        }
    }

    public void Save(DeviceConfigDocument doc)
    {
        try
        {
            ClientJson.WriteFileAtomic(paths.ConfigFile, JsonSerializer.Serialize(doc, ClientJson.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not save configuration to {ConfigFile}", paths.ConfigFile);
        }
    }
}

/// <summary>Applies <see cref="DeviceConfigDocument"/>s: diffs and (re)creates components, persists, and reports the result.</summary>
public sealed class DeviceRuntime(
    ConfigStore store,
    PluginRegistry plugins,
    SensorManager sensors,
    ActuatorController actuators,
    CameraManager cameras,
    LocalScheduler scheduler,
    Outbox outbox,
    IDeviceReporter reporter,
    ILogger<DeviceRuntime> logger) : IHostedService
{
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private volatile DeviceConfigDocument? _current;

    public DeviceConfigDocument? Current => _current;

    public int? AppliedVersion => _current?.Version;

    public ClientSettings Settings => _current?.Settings ?? ClientSettings.Default;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cached = store.Load();
        plugins.Load(cached?.TrustedPluginKeys);
        foreach (var p in plugins.Packages.Where(p => p.TrustResult.Trust == PluginTrust.Invalid))
        {
            reporter.Event(EventSeverities.Error, EventCategories.Plugin,
                $"Plugin '{p.Manifest.Id}' {p.Manifest.Version} refused: {p.TrustResult.Problem}");
        }
        if (cached is null)
        {
            logger.LogInformation("No cached configuration yet; waiting for the server");
            return;
        }
        logger.LogInformation("Applying cached configuration version {Version} (offline-capable)", cached.Version);
        await ApplyCoreAsync(cached, persist: false, report: false);
    }

    public Task<ConfigAppliedReport> ApplyAsync(DeviceConfigDocument doc) => ApplyCoreAsync(doc, persist: true, report: true);

    private async Task<ConfigAppliedReport> ApplyCoreAsync(DeviceConfigDocument doc, bool persist, bool report)
    {
        await _applyGate.WaitAsync();
        try
        {
            var errors = new List<string>();
            plugins.UpdateTrustedKeys(doc.TrustedPluginKeys ?? []);
            if (TimeZones.Find(doc.TimeZoneId) == TimeZoneInfo.Utc && doc.TimeZoneId is not ("UTC" or "Etc/UTC" or ""))
                errors.Add($"Unknown time zone '{doc.TimeZoneId}'; using UTC.");
            outbox.MaxRows = doc.Settings?.OfflineBufferMaxRows is > 0 ? doc.Settings.OfflineBufferMaxRows : Outbox.DefaultMaxRows;
            await Step("sensors", () => sensors.ApplyAsync(doc.Sensors ?? [], errors), errors);
            await Step("actuators", () => actuators.ApplyAsync(doc, errors), errors);
            await Step("cameras", () => cameras.ApplyAsync(doc.Cameras ?? [], errors), errors);
            await Step("schedules", () => { scheduler.Update(doc.Schedules ?? [], errors); return Task.CompletedTask; }, errors);
            _current = doc;
            if (persist) store.Save(doc);
            var result = new ConfigAppliedReport(doc.Version, errors.Count == 0, errors);
            if (errors.Count == 0) logger.LogInformation("Configuration version {Version} applied", doc.Version);
            else logger.LogWarning("Configuration version {Version} applied with {Count} error(s): {Errors}", doc.Version, errors.Count, string.Join(" | ", errors));
            if (report) reporter.ConfigApplied(result);
            return result;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task Step(string name, Func<Task> action, List<string> errors)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying {Section} failed", name);
            errors.Add($"Applying {name} failed: {ex.Message}");
        }
    }

    public async Task SetEmergencyStopAsync(EmergencyStopState state)
    {
        logger.Log(state.DeviceStopped || state.StoppedActuatorIds.Count > 0 ? LogLevel.Warning : LogLevel.Information,
            "Emergency stop update: device stopped={DeviceStopped}, actuators={Count}, reason={Reason}", state.DeviceStopped, state.StoppedActuatorIds.Count, state.Reason);
        await actuators.SetEmergencyStopAsync(state);
        await _applyGate.WaitAsync();
        try
        {
            if (_current is { } doc)
            {
                _current = doc with { EmergencyStop = state };
                store.Save(_current);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await actuators.ShutdownAsync();
        await sensors.DisposeAsync();
        await cameras.DisposeAsync();
    }
}
