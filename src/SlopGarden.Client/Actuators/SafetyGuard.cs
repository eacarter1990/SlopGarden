using System.Globalization;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Storage;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Actuators;

/// <summary>A request to change an actuator, from a server command, a local schedule or a manual action.</summary>
public sealed record ActuatorRequest(string Action, double? DurationSec = null, double? AmountMl = null, double? Level = null);

/// <summary>Outcome of the safety check. <see cref="DurationSec"/> is the enforced run time (null = until switched off,
/// only possible when no limit applies); <see cref="StopReason"/> is reported when that planned end is reached.</summary>
public sealed record SafetyDecision(bool Allowed, string? Reason, double? DurationSec = null, double? AmountMl = null, string? Note = null, string StopReason = StopReasons.Completed)
{
    public static SafetyDecision Reject(string reason) => new(false, reason);
}

public static class StopReasons
{
    public const string Completed = "completed";
    public const string OffCommand = "off_command";
    public const string MaxRunTime = "max_run_time";
    public const string DailyLimit = "daily_limit";
    public const string HourlyLimit = "hourly_limit";
    public const string Interlock = "interlock";
    public const string FlowStall = "flow_stall";
    public const string EmergencyStop = "emergency_stop";
    public const string Reconfigured = "reconfigured";
    public const string Shutdown = "shutdown";
    public const string Error = "error";
}

public sealed record DailyUsage(double RunSeconds, double AmountMl);

/// <summary>The other actuators of the device, for limits that depend on them (reservoir dosing, required pumps).</summary>
public sealed record ActuatorPeers(IReadOnlyCollection<ActuatorConfig> Actuators, Func<Guid, bool> IsOn);

/// <summary>Enforces <see cref="ActuatorSafety"/> locally. Commands cannot override these limits.</summary>
public sealed class SafetyGuard(IRunLedger ledger, IChannelValues values, TimeProvider time)
{
    /// <summary>An interlock whose channel has no reading this fresh blocks the actuator (fail closed).</summary>
    public static readonly TimeSpan InterlockMaxAge = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(1);
    private const double Epsilon = 1e-6;

    /// <summary>Checks a request against the actuator's limits. Without <paramref name="peers"/>, limits that depend on other
    /// actuators fail closed (<c>RequiresActuatorOn</c>) or only consider this actuator (reservoir dosing interval).
    /// Per-run, daily and minimum-interval limits do not apply to continuous roles (circulation and air pumps).</summary>
    public SafetyDecision Evaluate(ActuatorConfig actuator, string timeZoneId, EmergencyStopState emergencyStop, ActuatorRequest request, bool running,
        ActuatorPeers? peers = null)
    {
        if (request.Action == ActuatorActions.Off) return new SafetyDecision(true, null);
        if (request.Action is not (ActuatorActions.On or ActuatorActions.Pulse or ActuatorActions.Dose))
            return SafetyDecision.Reject($"unknown action '{request.Action}'");
        if (emergencyStop.IsStopped(actuator.Id))
            return SafetyDecision.Reject("emergency stop is active" + (emergencyStop.Reason is { Length: > 0 } r ? $": {r}" : ""));
        if (!actuator.Enabled) return SafetyDecision.Reject("actuator is disabled");
        if (running) return SafetyDecision.Reject("actuator is already running");
        if (request.Level is { } level && (!double.IsFinite(level) || level is < 0 or > 1)) return SafetyDecision.Reject("level must be between 0 and 1");

        var safety = actuator.Safety ?? ActuatorSafety.None;
        var now = time.GetUtcNow();
        var continuous = ActuatorRoles.IsContinuous(actuator.Role);

        if (CheckInterlocks(actuator, requireFresh: true) is { } blocked) return SafetyDecision.Reject(blocked);
        if (CheckRequiredActuator(actuator, peers) is { } notRunning) return SafetyDecision.Reject(notRunning);
        if (CheckReservoirMixing(actuator, peers, now) is { } mixing) return SafetyDecision.Reject(mixing);

        if (!continuous && safety.MinSecondsBetweenRuns is > 0 && ledger.LastEnd(actuator.Id) is { } lastEnd)
        {
            var since = (now - lastEnd).TotalSeconds;
            if (since < safety.MinSecondsBetweenRuns)
                return SafetyDecision.Reject($"minimum {safety.MinSecondsBetweenRuns}s between runs (last run ended {Fmt(since)}s ago)");
        }

        var flow = actuator.FlowRateMlPerMin is > 0 ? actuator.FlowRateMlPerMin.Value : (double?)null;
        double? duration;
        switch (request.Action)
        {
            case ActuatorActions.Pulse:
                if (request.DurationSec is not > 0) return SafetyDecision.Reject("pulse requires durationSec > 0");
                duration = request.DurationSec;
                break;
            case ActuatorActions.Dose:
                if (request.AmountMl is not > 0) return SafetyDecision.Reject("dose requires amountMl > 0");
                if (flow is null) return SafetyDecision.Reject("dose requires the actuator's flowRateMlPerMin");
                duration = request.AmountMl.Value / flow.Value * 60;
                break;
            default:
                duration = request.DurationSec is > 0 ? request.DurationSec : null;
                break;
        }

        string? note = null;
        var stopReason = StopReasons.Completed;
        if (!continuous && safety.MaxRunSeconds is int maxRun && maxRun > 0)
        {
            if (duration is null)
            {
                duration = maxRun;
                stopReason = StopReasons.MaxRunTime;
                note = $"auto-off after max run time {maxRun}s";
            }
            else if (duration > maxRun + Epsilon)
            {
                if (request.Action == ActuatorActions.Dose)
                    return SafetyDecision.Reject($"dose of {Fmt(request.AmountMl!.Value)} ml needs {Fmt(duration.Value)}s, exceeding max run time {maxRun}s");
                note = $"duration capped from {Fmt(duration.Value)}s to max run time {maxRun}s";
                duration = maxRun;
            }
        }

        var usage = continuous ? new DailyUsage(0, 0) : GetDailyUsage(actuator.Id, timeZoneId);
        if (!continuous && safety.MaxDailyRunSeconds is int maxDaily && maxDaily > 0)
        {
            var remaining = maxDaily - usage.RunSeconds;
            if (remaining <= Epsilon)
                return SafetyDecision.Reject($"daily run limit reached ({Fmt(usage.RunSeconds)}s of {maxDaily}s used today)");
            if (duration is null || (request.Action == ActuatorActions.On && duration > remaining))
            {
                duration = remaining;
                stopReason = StopReasons.DailyLimit;
                note = $"auto-off when the daily run limit is reached in {Fmt(remaining)}s";
            }
            else if (duration > remaining + Epsilon)
            {
                return SafetyDecision.Reject($"would exceed daily run limit: requested {Fmt(duration.Value)}s, {Fmt(remaining)}s of {maxDaily}s left today");
            }
        }

        if (!continuous && safety.MaxDailyAmountMl is double maxMl && maxMl > 0 && flow is not null)
        {
            var remainingMl = maxMl - usage.AmountMl;
            if (remainingMl <= Epsilon)
                return SafetyDecision.Reject($"daily amount limit reached ({Fmt(usage.AmountMl)} of {Fmt(maxMl)} ml used today)");
            var remainingSec = remainingMl / flow.Value * 60;
            if (duration is null || (request.Action == ActuatorActions.On && duration > remainingSec))
            {
                duration = remainingSec;
                stopReason = StopReasons.DailyLimit;
                note = $"auto-off when the daily amount limit is reached in {Fmt(remainingSec)}s";
            }
            else if (duration.Value * flow.Value / 60 > remainingMl + Epsilon)
            {
                return SafetyDecision.Reject($"would exceed daily amount limit: {Fmt(duration.Value * flow.Value / 60)} ml requested, {Fmt(remainingMl)} of {Fmt(maxMl)} ml left today");
            }
        }

        if (safety.MaxHourlyAmountMl is double maxHourlyMl && maxHourlyMl > 0)
        {
            if (flow is null) return SafetyDecision.Reject("hourly amount limit needs the actuator's flowRateMlPerMin");
            var usedMl = GetAmountSince(actuator, now - HourlyWindow, now);
            var remainingMl = maxHourlyMl - usedMl;
            if (remainingMl <= Epsilon)
                return SafetyDecision.Reject($"hourly amount limit reached ({Fmt(usedMl)} of {Fmt(maxHourlyMl)} ml in the last 60 minutes)");
            var remainingSec = remainingMl / flow.Value * 60;
            if (duration is null || (request.Action == ActuatorActions.On && duration > remainingSec))
            {
                duration = remainingSec;
                stopReason = StopReasons.HourlyLimit;
                note = $"auto-off when the hourly amount limit is reached in {Fmt(remainingSec)}s";
            }
            else if (duration.Value * flow.Value / 60 > remainingMl + Epsilon)
            {
                return SafetyDecision.Reject($"would exceed hourly amount limit: {Fmt(duration.Value * flow.Value / 60)} ml requested, {Fmt(remainingMl)} of {Fmt(maxHourlyMl)} ml left in the last 60 minutes");
            }
        }

        double? amount = request.Action == ActuatorActions.Dose ? request.AmountMl : flow is not null && duration is not null ? duration * flow / 60 : null;
        return new SafetyDecision(true, null, duration, amount, note, stopReason);
    }

    /// <summary>Returns a reason when an interlock blocks the actuator. With <paramref name="requireFresh"/> a missing or
    /// stale reading also blocks; otherwise only a tripped value does (used to stop running actuators).</summary>
    public string? CheckInterlocks(ActuatorConfig actuator, bool requireFresh)
    {
        var now = time.GetUtcNow();
        foreach (var interlock in actuator.Safety?.Interlocks ?? [])
        {
            var v = values.Get(interlock.ChannelId);
            if (v is null || v.Quality == ReadingQuality.Error || now - v.Time > InterlockMaxAge)
            {
                if (requireFresh) return $"interlock '{interlock.Description}': no recent reading of its channel";
                continue;
            }
            if (interlock.IsTripped(v.Value))
                return $"interlock '{interlock.Description}' active ({Fmt(v.Value)} {interlock.Operator} {Fmt(interlock.Threshold)})";
        }
        return null;
    }

    /// <summary>Returns a reason when <c>RequiresActuatorOn</c> names an actuator that is not currently on.</summary>
    public static string? CheckRequiredActuator(ActuatorConfig actuator, ActuatorPeers? peers)
    {
        if (actuator.Safety?.RequiresActuatorOn is not { } requiredId || requiredId == actuator.Id) return null;
        if (peers is null) return "requires another actuator to be on, but its state is unknown";
        var required = peers.Actuators.FirstOrDefault(a => a.Id == requiredId);
        if (required is null) return $"requires actuator {requiredId} to be on, but it is not configured on this device";
        return peers.IsOn(requiredId) ? null : $"requires '{required.Name}' to be on, but it is off";
    }

    /// <summary>Enforces <c>MinSecondsBetweenReservoirDoses</c>: no dose while any doser of the same reservoir is dosing or
    /// finished less than that many seconds ago, so the previous dose can mix before the next one.</summary>
    private string? CheckReservoirMixing(ActuatorConfig actuator, ActuatorPeers? peers, DateTimeOffset now)
    {
        if (actuator.Safety?.MinSecondsBetweenReservoirDoses is not { } wait || wait <= 0 || actuator.ReservoirId is not { } reservoir) return null;
        var dosers = (peers?.Actuators ?? []).Where(a => a.ReservoirId == reservoir && ActuatorRoles.IsDosing(a.Role)).ToList();
        if (ActuatorRoles.IsDosing(actuator.Role) && dosers.All(a => a.Id != actuator.Id)) dosers.Add(actuator);
        foreach (var doser in dosers)
        {
            if (doser.Id != actuator.Id && peers!.IsOn(doser.Id))
                return $"'{doser.Name}' is dosing into the same reservoir right now; wait {wait}s after it for mixing";
            if (ledger.LastEnd(doser.Id) is { } end && (now - end).TotalSeconds < wait)
                return $"minimum {wait}s between doses into the same reservoir for mixing ('{doser.Name}' finished {Fmt((now - end).TotalSeconds)}s ago)";
        }
        return null;
    }

    /// <summary>Amount dispensed in [from, to): recorded amounts, or run time × flow rate when a run has none.
    /// Runs crossing the window edge count proportionally.</summary>
    public double GetAmountSince(ActuatorConfig actuator, DateTimeOffset from, DateTimeOffset to)
    {
        double amount = 0;
        foreach (var run in ledger.Query(actuator.Id, from, to))
        {
            if (run.DurationSec <= 0) continue;
            var start = run.StartedAt > from ? run.StartedAt : from;
            var end = run.EndedAt < to ? run.EndedAt : to;
            var overlap = Math.Max(0, (end - start).TotalSeconds);
            var total = run.AmountMl ?? (actuator.FlowRateMlPerMin is > 0 ? actuator.FlowRateMlPerMin.Value * run.DurationSec / 60 : 0);
            amount += total * overlap / run.DurationSec;
        }
        return amount;
    }

    /// <summary>Run time and amount used during the current local calendar day in <paramref name="timeZoneId"/>.
    /// Runs crossing midnight count proportionally towards each day.</summary>
    public DailyUsage GetDailyUsage(Guid actuatorId, string timeZoneId)
    {
        var (start, end) = TimeZones.LocalDay(time.GetUtcNow(), TimeZones.Find(timeZoneId));
        double seconds = 0, amount = 0;
        foreach (var run in ledger.Query(actuatorId, start, end))
        {
            var from = run.StartedAt > start ? run.StartedAt : start;
            var to = run.EndedAt < end ? run.EndedAt : end;
            var overlap = Math.Max(0, (to - from).TotalSeconds);
            seconds += overlap;
            if (run.AmountMl is { } ml && run.DurationSec > 0) amount += ml * overlap / run.DurationSec;
        }
        return new DailyUsage(seconds, amount);
    }

    private static string Fmt(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
}
