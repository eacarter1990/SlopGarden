using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai.Tools;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record LatestReading(Guid SensorId, string SensorName, Guid ChannelId, string Metric, string Unit, double? Value, DateTimeOffset? At, short? Quality, double? AlertLow, double? AlertHigh);

public sealed record ActuatorBrief(Guid Id, string Name, string Kind, bool IsOn, DateTimeOffset? StateSince, bool Enabled, string TargetType, Guid? BedId, Guid? PlantId, Guid DeviceId,
    Guid? ReservoirId = null, string? Role = null, bool ContinuousRun = false)
{
    public static ActuatorBrief From(Actuator a) => new(a.Id, a.Name, a.Kind, a.IsOn, a.StateSince, a.Enabled, a.TargetType, a.BedId, a.PlantId, a.DeviceId, a.ReservoirId, a.Role, a.ContinuousRun);
}

public sealed record CameraBrief(Guid Id, string Name, string TargetType, Guid? BedId, Guid? PlantId, DateTimeOffset? LastFrameAt, string SnapshotUrl);

public sealed record CareRange(string Metric, double? Min, double? Max, string Source);

public sealed record PlantOverview(PlantDto Plant, IReadOnlyList<LatestReading> Readings, IReadOnlyList<CareRange> Targets, bool AiActive);

/// <param name="Medium">Effective growing medium (the bed's own, or the garden default).</param>
public sealed record BedOverview(BedDto Bed, IReadOnlyList<PlantOverview> Plants, IReadOnlyList<LatestReading> Readings, bool AiActive, string Medium, string? HydroSystem, Guid? ReservoirId);

public sealed record ReservoirTarget(string Metric, double? Min, double? Max, string Source);
public sealed record BedRef(Guid Id, string Name, string? HydroSystem);
public sealed record DoseTotal(Guid ActuatorId, string Name, string? Role, double AmountMl);

public sealed record ReservoirStatus(
    Guid Id,
    Guid GardenId,
    string Name,
    double? VolumeLiters,
    DateTimeOffset? LastChangedAt,
    int? ChangeIntervalDays,
    double? DaysSinceChange,
    bool ChangeDue,
    double ToppedUpLitersSinceChange,
    string AiContext,
    string Notes,
    IReadOnlyList<LatestReading> Readings,
    IReadOnlyList<ReservoirTarget> Targets,
    IReadOnlyList<ActuatorBrief> Actuators,
    IReadOnlyList<BedRef> Beds,
    IReadOnlyList<DoseTotal> DosedSinceChange,
    IReadOnlyList<string> Alerts,
    bool AiActive);

public sealed record DeviceSummary(int Total, int Online, int Pending, IReadOnlyList<DeviceBrief> Devices);

public sealed record DeviceBrief(Guid Id, string Name, string Status, bool IsOnline, DateTimeOffset? LastSeenAt, int UnsignedPlugins);

public sealed record GardenOverview(
    GardenDto Garden,
    EnvironmentContext Environment,
    IReadOnlyList<BedOverview> Beds,
    IReadOnlyList<ReservoirStatus> Reservoirs,
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
    /// <summary>Defaults used when neither the user nor the AI set a reservoir target. EC comes from the crops it feeds.</summary>
    public static readonly ReservoirTarget DefaultPh = new(Metrics.Ph, 5.5, 6.5, "default");
    public static readonly ReservoirTarget DefaultWaterTemp = new(Metrics.WaterTemperatureC, 18, 22, "default");

    public async Task<List<ReservoirStatus>> ReservoirsAsync(Guid gardenId, Guid? onlyId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var q = db.Reservoirs.AsNoTracking().Where(r => r.GardenId == gardenId && r.ArchivedAt == null);
        if (onlyId is { } oid) q = db.Reservoirs.AsNoTracking().Where(r => r.Id == oid);
        var reservoirs = await q.OrderBy(r => r.Name).ToListAsync(ct);
        if (reservoirs.Count == 0) return [];
        var ids = reservoirs.Select(r => r.Id).ToList();
        var sensors = await db.Sensors.AsNoTracking().Include(s => s.Channels).Where(s => s.ReservoirId != null && ids.Contains(s.ReservoirId.Value)).ToListAsync(ct);
        var actuators = await db.Actuators.AsNoTracking().Where(a => a.ReservoirId != null && ids.Contains(a.ReservoirId.Value)).OrderBy(a => a.Name).ToListAsync(ct);
        var beds = await db.Beds.AsNoTracking().Where(b => b.ReservoirId != null && ids.Contains(b.ReservoirId.Value) && b.ArchivedAt == null).ToListAsync(ct);
        var bedIds = beds.Select(b => b.Id).ToList();
        var profiles = await db.Plants.AsNoTracking().Include(p => p.PlantProfile)
            .Where(p => bedIds.Contains(p.BedId) && p.RemovedAt == null && p.PlantProfile != null)
            .Select(p => new { p.BedId, p.PlantProfile!.EcMin, p.PlantProfile.EcMax }).ToListAsync(ct);
        var targets = await db.CareTargets.AsNoTracking().Where(t => t.ReservoirId != null && ids.Contains(t.ReservoirId.Value)).ToListAsync(ct);
        var running = await db.AiActivities.AsNoTracking().Where(a => a.Status == "running" && a.TargetId != null).Select(a => new { a.TargetType, a.TargetId }).ToListAsync(ct);
        var doserIds = actuators.Where(a => ActuatorRoles.IsDosing(a.Role)).Select(a => a.Id).ToList();
        var minChange = reservoirs.Min(r => r.LastChangedAt ?? DateTimeOffset.MinValue);
        var doseRuns = await db.ActuatorRuns.AsNoTracking().Where(r => doserIds.Contains(r.ActuatorId) && r.EndedAt >= minChange).ToListAsync(ct);

        var result = new List<ReservoirStatus>();
        foreach (var r in reservoirs)
        {
            var readings = sensors.Where(s => s.ReservoirId == r.Id)
                .SelectMany(s => s.Channels.Select(c => new LatestReading(s.Id, s.Name, c.Id, c.Metric, c.Unit, c.LastValue, c.LastValueAt, c.LastQuality, c.AlertLow, c.AlertHigh)))
                .OrderBy(x => x.Metric).ToList();
            var acts = actuators.Where(a => a.ReservoirId == r.Id).ToList();
            var fed = beds.Where(b => b.ReservoirId == r.Id).ToList();

            var tlist = targets.Where(t => t.ReservoirId == r.Id).Select(t => new ReservoirTarget(t.Metric, t.MinValue, t.MaxValue, t.Source)).ToList();
            if (tlist.All(t => t.Metric != Metrics.Ph)) tlist.Add(DefaultPh);
            if (tlist.All(t => t.Metric != Metrics.WaterTemperatureC)) tlist.Add(DefaultWaterTemp);
            if (tlist.All(t => t.Metric != Metrics.EcMsCm))
            {
                var ecs = profiles.Where(p => fed.Any(b => b.Id == p.BedId) && p.EcMin != null && p.EcMax != null).ToList();
                if (ecs.Count > 0)
                {
                    double lo = ecs.Max(p => p.EcMin!.Value), hi = ecs.Min(p => p.EcMax!.Value);
                    if (lo > hi) (lo, hi) = (ecs.Average(p => p.EcMin!.Value), ecs.Average(p => p.EcMax!.Value));
                    tlist.Add(new ReservoirTarget(Metrics.EcMsCm, Math.Round(lo, 2), Math.Round(hi, 2), "profile"));
                }
            }

            var alerts = new List<string>();
            foreach (var t in tlist)
            {
                var v = readings.FirstOrDefault(x => x.Metric == t.Metric)?.Value;
                if (v is null) continue;
                if (t.Min is { } mn && v < mn) alerts.Add($"{Metrics.Label(t.Metric)} {v:0.##} below {mn}");
                if (t.Max is { } mx && v > mx) alerts.Add($"{Metrics.Label(t.Metric)} {v:0.##} above {mx}");
            }
            foreach (var a in acts.Where(a => a.FlowChannelId != null && a.IsOn))
            {
                var flow = sensors.SelectMany(s => s.Channels).FirstOrDefault(c => c.Id == a.FlowChannelId);
                if (flow?.LastValue is { } fv && fv < (a.FlowMinWhenOn ?? 0.2) && a.StateSince is { } since && (now - since).TotalSeconds >= (a.FlowGraceSec ?? 20))
                    alerts.Add($"flow stall: {a.Name} is on but flow is {fv:0.##} L/min");
            }
            foreach (var a in acts.Where(a => a.ContinuousRun && a.Enabled && !a.IsOn))
                alerts.Add($"{a.Name} should run continuously but is off");
            double? days = r.LastChangedAt is { } lc ? Math.Round((now - lc).TotalDays, 1) : null;
            var due = r.LastChangedAt is { } l2 && r.ChangeIntervalDays is { } iv && now >= l2.AddDays(iv);
            if (due) alerts.Add("solution change due");

            var since0 = r.LastChangedAt ?? DateTimeOffset.MinValue;
            var doses = acts.Where(a => ActuatorRoles.IsDosing(a.Role)).Select(a => new DoseTotal(a.Id, a.Name, a.Role,
                Math.Round(doseRuns.Where(x => x.ActuatorId == a.Id && x.EndedAt >= since0).Sum(x => x.AmountMl ?? x.DurationSec * (a.FlowRateMlPerMin ?? 0) / 60.0), 1))).ToList();
            var aiActive = running.Any(x => (x.TargetType == "reservoir" && x.TargetId == r.Id) || (x.TargetType == "actuator" && acts.Any(a => a.Id == x.TargetId)));

            result.Add(new ReservoirStatus(r.Id, r.GardenId, r.Name, r.VolumeLiters, r.LastChangedAt, r.ChangeIntervalDays, days, due, r.ToppedUpLitersSinceChange,
                r.AiContext, r.Notes, readings, tlist, acts.Select(ActuatorBrief.From).ToList(), fed.Select(b => new BedRef(b.Id, b.Name, b.HydroSystem)).ToList(),
                doses, alerts, aiActive));
        }
        return result;
    }

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
            var medium = b.GrowingMedium ?? garden.DefaultGrowingMedium;
            var bp = plants.Where(p => p.BedId == b.Id).Select(p =>
            {
                var tg = targets.Where(t => t.PlantId == p.Id).Select(t => new CareRange(t.Metric, t.MinValue, t.MaxValue, t.Source)).ToList();
                if (p.PlantProfile is { } prof && medium != GrowingMedia.Hydroponic && tg.All(t => t.Metric != Metrics.SoilMoisturePct) && prof.SoilMoistureMinPct is not null)
                    tg.Add(new CareRange(Metrics.SoilMoisturePct, prof.SoilMoistureMinPct, prof.SoilMoistureMaxPct, "profile"));
                if (p.PlantProfile is { } prof2 && tg.All(t => t.Metric != Metrics.AirTemperatureC) && prof2.AirTempMinC is not null)
                    tg.Add(new CareRange(Metrics.AirTemperatureC, prof2.AirTempMinC, prof2.AirTempMaxC, "profile"));
                return new PlantOverview(PlantDto.From(p, garden.Id), ReadingsFor(s => s.PlantId == p.Id), tg, Acting("plant", p.Id));
            }).ToList();
            return new BedOverview(BedDto.From(b, bp.Count), bp, ReadingsFor(s => s.BedId == b.Id && s.PlantId == null), Acting("bed", b.Id),
                medium, medium == GrowingMedia.Hydroponic ? b.HydroSystem : null, b.ReservoirId);
        }).ToList();

        return new GardenOverview(
            GardenDto.From(garden),
            env.For(garden),
            bedOverviews,
            await ReservoirsAsync(garden.Id, null, ct),
            ReadingsFor(s => s.BedId == null && s.PlantId == null && s.ReservoirId == null),
            actuators.Select(ActuatorBrief.From).ToList(),
            cameras.Select(c => new CameraBrief(c.Id, c.Name, c.TargetType, c.BedId, c.PlantId, c.LastFrameAt, $"/api/cameras/{c.Id}/snapshot")).ToList(),
            new DeviceSummary(devices.Count, devices.Count(d => d.IsOnline), devices.Count(d => d.Status == DeviceStatuses.Pending),
                devices.Select(d => new DeviceBrief(d.Id, d.Name, d.Status, d.IsOnline, d.LastSeenAt, unsigned.GetValueOrDefault(d.Id))).ToList()),
            stops,
            pending,
            running,
            unread);
    }
}
