using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai.Tools;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record LatestReading(Guid SensorId, string SensorName, Guid ChannelId, string Metric, string Unit, double? Value, DateTimeOffset? At, short? Quality, double? AlertLow, double? AlertHigh);

public sealed record ActuatorBrief(Guid Id, string Name, string Kind, bool IsOn, DateTimeOffset? StateSince, bool Enabled, string TargetType, Guid? BedId, Guid? PlantId, Guid DeviceId);

public sealed record CameraBrief(Guid Id, string Name, string TargetType, Guid? BedId, Guid? PlantId, DateTimeOffset? LastFrameAt, string SnapshotUrl);

public sealed record CareRange(string Metric, double? Min, double? Max, string Source);

public sealed record PlantOverview(PlantDto Plant, IReadOnlyList<LatestReading> Readings, IReadOnlyList<CareRange> Targets, bool AiActive);

public sealed record BedOverview(BedDto Bed, IReadOnlyList<PlantOverview> Plants, IReadOnlyList<LatestReading> Readings, bool AiActive);

public sealed record DeviceSummary(int Total, int Online, int Pending, IReadOnlyList<DeviceBrief> Devices);

public sealed record DeviceBrief(Guid Id, string Name, string Status, bool IsOnline, DateTimeOffset? LastSeenAt, int UnsignedPlugins);

public sealed record GardenOverview(
    GardenDto Garden,
    EnvironmentContext Environment,
    IReadOnlyList<BedOverview> Beds,
    IReadOnlyList<LatestReading> GardenReadings,
    IReadOnlyList<ActuatorBrief> Actuators,
    IReadOnlyList<CameraBrief> Cameras,
    DeviceSummary Devices,
    IReadOnlyList<EmergencyStop> ActiveEmergencyStops,
    int PendingProposals,
    IReadOnlyList<AiActivity> RunningAiActivities,
    int UnreadNotifications);

public sealed class OverviewService(SlopDbContext db, EnvironmentService env)
{
    public async Task<GardenOverview> BuildAsync(Guid? gardenId, CancellationToken ct)
    {
        var garden = await GardenTools.ResolveGardenAsync(db, gardenId, ct);
        var beds = await db.Beds.AsNoTracking().Where(b => b.GardenId == garden.Id && b.ArchivedAt == null).OrderBy(b => b.LayoutY).ThenBy(b => b.LayoutX).ThenBy(b => b.Name).ToListAsync(ct);
        var bedIds = beds.Select(b => b.Id).ToList();
        var plants = await db.Plants.AsNoTracking().Include(p => p.PlantProfile).Where(p => bedIds.Contains(p.BedId) && p.RemovedAt == null).OrderBy(p => p.Name).ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking().Include(s => s.Channels).Where(s => s.GardenId == garden.Id).ToListAsync(ct);
        var actuators = await db.Actuators.AsNoTracking().Where(a => a.GardenId == garden.Id).OrderBy(a => a.Name).ToListAsync(ct);
        var cameras = await db.Cameras.AsNoTracking().Where(c => c.GardenId == garden.Id).OrderBy(c => c.Name).ToListAsync(ct);
        var plantIds = plants.Select(p => p.Id).ToList();
        var targets = await db.CareTargets.AsNoTracking().Where(t => (t.PlantId != null && plantIds.Contains(t.PlantId.Value)) || (t.BedId != null && bedIds.Contains(t.BedId.Value) && t.ScopeType == "bed")).ToListAsync(ct);
        var running = await db.AiActivities.AsNoTracking().Where(a => a.Status == "running" && (a.GardenId == garden.Id || a.GardenId == null)).OrderByDescending(a => a.StartedAt).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        var unsigned = await db.DevicePlugins.AsNoTracking().Where(p => p.Trust == PluginTrust.Unsigned || p.Trust == PluginTrust.Invalid)
            .GroupBy(p => p.DeviceId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var stops = await db.EmergencyStops.AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        var pending = await db.AiProposals.CountAsync(p => p.Status == ProposalStatuses.Pending, ct);
        var unread = await db.Notifications.CountAsync(n => n.ReadAt == null, ct);

        // Actuators/sensors targeting a plant also count for its bed when deciding "AI acting".
        var plantBed = plants.ToDictionary(p => p.Id, p => p.BedId);
        var actuatorTarget = actuators.ToDictionary(a => a.Id, a => (a.BedId, a.PlantId));
        bool Acting(string type, Guid id) => running.Any(a =>
            (a.TargetType == type && a.TargetId == id) ||
            (type == "bed" && a.TargetType == "plant" && a.TargetId is { } pid && plantBed.GetValueOrDefault(pid) == id) ||
            (a.TargetType == "actuator" && a.TargetId is { } aid && actuatorTarget.TryGetValue(aid, out var t) &&
                ((type == "plant" && t.PlantId == id) || (type == "bed" && (t.BedId == id || (t.PlantId is { } tp && plantBed.GetValueOrDefault(tp) == id))))));

        List<LatestReading> ReadingsFor(Func<Sensor, bool> pred) => sensors.Where(pred)
            .SelectMany(s => s.Channels.Select(c => new LatestReading(s.Id, s.Name, c.Id, c.Metric, c.Unit, c.LastValue, c.LastValueAt, c.LastQuality, c.AlertLow, c.AlertHigh)))
            .OrderBy(r => r.Metric).ToList();

        var bedOverviews = beds.Select(b =>
        {
            var bp = plants.Where(p => p.BedId == b.Id).Select(p =>
            {
                var tg = targets.Where(t => t.PlantId == p.Id).Select(t => new CareRange(t.Metric, t.MinValue, t.MaxValue, t.Source)).ToList();
                if (p.PlantProfile is { } prof && tg.All(t => t.Metric != Metrics.SoilMoisturePct) && prof.SoilMoistureMinPct is not null)
                    tg.Add(new CareRange(Metrics.SoilMoisturePct, prof.SoilMoistureMinPct, prof.SoilMoistureMaxPct, "profile"));
                if (p.PlantProfile is { } prof2 && tg.All(t => t.Metric != Metrics.AirTemperatureC) && prof2.AirTempMinC is not null)
                    tg.Add(new CareRange(Metrics.AirTemperatureC, prof2.AirTempMinC, prof2.AirTempMaxC, "profile"));
                return new PlantOverview(PlantDto.From(p, garden.Id), ReadingsFor(s => s.PlantId == p.Id), tg, Acting("plant", p.Id));
            }).ToList();
            return new BedOverview(BedDto.From(b, bp.Count), bp, ReadingsFor(s => s.BedId == b.Id && s.PlantId == null), Acting("bed", b.Id));
        }).ToList();

        return new GardenOverview(
            GardenDto.From(garden),
            env.For(garden),
            bedOverviews,
            ReadingsFor(s => s.BedId == null && s.PlantId == null),
            actuators.Select(a => new ActuatorBrief(a.Id, a.Name, a.Kind, a.IsOn, a.StateSince, a.Enabled, a.TargetType, a.BedId, a.PlantId, a.DeviceId)).ToList(),
            cameras.Select(c => new CameraBrief(c.Id, c.Name, c.TargetType, c.BedId, c.PlantId, c.LastFrameAt, $"/api/cameras/{c.Id}/snapshot")).ToList(),
            new DeviceSummary(devices.Count, devices.Count(d => d.IsOnline), devices.Count(d => d.Status == DeviceStatuses.Pending),
                devices.Select(d => new DeviceBrief(d.Id, d.Name, d.Status, d.IsOnline, d.LastSeenAt, unsigned.GetValueOrDefault(d.Id))).ToList()),
            stops,
            pending,
            running,
            unread);
    }
}
