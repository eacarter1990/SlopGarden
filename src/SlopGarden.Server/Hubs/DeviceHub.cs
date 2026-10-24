using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Hubs;

/// <summary>Device-facing hub. Every connection is an approved client authenticated with its device token.</summary>
[Authorize(Policy = Policies.Device)]
public sealed class DeviceHub(
    SlopDbContext db,
    DeviceConnections connections,
    DeviceConfigService configs,
    DeviceRegistry registry,
    CommandService commands,
    NotificationService notifications,
    LiveNotifier live,
    AiActivityTracker activities,
    ILogger<DeviceHub> log) : Hub
{
    private Guid DeviceId => Guid.Parse(Context.User!.FindFirst(Claims.DeviceId)!.Value);

    public override async Task OnConnectedAsync()
    {
        connections.Set(DeviceId, Context.ConnectionId);
        await SetOnlineAsync(true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(DeviceId, Context.ConnectionId);
        if (!connections.IsConnected(DeviceId)) await SetOnlineAsync(false);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task SetOnlineAsync(bool online)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == DeviceId);
        if (device is null) return;
        var wasOnline = device.IsOnline;
        device.IsOnline = online;
        device.LastSeenAt = DateTimeOffset.UtcNow;
        device.IpAddress = Context.GetHttpContext()?.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? device.IpAddress;
        await db.SaveChangesAsync();
        await live.All(LiveEvents.DeviceStatus, new { deviceId = device.Id, isOnline = online, device.Name });
        if (wasOnline && !online)
        {
            await notifications.CreateAsync(new NotificationRequest(device.GardenId, Severities.Warning, "device",
                $"Device offline: {device.Name}", $"`{device.Name}` disconnected. It keeps running its last approved schedules and safety limits and will sync when it reconnects.",
                "device", device.Id, $"offline:{device.Id}"), CancellationToken.None);
        }
    }

    public async Task Hello(DeviceHello hello)
    {
        var device = await db.Devices.FirstAsync(d => d.Id == DeviceId);
        device.ClientVersion = hello.ClientVersion;
        device.Os = hello.Os;
        device.Architecture = hello.Architecture;
        device.Hostname = hello.Hostname;
        device.AppliedConfigVersion = hello.AppliedConfigVersion;
        device.BufferedReadings = hello.BufferedReadings;
        DeviceRegistry.ApplyEnvironment(device, hello.Environment);
        if (device.GardenId is { } gid && await db.Gardens.FirstOrDefaultAsync(g => g.Id == gid) is { } garden)
            DeviceRegistry.InheritEnvironment(garden, device);
        var release = await ClientUpdateService.ReadAsync(db, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(release.Version) && !ClientVersions.IsOutdated(device.ClientVersion, release.Version))
            device.UpdateStatus = "current";
        await db.SaveChangesAsync();
        await registry.UpsertPluginsAsync(device.Id, hello.Plugins, CancellationToken.None);
        await configs.SyncAsync(device.Id, forcePush: true, CancellationToken.None);
        await commands.FlushPendingAsync(device.Id, CancellationToken.None);
        await Clients.Caller.SendAsync(DeviceHubMethods.RequestDiscovery);
    }

    public async Task ReportUpdate(UpdateResultReport report)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == DeviceId);
        if (device is null) return;
        device.UpdateStatus = report.Status;
        device.UpdateError = report.Error;
        await db.SaveChangesAsync();
        await live.All(LiveEvents.DeviceStatus, new { deviceId = device.Id, device.UpdateStatus, device.ClientVersion });
        if (report.Status == "failed")
        {
            await notifications.CreateAsync(new NotificationRequest(device.GardenId, Severities.Warning, "device",
                $"Update failed: {device.Name}", report.Error ?? "The client could not apply the image.", "device", device.Id), CancellationToken.None);
        }
    }

    public async Task<ReadingBatchAck> ReportReadings(ReadingBatch batch)
    {
        if (batch.Readings.Count == 0) return new ReadingBatchAck(batch.BatchId, 0);
        var channelIds = batch.Readings.Select(r => r.ChannelId).Distinct().ToList();
        var channels = await db.SensorChannels.Include(c => c.Sensor)
            .Where(c => channelIds.Contains(c.Id) && c.Sensor!.DeviceId == DeviceId)
            .ToDictionaryAsync(c => c.Id);
        var valid = batch.Readings.Where(r => channels.ContainsKey(r.ChannelId) && double.IsFinite(r.Value)).ToList();

        if (valid.Count > 0)
        {
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO readings (channel_id, time, value, raw_value, quality) " +
                "SELECT * FROM unnest(@c, @t, @v, @r, @q) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = valid.Select(r => r.ChannelId).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = valid.Select(r => r.Time.UtcDateTime).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = valid.Select(r => r.Value).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = valid.Select(r => r.RawValue ?? (object)DBNull.Value).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = valid.Select(r => r.Quality).ToArray() });
            await cmd.ExecuteNonQueryAsync();
        }

        var latest = valid.GroupBy(r => r.ChannelId).Select(g => g.MaxBy(r => r.Time)!).ToList();
        foreach (var r in latest)
        {
            var c = channels[r.ChannelId];
            if (c.LastValueAt is null || r.Time >= c.LastValueAt)
            {
                c.LastValue = r.Value;
                c.LastRawValue = r.RawValue;
                c.LastQuality = r.Quality;
                c.LastValueAt = r.Time;
            }
            if (c.Sensor is { } s && s.Status != "ok" && r.Quality == ReadingQuality.Good)
            {
                s.Status = "ok";
                s.LastError = null;
            }
        }
        var device = await db.Devices.FirstAsync(d => d.Id == DeviceId);
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await live.All(LiveEvents.Readings, latest.Select(r => new
        {
            channelId = r.ChannelId,
            sensorId = channels[r.ChannelId].SensorId,
            value = r.Value,
            time = r.Time,
            quality = r.Quality,
        }));

        foreach (var r in latest) await EvaluateAlertsAsync(channels[r.ChannelId], r.Value);
        return new ReadingBatchAck(batch.BatchId, batch.Readings.Count);
    }

    private async Task EvaluateAlertsAsync(SensorChannel c, double value)
    {
        var sensor = c.Sensor!;
        string? which = null;
        if (c.AlertLow is { } lo && value < lo) which = $"below {lo:0.##} {c.Unit}";
        else if (c.AlertHigh is { } hi && value > hi) which = $"above {hi:0.##} {c.Unit}";
        if (which is null) return;
        await notifications.CreateAsync(new NotificationRequest(sensor.GardenId, Severities.Warning, "sensor",
            $"{sensor.Name}: {c.DisplayName ?? c.Metric} {which}",
            $"Current value **{value:0.##} {c.Unit}** ({c.Metric}).", "sensor", sensor.Id, $"alert:{c.Id}:{which}"), CancellationToken.None);
    }

    public async Task ReportActuatorState(ActuatorStateReport report)
    {
        var a = await db.Actuators.FirstOrDefaultAsync(x => x.Id == report.ActuatorId && x.DeviceId == DeviceId);
        if (a is null) return;
        a.IsOn = report.On;
        a.Level = report.Level;
        a.StateSince = report.Since;
        await db.SaveChangesAsync();
        await live.All(LiveEvents.ActuatorState, new { actuatorId = a.Id, isOn = a.IsOn, level = a.Level, since = a.StateSince, reason = report.Reason, commandId = report.CommandId });
    }

    public async Task ReportActuatorRun(ActuatorRunReport run)
    {
        if (!await db.Actuators.AnyAsync(a => a.Id == run.ActuatorId && a.DeviceId == DeviceId)) return;
        if (await db.ActuatorRuns.AnyAsync(r => r.Id == run.RunId)) return;
        db.ActuatorRuns.Add(new ActuatorRun
        {
            Id = run.RunId,
            ActuatorId = run.ActuatorId,
            CommandId = run.CommandId,
            ScheduleId = run.ScheduleId,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            DurationSec = run.DurationSec,
            AmountMl = run.AmountMl,
            Source = run.Source,
            StopReason = run.StopReason,
        });
        await db.SaveChangesAsync();
        await live.Changed("actuator-runs");
    }

    public async Task AckCommand(CommandAck ack)
    {
        var cmd = await db.ActuatorCommands.FirstOrDefaultAsync(c => c.Id == ack.CommandId);
        if (cmd is null) return;
        cmd.Status = ack.Status;
        if (ack.Status is CommandStatuses.Rejected or CommandStatuses.Failed or CommandStatuses.Expired) cmd.BlockedReason = ack.Reason;
        if (ack.Status is CommandStatuses.Completed or CommandStatuses.Rejected or CommandStatuses.Failed or CommandStatuses.Expired)
        {
            cmd.CompletedAt = ack.Time;
            await activities.FinishForCommandAsync(cmd.Id, ack.Status == CommandStatuses.Completed);
        }
        await db.SaveChangesAsync();
        await live.Changed("commands");
    }

    public async Task ReportEvent(DeviceEventReport e)
    {
        var device = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == DeviceId);
        db.DeviceEvents.Add(new DeviceEvent
        {
            DeviceId = DeviceId,
            Time = e.Time,
            Severity = e.Severity,
            Category = e.Category,
            Message = e.Message,
            SensorId = e.SensorId,
            ActuatorId = e.ActuatorId,
            CameraId = e.CameraId,
        });
        if (e.SensorId is { } sid && e.Category == "sensor" && await db.Sensors.FirstOrDefaultAsync(s => s.Id == sid) is { } sensor)
        {
            sensor.Status = "error";
            sensor.LastError = e.Message;
        }
        await db.SaveChangesAsync();
        if (e.Severity is Severities.Warning or Severities.Critical)
        {
            var (targetType, targetId) = e.ActuatorId is { } aid ? ("actuator", aid) : e.SensorId is { } s2 ? ("sensor", s2) : ("device", DeviceId);
            var title = e.Code switch
            {
                SafetyEventCodes.FlowStall => "No flow",
                SafetyEventCodes.UnexpectedFlow => "Unexpected flow",
                SafetyEventCodes.PumpNotRunning => "Pump not running",
                _ => $"{device.Name}: {e.Category} event",
            };
            var dedupe = e.Code is { Length: > 0 } code ? Background.HydroWatchdog.DedupeKey(code, targetId) : $"devevent:{targetId}:{e.Message}";
            await notifications.CreateAsync(new NotificationRequest(device.GardenId, e.Severity, e.Category, title, e.Message, targetType, targetId, dedupe), CancellationToken.None);
        }
    }

    public async Task ReportConfigApplied(ConfigAppliedReport report)
    {
        var device = await db.Devices.FirstAsync(d => d.Id == DeviceId);
        device.AppliedConfigVersion = report.Version;
        device.ConfigErrors = new System.Text.Json.Nodes.JsonArray(report.Errors.Select(e => (System.Text.Json.Nodes.JsonNode)e).ToArray());
        var rev = await db.DeviceConfigRevisions.FirstOrDefaultAsync(r => r.DeviceId == DeviceId && r.Version == report.Version);
        if (rev is not null)
        {
            rev.AppliedAt = DateTimeOffset.UtcNow;
            rev.Success = report.Success;
            rev.Errors = device.ConfigErrors.DeepClone().AsArray();
        }
        await db.SaveChangesAsync();
        if (report.Errors.Count > 0) log.LogWarning("Device {Device} reported config errors: {Errors}", device.Name, string.Join("; ", report.Errors));
        await live.Changed("devices");
    }

    public Task ReportDiscoveredHardware(DiscoveredHardwareReport report) =>
        registry.UpsertDiscoveredAsync(DeviceId, report, CancellationToken.None);
}
