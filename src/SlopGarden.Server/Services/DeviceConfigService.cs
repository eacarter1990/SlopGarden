using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;

namespace SlopGarden.Server.Services;

/// <summary>Tracks which SignalR connection belongs to which device.</summary>
public sealed class DeviceConnections
{
    private readonly ConcurrentDictionary<Guid, string> _byDevice = new();

    public void Set(Guid deviceId, string connectionId) => _byDevice[deviceId] = connectionId;

    public void Remove(Guid deviceId, string connectionId) =>
        _byDevice.TryRemove(new KeyValuePair<Guid, string>(deviceId, connectionId));

    public string? Get(Guid deviceId) => _byDevice.TryGetValue(deviceId, out var c) ? c : null;

    public bool IsConnected(Guid deviceId) => _byDevice.ContainsKey(deviceId);
}

/// <summary>Queue of devices whose desired config must be regenerated and pushed (debounced).</summary>
public sealed class ConfigPushQueue
{
    private readonly Channel<Guid?> _channel = Channel.CreateUnbounded<Guid?>();

    /// <summary>Mark one device dirty, or all devices when <paramref name="deviceId"/> is null.</summary>
    public void Dirty(Guid? deviceId = null) => _channel.Writer.TryWrite(deviceId);

    public ChannelReader<Guid?> Reader => _channel.Reader;
}

public sealed class DeviceConfigService(SlopDbContext db, SafetyService safety, DeviceConnections connections, IHubContext<DeviceHub> hub)
{
    private static readonly JsonSerializerOptions Json = SlopJson.Create();

    public async Task<DeviceConfigDocument> BuildAsync(Device device, int version, CancellationToken ct)
    {
        var sensors = await db.Sensors.AsNoTracking().Include(s => s.Channels).Where(s => s.DeviceId == device.Id).OrderBy(s => s.Name).ToListAsync(ct);
        var actuators = await db.Actuators.AsNoTracking().Where(a => a.DeviceId == device.Id).OrderBy(a => a.Name).ToListAsync(ct);
        var cameras = await db.Cameras.AsNoTracking().Where(c => c.DeviceId == device.Id).OrderBy(c => c.Name).ToListAsync(ct);
        var actuatorIds = actuators.Select(a => a.Id).ToList();
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => s.Kind == ScheduleKinds.Actuator && s.ActuatorId != null && actuatorIds.Contains(s.ActuatorId.Value))
            .OrderBy(s => s.Name).ToListAsync(ct);
        var keys = await db.PluginSigningKeys.AsNoTracking().OrderBy(k => k.KeyId).ToListAsync(ct);

        var actuatorConfigs = new List<ActuatorConfig>();
        var stopped = new List<Guid>();
        var deviceStopped = false;
        string? stopReason = null;
        foreach (var a in actuators)
        {
            var s = await safety.ResolveAsync(a, ct);
            var placement = await safety.ResolvePlacementAsync(a, ct);
            var flowWatch = a.FlowChannelId is { } fc
                ? new FlowWatch(fc, a.FlowMinWhenOn ?? 0.2, a.FlowGraceSec ?? 20, a.FlowMaxWhenOff, a.StopOnStall)
                : null;
            actuatorConfigs.Add(new ActuatorConfig(a.Id, a.Name, a.Kind, a.PluginId, a.Settings, a.Enabled, a.FailsafeState, a.FlowRateMlPerMin, s,
                a.Role, placement.ReservoirId, a.ContinuousRun, flowWatch));
            var stops = await safety.ActiveStopsForAsync(a, ct);
            if (stops.Count > 0)
            {
                stopped.Add(a.Id);
                stopReason ??= stops[0].Reason;
                if (stops.Any(x => x.ScopeType is Data.ScopeTypes.Global || (x.ScopeType == Data.ScopeTypes.Device && x.ScopeId == device.Id)))
                    deviceStopped = true;
            }
        }
        if (!deviceStopped)
        {
            var devStop = await db.EmergencyStops.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Active && (x.ScopeType == Data.ScopeTypes.Global || (x.ScopeType == Data.ScopeTypes.Device && x.ScopeId == device.Id)), ct);
            if (devStop is not null)
            {
                deviceStopped = true;
                stopReason ??= devStop.Reason;
            }
        }

        var tz = device.GardenId is { } gid
            ? await db.Gardens.Where(g => g.Id == gid).Select(g => g.TimezoneId).FirstOrDefaultAsync(ct)
            : null;

        return new DeviceConfigDocument(
            version,
            device.Id,
            device.Name,
            DateTimeOffset.UtcNow,
            tz ?? device.ReportedTimezoneId ?? "UTC",
            sensors.Select(s => new SensorConfig(s.Id, s.Name, s.PluginId, s.Settings, s.ReadIntervalSec, s.Enabled,
                s.Channels.OrderBy(c => c.Key).Select(c => new ChannelConfig(c.Id, c.Key, c.Metric, c.Unit, c.Calibration, c.MinValid, c.MaxValid)).ToList())).ToList(),
            actuatorConfigs,
            cameras.Select(c => new CameraConfig(c.Id, c.Name, c.PluginId, c.Settings, c.CaptureWidth, c.CaptureHeight, c.FrameIntervalSec, c.Enabled)).ToList(),
            schedules.Select(s => new ScheduleConfig(s.Id, s.Name, s.ActuatorId!.Value, s.Cron, s.TimezoneId, s.DurationSec ?? 60, s.AmountMl, s.Enabled)).ToList(),
            new EmergencyStopState(deviceStopped, stopped, stopReason),
            ClientSettings.Default,
            keys.Select(k => new TrustedPluginKey(k.KeyId, k.Publisher, k.PublicKeySpki, k.Status != "active")).ToList());
    }

    private static string Fingerprint(DeviceConfigDocument d) =>
        JsonSerializer.Serialize(d with { Version = 0, GeneratedAt = DateTimeOffset.UnixEpoch }, Json);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SyncLocks = new();

    /// <summary>Regenerates the document; stores a new revision if it changed; pushes it if the device is connected.
    /// Serialized per device: a reconnect (Hello) and the background push worker may sync at the same moment.</summary>
    public async Task<DeviceConfigDocument?> SyncAsync(Guid deviceId, bool forcePush, CancellationToken ct)
    {
        var gate = SyncLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await SyncCoreAsync(deviceId, forcePush, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DeviceConfigDocument?> SyncCoreAsync(Guid deviceId, bool forcePush, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || device.Status != DeviceStatuses.Approved) return null;

        var last = await db.DeviceConfigRevisions.AsNoTracking().Where(r => r.DeviceId == deviceId).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        var candidate = await BuildAsync(device, (last?.Version ?? 0) + 1, ct);
        DeviceConfigDocument doc = candidate;
        var changed = true;
        if (last is not null)
        {
            var previous = last.Document.Deserialize<DeviceConfigDocument>(Json);
            if (previous is not null && Fingerprint(previous) == Fingerprint(candidate))
            {
                changed = false;
                doc = previous;
            }
        }

        if (changed)
        {
            db.DeviceConfigRevisions.Add(new DeviceConfigRevision
            {
                DeviceId = deviceId,
                Version = doc.Version,
                Document = JsonSerializer.SerializeToNode(doc, Json)!.AsObject(),
            });
            device.DesiredConfigVersion = doc.Version;
            await db.SaveChangesAsync(ct);
        }

        if ((changed || forcePush) && connections.Get(deviceId) is { } conn)
            await hub.Clients.Client(conn).SendAsync(DeviceHubMethods.ApplyConfig, doc, ct);
        return doc;
    }
}

/// <summary>Drains <see cref="ConfigPushQueue"/> with a short debounce so bursts of edits produce one push.</summary>
public sealed class ConfigPushWorker(ConfigPushQueue queue, IServiceScopeFactory scopes, ILogger<ConfigPushWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var first = await queue.Reader.ReadAsync(stoppingToken);
            await Task.Delay(300, stoppingToken);
            var batch = new HashSet<Guid?> { first };
            while (queue.Reader.TryRead(out var more)) batch.Add(more);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
                var svc = scope.ServiceProvider.GetRequiredService<DeviceConfigService>();
                var ids = batch.Contains(null)
                    ? await db.Devices.Where(d => d.Status == DeviceStatuses.Approved).Select(d => d.Id).ToListAsync(stoppingToken)
                    : batch.Select(x => x!.Value).ToList();
                foreach (var id in ids) await svc.SyncAsync(id, false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Config push failed");
            }
        }
    }
}
