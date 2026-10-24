using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record SafetyDecision(bool Allowed, string? Reason, double? DurationSec, IReadOnlyList<string> Notes);

public sealed record StopScope(string ScopeType, Guid? ScopeId, string Reason);

/// <summary>Resolves effective safety limits and emergency stops per actuator, and checks commands against them.</summary>
public sealed class SafetyService(SlopDbContext db)
{
    public async Task<(Guid? BedId, Guid? PlantId)> ResolvePlacementAsync(ITargeted t, CancellationToken ct)
    {
        if (t.PlantId is { } pid)
        {
            var bedId = await db.Plants.Where(p => p.Id == pid).Select(p => (Guid?)p.BedId).FirstOrDefaultAsync(ct);
            return (bedId ?? t.BedId, pid);
        }
        return (t.BedId, null);
    }

    private static bool LimitApplies(SafetyLimit l, Actuator a, Guid? bedId, Guid? plantId)
    {
        if (!l.Enabled) return false;
        var scopeMatch = l.ScopeType switch
        {
            Data.ScopeTypes.Global => true,
            Data.ScopeTypes.Garden => l.ScopeId == a.GardenId,
            Data.ScopeTypes.Bed => bedId is not null && l.ScopeId == bedId,
            Data.ScopeTypes.Plant => plantId is not null && l.ScopeId == plantId,
            Data.ScopeTypes.Device => l.ScopeId == a.DeviceId,
            Data.ScopeTypes.Actuator => l.ScopeId == a.Id,
            _ => false,
        };
        if (!scopeMatch) return false;
        if (l.ActuatorKind is { Length: > 0 } kind) return kind == a.Kind;
        return l.LimitType switch
        {
            SafetyLimitTypes.TankLowLockout => ActuatorKinds.IsWatering(a.Kind) || a.Kind == ActuatorKinds.FertilizerDoser,
            SafetyLimitTypes.MaxTemperatureCutoff => a.Kind is ActuatorKinds.Heater or ActuatorKinds.Light,
            SafetyLimitTypes.MinTemperatureCutoff => ActuatorKinds.IsWatering(a.Kind),
            SafetyLimitTypes.MaxDailyAmountMl => a.Kind == ActuatorKinds.FertilizerDoser,
            SafetyLimitTypes.MaxRunSeconds or SafetyLimitTypes.MaxDailyRunSeconds => ActuatorKinds.IsWatering(a.Kind) || a.Kind == ActuatorKinds.FertilizerDoser,
            _ => true,
        };
    }

    public async Task<List<SafetyLimit>> ApplicableLimitsAsync(Actuator a, CancellationToken ct)
    {
        var (bedId, plantId) = await ResolvePlacementAsync(a, ct);
        var all = await db.SafetyLimits.AsNoTracking().Where(l => l.Enabled).ToListAsync(ct);
        return all.Where(l => LimitApplies(l, a, bedId, plantId)).ToList();
    }

    public async Task<ActuatorSafety> ResolveAsync(Actuator a, CancellationToken ct)
    {
        var limits = await ApplicableLimitsAsync(a, ct);
        int? MinInt(string type) => limits.Where(l => l.LimitType == type).Select(l => (int?)(int)Math.Floor(l.Value)).Min();
        double? MinDouble(string type) => limits.Where(l => l.LimitType == type).Select(l => (double?)l.Value).Min();
        int? MaxInt(string type) => limits.Where(l => l.LimitType == type).Select(l => (int?)(int)Math.Ceiling(l.Value)).Max();

        var interlocks = limits
            .Where(l => SafetyLimitTypes.Interlocks.Contains(l.LimitType) && l.ChannelId is not null)
            .Select(l => new SafetyInterlock(l.LimitType, l.ChannelId!.Value, l.Operator ?? SafetyLimitTypes.DefaultOperator(l.LimitType), l.Value,
                string.IsNullOrWhiteSpace(l.Notes) ? Describe(l) : l.Notes))
            .ToList();

        return new ActuatorSafety(
            MinInt(SafetyLimitTypes.MaxRunSeconds),
            MinInt(SafetyLimitTypes.MaxDailyRunSeconds),
            MaxInt(SafetyLimitTypes.MinSecondsBetweenRuns),
            MinDouble(SafetyLimitTypes.MaxDailyAmountMl),
            interlocks);
    }

    public static string Describe(SafetyLimit l) => l.LimitType switch
    {
        SafetyLimitTypes.TankLowLockout => $"No pumping while tank level < {l.Value}",
        SafetyLimitTypes.MaxTemperatureCutoff => $"Cut off above {l.Value} °C",
        SafetyLimitTypes.MinTemperatureCutoff => $"Cut off below {l.Value} °C",
        SafetyLimitTypes.MaxRunSeconds => $"Max {l.Value} s per run",
        SafetyLimitTypes.MaxDailyRunSeconds => $"Max {l.Value} s per day",
        SafetyLimitTypes.MinSecondsBetweenRuns => $"At least {l.Value} s between runs",
        SafetyLimitTypes.MaxDailyAmountMl => $"Max {l.Value} ml per day",
        _ => l.LimitType,
    };

    public async Task<List<StopScope>> ActiveStopsForAsync(Actuator a, CancellationToken ct)
    {
        var (bedId, plantId) = await ResolvePlacementAsync(a, ct);
        var stops = await db.EmergencyStops.AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        return stops.Where(s => s.ScopeType switch
        {
            Data.ScopeTypes.Global => true,
            Data.ScopeTypes.Garden => s.ScopeId == a.GardenId,
            Data.ScopeTypes.Bed => bedId is not null && s.ScopeId == bedId,
            Data.ScopeTypes.Plant => plantId is not null && s.ScopeId == plantId,
            Data.ScopeTypes.Device => s.ScopeId == a.DeviceId,
            Data.ScopeTypes.Actuator => s.ScopeId == a.Id,
            _ => false,
        }).Select(s => new StopScope(s.ScopeType, s.ScopeId, s.Reason)).ToList();
    }

    public async Task<TimeZoneInfo> GardenTimeZoneAsync(Guid gardenId, CancellationToken ct)
    {
        var tz = await db.Gardens.Where(g => g.Id == gardenId).Select(g => g.TimezoneId).FirstOrDefaultAsync(ct);
        return TimeZones.Find(tz);
    }

    /// <summary>Server-side gate before a command is sent. The client enforces the same limits again.</summary>
    public async Task<SafetyDecision> CheckAsync(Actuator a, string action, double? durationSec, double? amountMl, CancellationToken ct)
    {
        var notes = new List<string>();
        if (action == ActuatorActions.Off) return new SafetyDecision(true, null, null, notes);
        if (!a.Enabled) return new SafetyDecision(false, "Actuator is disabled", null, notes);

        var stops = await ActiveStopsForAsync(a, ct);
        if (stops.Count > 0)
            return new SafetyDecision(false, $"Emergency stop active ({stops[0].ScopeType}: {stops[0].Reason})", null, notes);

        var safety = await ResolveAsync(a, ct);
        if (action == ActuatorActions.Dose)
        {
            if (amountMl is null or <= 0) return new SafetyDecision(false, "Dose requires a positive amountMl", null, notes);
            if (a.FlowRateMlPerMin is null or <= 0) return new SafetyDecision(false, "Actuator has no flow rate configured, cannot dose", null, notes);
            durationSec = amountMl.Value / a.FlowRateMlPerMin.Value * 60.0;
        }
        else if (action == ActuatorActions.Pulse && durationSec is null or <= 0)
        {
            return new SafetyDecision(false, "Pulse requires a positive durationSec", null, notes);
        }

        if (safety.MaxRunSeconds is { } maxRun)
        {
            if (action == ActuatorActions.On)
            {
                durationSec ??= maxRun;
                notes.Add($"'on' is capped at {maxRun} s by the max-run limit");
            }
            if (durationSec > maxRun)
            {
                if (action == ActuatorActions.Dose)
                    return new SafetyDecision(false, $"Dose would run {durationSec:0} s, above the {maxRun} s per-run limit", null, notes);
                notes.Add($"Duration capped from {durationSec:0} s to {maxRun} s (max per run)");
                durationSec = maxRun;
            }
        }

        var tz = await GardenTimeZoneAsync(a.GardenId, ct);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var dayStartLocal = new DateTimeOffset(nowLocal.Date, nowLocal.Offset);
        var runsToday = await db.ActuatorRuns.AsNoTracking()
            .Where(r => r.ActuatorId == a.Id && r.EndedAt >= dayStartLocal.ToUniversalTime())
            .ToListAsync(ct);

        if (safety.MaxDailyRunSeconds is { } maxDaily)
        {
            var used = runsToday.Sum(r => r.DurationSec);
            var remaining = maxDaily - used;
            if (remaining <= 0) return new SafetyDecision(false, $"Daily run limit reached ({used:0}/{maxDaily} s today)", null, notes);
            if (durationSec > remaining)
            {
                if (action == ActuatorActions.Dose)
                    return new SafetyDecision(false, $"Dose exceeds remaining daily run time ({remaining:0} s left)", null, notes);
                notes.Add($"Duration capped to {remaining:0} s (daily limit {maxDaily} s, {used:0} s used)");
                durationSec = remaining;
            }
        }

        if (safety.MinSecondsBetweenRuns is { } minGap)
        {
            var lastEnd = await db.ActuatorRuns.AsNoTracking().Where(r => r.ActuatorId == a.Id).MaxAsync(r => (DateTimeOffset?)r.EndedAt, ct);
            if (a.IsOn) return new SafetyDecision(false, "Actuator is already running", null, notes);
            if (lastEnd is { } le && (DateTimeOffset.UtcNow - le).TotalSeconds < minGap)
                return new SafetyDecision(false, $"Minimum {minGap} s between runs (last run ended {(DateTimeOffset.UtcNow - le).TotalSeconds:0} s ago)", null, notes);
        }

        if (safety.MaxDailyAmountMl is { } maxMl && a.FlowRateMlPerMin is { } rate && durationSec is { } d)
        {
            var usedMl = runsToday.Sum(r => r.AmountMl ?? r.DurationSec * rate / 60.0);
            var thisMl = d * rate / 60.0;
            if (usedMl + thisMl > maxMl + 1e-6)
                return new SafetyDecision(false, $"Daily dose limit: {usedMl:0.#} ml used + {thisMl:0.#} ml > {maxMl} ml", null, notes);
        }

        if (safety.Interlocks.Count > 0)
        {
            var channelIds = safety.Interlocks.Select(i => i.ChannelId).ToList();
            var values = await db.SensorChannels.AsNoTracking().Where(c => channelIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.LastValue, ct);
            foreach (var il in safety.Interlocks)
            {
                if (values.TryGetValue(il.ChannelId, out var v) && v is { } val && il.IsTripped(val))
                    return new SafetyDecision(false, $"{il.Description} (current value {val:0.##})", null, notes);
            }
        }

        return new SafetyDecision(true, null, durationSec, notes);
    }
}

public static class TimeZones
{
    public static TimeZoneInfo Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
