using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Background;

/// <summary>
/// Server-side backstop for the device watchdogs: flow stalls, unexpected flow, continuous pumps that are off,
/// and reservoir changes that are due. Devices enforce and stop pumps themselves; this raises the alarms even
/// when an older client doesn't report them, using the same dedupe keys so nothing is notified twice.
/// </summary>
public sealed class HydroWatchdog(IServiceScopeFactory scopes, DeviceConnections connections, ILogger<HydroWatchdog> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FreshReading = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PumpOffAlarm = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await CheckAsync(scope.ServiceProvider, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Hydro watchdog failed");
            }
        }
    }

    public static string DedupeKey(string code, Guid targetId) => $"{code}:{targetId}";

    public async Task CheckAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var db = sp.GetRequiredService<SlopDbContext>();
        var notifications = sp.GetRequiredService<NotificationService>();
        var watched = await db.Actuators.AsNoTracking().Where(a => a.Enabled && (a.FlowChannelId != null || a.ContinuousRun)).ToListAsync(ct);
        var channelIds = watched.Where(a => a.FlowChannelId != null).Select(a => a.FlowChannelId!.Value).ToList();
        var channels = await db.SensorChannels.AsNoTracking().Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var stops = await db.EmergencyStops.AsNoTracking().AnyAsync(s => s.Active, ct);

        foreach (var a in watched)
        {
            if (!connections.IsConnected(a.DeviceId)) continue;
            var since = a.StateSince ?? now;
            var grace = TimeSpan.FromSeconds(a.FlowGraceSec ?? 20);
            if (a.FlowChannelId is { } fc && channels.TryGetValue(fc, out var ch) && ch.LastValueAt is { } at && now - at <= FreshReading && ch.LastValue is { } flow)
            {
                if (a.IsOn && now - since >= grace && flow < (a.FlowMinWhenOn ?? 0.2))
                {
                    await notifications.CreateAsync(new NotificationRequest(a.GardenId, Severities.Critical, "safety",
                        $"No flow: {a.Name}", $"**{a.Name}** has been on for {(now - since).TotalSeconds:0} s but flow is **{flow:0.##} L/min**. The pump may have stopped, a line may be clogged, or the reservoir may be empty.",
                        "actuator", a.Id, DedupeKey(SafetyEventCodes.FlowStall, a.Id)), ct);
                }
                else if (!a.IsOn && a.FlowMaxWhenOff is { } maxOff && now - since >= grace && flow > maxOff)
                {
                    await notifications.CreateAsync(new NotificationRequest(a.GardenId, Severities.Warning, "safety",
                        $"Unexpected flow: {a.Name}", $"**{a.Name}** is off but flow is **{flow:0.##} L/min**. Check for a leak or a stuck valve.",
                        "actuator", a.Id, DedupeKey(SafetyEventCodes.UnexpectedFlow, a.Id)), ct);
                }
            }
            if (a.ContinuousRun && !a.IsOn && !stops && now - since >= PumpOffAlarm)
            {
                await notifications.CreateAsync(new NotificationRequest(a.GardenId, Severities.Critical, "safety",
                    $"Pump not running: {a.Name}", $"**{a.Name}** should run continuously but has been off for {(now - since).TotalMinutes:0} min. Roots in NFT/DWC/aeroponic systems can dry out or suffocate quickly.",
                    "actuator", a.Id, DedupeKey(SafetyEventCodes.PumpNotRunning, a.Id)), ct);
            }
        }

        var reservoirs = await db.Reservoirs.AsNoTracking().Where(r => r.ArchivedAt == null && r.LastChangedAt != null && r.ChangeIntervalDays != null).ToListAsync(ct);
        foreach (var r in reservoirs)
        {
            var due = r.LastChangedAt!.Value.AddDays(r.ChangeIntervalDays!.Value);
            if (now < due) continue;
            await notifications.CreateAsync(new NotificationRequest(r.GardenId, Severities.Info, "schedule",
                $"Reservoir change due: {r.Name}", $"The nutrient solution in **{r.Name}** was last changed {(now - r.LastChangedAt.Value).TotalDays:0} days ago (interval {r.ChangeIntervalDays} days).",
                "reservoir", r.Id, $"reservoir_change:{r.Id}:{now:yyyy-MM-dd}"), ct);
        }
    }
}
