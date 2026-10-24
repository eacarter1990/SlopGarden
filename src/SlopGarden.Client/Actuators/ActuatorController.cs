using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Server;
using SlopGarden.Client.Storage;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Client.Actuators;

public static class RunSources
{
    public const string Command = "command";
    public const string Schedule = "schedule";
    public const string Manual = "manual";
    /// <summary>Started (and re-asserted) by the client for <see cref="ActuatorConfig.ContinuousRun"/> actuators.</summary>
    public const string Continuous = "continuous";
}

public sealed record CommandOutcome(string Status, string? Reason);

/// <summary>Owns the actuator instances and executes every on/off/pulse/dose request through the <see cref="SafetyGuard"/>.</summary>
public sealed class ActuatorController : IAsyncDisposable
{
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan ContinuousReassertInterval = TimeSpan.FromSeconds(30);
    /// <summary>Flow readings older than this are ignored by the flow watchdog (and count as a silent sensor).</summary>
    public static readonly TimeSpan FlowReadingMaxAge = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FlowEventInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SilentFlowSensorEventInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan NotRunningEventInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, Slot> _slots = new();
    private readonly PluginRegistry _registry;
    private readonly SafetyGuard _guard;
    private readonly IRunLedger _ledger;
    private readonly IChannelValues _values;
    private readonly IDeviceReporter _reporter;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private volatile string _timeZoneId = "UTC";
    private volatile EmergencyStopState _emergencyStop = EmergencyStopState.None;

    public ActuatorController(PluginRegistry registry, SafetyGuard guard, IRunLedger ledger, IChannelValues values, IDeviceReporter reporter,
        IServiceProvider services, TimeProvider time, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _guard = guard;
        _ledger = ledger;
        _values = values;
        _reporter = reporter;
        _services = services;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ActuatorController>();
        _values.Updated += OnChannelValue;
    }

    public EmergencyStopState EmergencyStop => _emergencyStop;

    public async Task ApplyAsync(DeviceConfigDocument doc, List<string> errors)
    {
        _timeZoneId = doc.TimeZoneId;
        _emergencyStop = doc.EmergencyStop ?? EmergencyStopState.None;
        var desired = doc.Actuators.ToDictionary(a => a.Id);

        foreach (var (id, slot) in _slots)
        {
            if (desired.ContainsKey(id)) continue;
            _slots.TryRemove(id, out _);
            await RemoveAsync(slot, StopReasons.Reconfigured);
        }

        foreach (var cfg in desired.Values)
        {
            var plugin = _registry.FindActuator(cfg.PluginId);
            if (_slots.TryGetValue(cfg.Id, out var existing))
            {
                if (existing.Device is not null && ReferenceEquals(plugin, existing.Plugin) && SameHardware(cfg, existing.Config))
                {
                    await existing.Gate.WaitAsync();
                    try
                    {
                        if (ConfigJson(cfg) != ConfigJson(existing.Config)) existing.OffLatched = false;
                        existing.Config = cfg;
                        if (!cfg.Enabled && existing.On) await StopLockedAsync(existing, StopReasons.Reconfigured, "actuator disabled");
                        else if (!cfg.ContinuousRun && existing.Run?.Source == RunSources.Continuous)
                            await StopLockedAsync(existing, StopReasons.Reconfigured, "continuous run disabled");
                    }
                    finally
                    {
                        existing.Gate.Release();
                    }
                    continue;
                }
                _slots.TryRemove(cfg.Id, out _);
                await RemoveAsync(existing, StopReasons.Reconfigured);
            }

            var slot = new Slot(cfg, plugin, _time.GetUtcNow());
            _slots[cfg.Id] = slot;
            if (plugin is null)
            {
                slot.CreateError = $"unknown or unavailable plugin '{cfg.PluginId}'";
                errors.Add($"Actuator '{cfg.Name}' ({cfg.Id}): {slot.CreateError}.");
                continue;
            }
            if (!plugin.SupportedKinds.Contains(cfg.Kind) && !plugin.SupportedKinds.Contains(ActuatorKinds.Other))
                errors.Add($"Actuator '{cfg.Name}' ({cfg.Id}): plugin '{cfg.PluginId}' does not declare support for kind '{cfg.Kind}'.");
            try
            {
                var logger = _loggerFactory.CreateLogger($"SlopGarden.Plugin.{plugin.PluginId}");
                slot.Device = plugin.Create(new PluginContext(cfg.Id, cfg.Name, cfg.Settings.DeepClone().AsObject(), _services, logger));
            }
            catch (Exception ex)
            {
                slot.CreateError = ex.Message;
                errors.Add($"Actuator '{cfg.Name}' ({cfg.Id}): {plugin.PluginId} failed to start: {ex.Message}");
                _logger.LogError(ex, "Actuator {ActuatorName} ({ActuatorId}) could not be created", cfg.Name, cfg.Id);
                continue;
            }
            await ApplyFailsafeAsync(slot, "failsafe");
        }

        await SetEmergencyStopAsync(_emergencyStop);
        foreach (var slot in _slots.Values.Where(s => s.Config.ContinuousRun)) await AssertContinuousAsync(slot);
    }

    /// <summary>Executes a server command: expiry check, acks (received → running → completed/rejected/failed).</summary>
    public async Task<CommandOutcome> ExecuteCommandAsync(ActuatorCommandMessage cmd)
    {
        var now = _time.GetUtcNow();
        if (cmd.ExpiresAt < now)
        {
            var reason = $"command expired at {cmd.ExpiresAt:O}";
            _reporter.Ack(cmd.CommandId, CommandStatuses.Expired, reason);
            return new CommandOutcome(CommandStatuses.Expired, reason);
        }
        _reporter.Ack(cmd.CommandId, CommandStatuses.Received, null);
        if (!_slots.TryGetValue(cmd.ActuatorId, out var slot))
        {
            _reporter.Ack(cmd.CommandId, CommandStatuses.Rejected, "unknown actuator");
            return new CommandOutcome(CommandStatuses.Rejected, "unknown actuator");
        }
        var outcome = await StartAsync(slot, new ActuatorRequest(cmd.Action, cmd.DurationSec, cmd.AmountMl, cmd.Level), RunSources.Command, cmd.CommandId, null);
        if (outcome.Status is CommandStatuses.Rejected or CommandStatuses.Failed)
        {
            _logger.LogWarning("Command {CommandId} ({Action}) for {ActuatorName} {Status}: {Reason}", cmd.CommandId, cmd.Action, slot.Config.Name, outcome.Status, outcome.Reason);
            _reporter.Ack(cmd.CommandId, outcome.Status, outcome.Reason);
        }
        return outcome;
    }

    public async Task<CommandOutcome> RunScheduleAsync(ScheduleConfig schedule)
    {
        if (!_slots.TryGetValue(schedule.ActuatorId, out var slot))
            return new CommandOutcome(CommandStatuses.Rejected, "unknown actuator");
        var request = schedule.AmountMl is > 0
            ? new ActuatorRequest(ActuatorActions.Dose, AmountMl: schedule.AmountMl)
            : new ActuatorRequest(ActuatorActions.Pulse, DurationSec: schedule.DurationSec);
        var outcome = await StartAsync(slot, request, RunSources.Schedule, null, schedule.Id);
        if (outcome.Status is CommandStatuses.Rejected or CommandStatuses.Failed)
        {
            _logger.LogWarning("Schedule {ScheduleName} for {ActuatorName} skipped: {Reason}", schedule.Name, slot.Config.Name, outcome.Reason);
            _reporter.Event(outcome.Status == CommandStatuses.Rejected ? EventSeverities.Warning : EventSeverities.Error, EventCategories.Safety,
                $"Schedule '{schedule.Name}' did not run '{slot.Config.Name}': {outcome.Reason}", actuatorId: slot.Config.Id);
        }
        return outcome;
    }

    /// <summary>Local/manual request (not from the server), still subject to all safety limits.</summary>
    public Task<CommandOutcome> ExecuteLocalAsync(Guid actuatorId, ActuatorRequest request) =>
        _slots.TryGetValue(actuatorId, out var slot)
            ? StartAsync(slot, request, RunSources.Manual, null, null)
            : Task.FromResult(new CommandOutcome(CommandStatuses.Rejected, "unknown actuator"));

    public async Task SetEmergencyStopAsync(EmergencyStopState state)
    {
        _emergencyStop = state;
        var stopped = 0;
        foreach (var slot in _slots.Values.Where(s => state.IsStopped(s.Config.Id)))
        {
            await slot.Gate.WaitAsync();
            try
            {
                if (slot.On)
                {
                    stopped++;
                    await StopLockedAsync(slot, StopReasons.EmergencyStop, "emergency stop" + (state.Reason is { Length: > 0 } r ? $": {r}" : ""));
                }
                else if (slot.Device is not null)
                {
                    await TrySetDeviceAsync(slot, false);
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }
        if (stopped > 0)
        {
            _logger.LogWarning("Emergency stop: {Count} running actuator(s) switched off", stopped);
            _reporter.Event(EventSeverities.Critical, EventCategories.Safety, $"Emergency stop: {stopped} running actuator(s) switched off" + (state.Reason is { Length: > 0 } r ? $" ({r})" : ""));
        }
    }

    public IReadOnlyList<ActuatorStateReport> CurrentStates() =>
        _slots.Values.Select(s => new ActuatorStateReport(s.Config.Id, s.On, s.Level, s.Since, s.Run?.CommandId, s.Run?.Source ?? "current")).ToList();

    public bool IsOn(Guid actuatorId) => _slots.TryGetValue(actuatorId, out var s) && s.On;

    /// <summary>Periodic safety work (called about every second by <see cref="ActuatorSupervisor"/>): flow watchdogs,
    /// stopping actuators whose required actuator went off, and re-asserting continuous runs every
    /// <see cref="ContinuousReassertInterval"/>. Never throws.</summary>
    public async Task SuperviseAsync()
    {
        foreach (var slot in _slots.Values)
        {
            try
            {
                await WatchFlowAsync(slot);
                await EnforceRequiredActuatorAsync(slot);
                if (slot.Config.ContinuousRun && _time.GetUtcNow() >= slot.NextContinuousAttempt) await AssertContinuousAsync(slot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supervising {ActuatorName} failed", slot.Config.Name);
            }
        }
    }

    public async Task ShutdownAsync()
    {
        foreach (var slot in _slots.Values) await RemoveAsync(slot, StopReasons.Shutdown);
        _slots.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _values.Updated -= OnChannelValue;
        await ShutdownAsync();
    }

    /// <summary>An explicit request (command, schedule, local). Off latches continuous runs off; a successful start clears
    /// the off latch and the flow-stall latch.</summary>
    private async Task<CommandOutcome> StartAsync(Slot slot, ActuatorRequest request, string source, Guid? commandId, Guid? scheduleId)
    {
        await slot.Gate.WaitAsync();
        try
        {
            var outcome = await StartLockedAsync(slot, request, source, commandId, scheduleId, null);
            if (request.Action == ActuatorActions.Off)
            {
                slot.OffLatched = true;
            }
            else if (outcome.Status == CommandStatuses.Running)
            {
                slot.OffLatched = false;
                slot.StallRetryAt = null;
            }
            return outcome;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Caller holds the slot gate.</summary>
    private async Task<CommandOutcome> StartLockedAsync(Slot slot, ActuatorRequest request, string source, Guid? commandId, Guid? scheduleId, string? reason)
    {
        if (slot.Device is null) return new CommandOutcome(CommandStatuses.Failed, $"actuator not available: {slot.CreateError ?? "not created"}");

        if (request.Action == ActuatorActions.Off)
        {
            if (slot.On) await StopLockedAsync(slot, StopReasons.OffCommand, "switched off");
            else await TrySetDeviceAsync(slot, false);
            if (slot.On || slot.OffFailed) return new CommandOutcome(CommandStatuses.Failed, "could not switch off");
            if (commandId is { } offId) _reporter.Ack(offId, CommandStatuses.Completed, null);
            return new CommandOutcome(CommandStatuses.Completed, null);
        }

        var decision = _guard.Evaluate(slot.Config, _timeZoneId, _emergencyStop, request, running: slot.On, Peers());
        if (!decision.Allowed) return new CommandOutcome(CommandStatuses.Rejected, decision.Reason);

        try
        {
            using var timeout = new CancellationTokenSource(DeviceTimeout);
            await slot.Device.SetStateAsync(true, request.Level, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching on {ActuatorName} failed", slot.Config.Name);
            await TrySetDeviceAsync(slot, false);
            _reporter.Event(EventSeverities.Error, EventCategories.Actuator, $"'{slot.Config.Name}' could not be switched on: {ex.Message}", actuatorId: slot.Config.Id);
            return new CommandOutcome(CommandStatuses.Failed, ex.Message);
        }

        var now = _time.GetUtcNow();
        var flowPerSec = slot.Config.FlowRateMlPerMin is > 0 ? slot.Config.FlowRateMlPerMin / 60 : null;
        var ackOnEnd = commandId is not null && request.Action is ActuatorActions.Pulse or ActuatorActions.Dose;
        var run = new ActiveRun(Guid.NewGuid(), commandId, scheduleId, source, now, flowPerSec, decision.StopReason, ackOnEnd);
        slot.On = true;
        slot.Level = request.Level;
        slot.Since = now;
        slot.Run = run;
        slot.FlowSeen = false;
        _logger.LogInformation("{ActuatorName} on ({Source}{Duration}){Note}", slot.Config.Name, source,
            decision.DurationSec is { } d ? $", {d.ToString("0.#", CultureInfo.InvariantCulture)}s" : "", decision.Note is { } n ? $": {n}" : "");
        _reporter.ActuatorState(new ActuatorStateReport(slot.Config.Id, true, request.Level, now, commandId, reason ?? source));
        if (commandId is { } id)
        {
            _reporter.Ack(id, CommandStatuses.Running, decision.Note);
            if (!ackOnEnd) _reporter.Ack(id, CommandStatuses.Completed, decision.Note);
        }
        if (decision.DurationSec is { } duration) StartTimer(slot, run, TimeSpan.FromSeconds(duration));
        return new CommandOutcome(CommandStatuses.Running, decision.Note);
    }

    private void StartTimer(Slot slot, ActiveRun run, TimeSpan duration)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(duration, _time, run.Timer.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await slot.Gate.WaitAsync();
            try
            {
                if (ReferenceEquals(slot.Run, run)) await StopLockedAsync(slot, run.PlannedStopReason, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ending run of {ActuatorName} failed", slot.Config.Name);
            }
            finally
            {
                slot.Gate.Release();
            }
        });
    }

    /// <summary>Switches off and, if a run was active, records/reports it. Caller holds the slot gate.</summary>
    private async Task StopLockedAsync(Slot slot, string stopReason, string? detail)
    {
        var switchedOff = await TrySetDeviceAsync(slot, false);
        var now = _time.GetUtcNow();
        var run = slot.Run;
        slot.Run = null;
        slot.On = !switchedOff;
        slot.Level = switchedOff ? null : slot.Level;
        if (switchedOff) slot.Since = now;
        _reporter.ActuatorState(new ActuatorStateReport(slot.Config.Id, slot.On, slot.Level, slot.Since, run?.CommandId, switchedOff ? stopReason : "off_failed"));
        if (run is null) return;

        await run.Timer.CancelAsync();
        var duration = Math.Max(0, (now - run.StartedAt).TotalSeconds);
        var amount = run.FlowMlPerSec * duration;
        _ledger.Record(new RunRecord(run.RunId, slot.Config.Id, run.StartedAt, now, amount));
        _reporter.Run(new ActuatorRunReport(run.RunId, slot.Config.Id, run.CommandId, run.ScheduleId, run.StartedAt, now, duration, amount, run.Source, stopReason));
        _logger.LogInformation("{ActuatorName} off after {Duration:0.#}s ({StopReason})", slot.Config.Name, duration, stopReason);
        if (run.AckOnEnd && run.CommandId is { } commandId)
        {
            var status = stopReason switch
            {
                StopReasons.Completed or StopReasons.MaxRunTime or StopReasons.DailyLimit or StopReasons.HourlyLimit => CommandStatuses.Completed,
                StopReasons.Error => CommandStatuses.Failed,
                _ => CommandStatuses.Cancelled,
            };
            _reporter.Ack(commandId, status, status == CommandStatuses.Completed ? null : detail ?? stopReason);
        }
    }

    private async Task<bool> TrySetDeviceAsync(Slot slot, bool on)
    {
        if (slot.Device is null) return !on;
        try
        {
            using var timeout = new CancellationTokenSource(DeviceTimeout);
            await slot.Device.SetStateAsync(on, null, timeout.Token);
            if (!on) slot.OffFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch {ActuatorName} {State}", slot.Config.Name, on ? "on" : "off");
            _reporter.Event(on ? EventSeverities.Error : EventSeverities.Critical, EventCategories.Actuator,
                $"Could not switch '{slot.Config.Name}' {(on ? "on" : "off")}: {ex.Message}", actuatorId: slot.Config.Id);
            if (!on && !slot.OffFailed)
            {
                slot.OffFailed = true;
                StartOffRetry(slot, slot.Device);
            }
            return false;
        }
    }

    /// <summary>Keeps trying to switch off an output that failed to switch off, until it succeeds or the device is replaced.</summary>
    private void StartOffRetry(Slot slot, IActuator device)
    {
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(2);
            while (true)
            {
                await Task.Delay(delay, _time);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                await slot.Gate.WaitAsync();
                try
                {
                    if (!slot.OffFailed || !ReferenceEquals(slot.Device, device)) return;
                    if (slot.Run is not null) continue;
                    using var timeout = new CancellationTokenSource(DeviceTimeout);
                    await device.SetStateAsync(false, null, timeout.Token);
                    slot.OffFailed = false;
                    slot.On = false;
                    slot.Level = null;
                    slot.Since = _time.GetUtcNow();
                    _logger.LogWarning("{ActuatorName} switched off after an earlier failure", slot.Config.Name);
                    _reporter.Event(EventSeverities.Warning, EventCategories.Actuator, $"'{slot.Config.Name}' is off again after an earlier switch-off failure", actuatorId: slot.Config.Id);
                    _reporter.ActuatorState(new ActuatorStateReport(slot.Config.Id, false, null, slot.Since, null, "off_retry"));
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Retrying switch-off of {ActuatorName} failed", slot.Config.Name);
                }
                finally
                {
                    slot.Gate.Release();
                }
            }
        });
    }

    private async Task ApplyFailsafeAsync(Slot slot, string reason)
    {
        var on = slot.Config.FailsafeState == "on" && slot.Config.Enabled && !_emergencyStop.IsStopped(slot.Config.Id);
        if (await TrySetDeviceAsync(slot, on))
        {
            slot.On = on;
            slot.Since = _time.GetUtcNow();
        }
        _reporter.ActuatorState(new ActuatorStateReport(slot.Config.Id, slot.On, null, slot.Since, null, reason));
    }

    private async Task RemoveAsync(Slot slot, string stopReason)
    {
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Run is not null) await StopLockedAsync(slot, stopReason, stopReason);
            if (slot.Device is null) return;
            await ApplyFailsafeAsync(slot, stopReason);
            try
            {
                await slot.Device.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing actuator {ActuatorName} failed", slot.Config.Name);
            }
            slot.Device = null;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private void OnChannelValue(Guid channelId, ChannelValue value)
    {
        foreach (var slot in _slots.Values)
        {
            if (!slot.On || slot.Config.Safety?.Interlocks is not { Count: > 0 } interlocks || !interlocks.Any(i => i.ChannelId == channelId)) continue;
            var reason = _guard.CheckInterlocks(slot.Config, requireFresh: false);
            if (reason is not null) _ = StopForInterlockAsync(slot, reason);
        }
    }

    private async Task StopForInterlockAsync(Slot slot, string reason)
    {
        await slot.Gate.WaitAsync();
        try
        {
            if (!slot.On) return;
            await StopLockedAsync(slot, StopReasons.Interlock, reason);
            _logger.LogWarning("Safety: {ActuatorName} switched off: {Reason}", slot.Config.Name, reason);
            _reporter.Event(EventSeverities.Critical, EventCategories.Safety, $"'{slot.Config.Name}' switched off: {reason}", actuatorId: slot.Config.Id,
                code: SafetyEventCodes.InterlockTripped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interlock stop of {ActuatorName} failed", slot.Config.Name);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Switches a <see cref="ActuatorConfig.ContinuousRun"/> actuator on unless it is on, latched off by an explicit off,
    /// waiting out a flow-stall retry, disabled, emergency-stopped or failed to switch off.</summary>
    private async Task AssertContinuousAsync(Slot slot)
    {
        await slot.Gate.WaitAsync();
        try
        {
            var now = _time.GetUtcNow();
            slot.NextContinuousAttempt = now + ContinuousReassertInterval;
            var cfg = slot.Config;
            if (!cfg.ContinuousRun || slot.On || slot.Device is null || slot.OffFailed || slot.OffLatched || !cfg.Enabled || _emergencyStop.IsStopped(cfg.Id)) return;
            if (slot.StallRetryAt is { } retryAt && now < retryAt)
            {
                slot.NextContinuousAttempt = retryAt;
                return;
            }
            if (slot.StallRetryAt is not null) _logger.LogInformation("Retrying {ActuatorName} after a flow stall", cfg.Name);
            var outcome = await StartLockedAsync(slot, new ActuatorRequest(ActuatorActions.On), RunSources.Continuous, null, null, "continuous run");
            if (outcome.Status == CommandStatuses.Rejected)
            {
                ReportLimited(slot, SafetyEventCodes.PumpNotRunning, NotRunningEventInterval, EventSeverities.Warning,
                    $"'{cfg.Name}' should run continuously but cannot be switched on: {outcome.Reason}", SafetyEventCodes.PumpNotRunning);
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Evaluates <see cref="FlowWatch"/>: stall while on, unexpected flow while off, silent flow sensor.</summary>
    private async Task WatchFlowAsync(Slot slot)
    {
        if (slot.Config.FlowWatch is null) return;
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Config.FlowWatch is not { } watch || slot.Device is null) return;
            var now = _time.GetUtcNow();
            var name = slot.Config.Name;
            var grace = TimeSpan.FromSeconds(Math.Max(0, watch.GraceSec));
            var reading = _values.Get(watch.ChannelId);
            if (reading is null || reading.Quality == ReadingQuality.Error || now - reading.Time > FlowReadingMaxAge)
            {
                if (slot.On && now - slot.Since > grace * 3)
                {
                    ReportLimited(slot, "flow_sensor_silent", SilentFlowSensorEventInterval, EventSeverities.Warning,
                        $"'{name}' has been on for {Secs(now - slot.Since)} s but its flow sensor sent no reading in the last {Secs(FlowReadingMaxAge)} s; flow cannot be verified", null);
                }
                return;
            }
            slot.LastEvents.Remove("flow_sensor_silent");
            // Readings taken during the grace period after switching (spin-up, draining) say nothing about the new state.
            if (reading.Time < slot.Since + grace) return;

            if (!slot.On)
            {
                if (watch.MaxFlowWhenOff is { } maxOff && reading.Value > maxOff)
                {
                    ReportLimited(slot, SafetyEventCodes.UnexpectedFlow, FlowEventInterval, EventSeverities.Warning,
                        $"'{name}' is off but {Num(reading.Value)} L/min is flowing (at most {Num(maxOff)} expected when off): leak, stuck valve or another pump feeding this line",
                        SafetyEventCodes.UnexpectedFlow);
                }
                return;
            }
            if (reading.Value >= watch.MinFlowWhenOn)
            {
                slot.FlowSeen = true;
                slot.StallRetryAt = null;
                return;
            }

            var what = slot.FlowSeen
                ? $"flow dropped to {Num(reading.Value)} L/min (minimum {Num(watch.MinFlowWhenOn)}) while running"
                : $"{(reading.Value < 0.05 ? "no" : "low")} flow ({Num(reading.Value)} L/min) for {Secs(now - slot.Since)} s";
            var message = $"{name}: {what} — pump stopped, clogged or reservoir empty";
            if (watch.StopOnStall) message += slot.Config.ContinuousRun ? $"; switched off, retrying in {Math.Max(1, watch.StallRetrySec)} s" : "; switched off";
            ReportLimited(slot, SafetyEventCodes.FlowStall, FlowEventInterval, EventSeverities.Critical, message, SafetyEventCodes.FlowStall);
            if (!watch.StopOnStall) return;
            await StopLockedAsync(slot, StopReasons.FlowStall, message);
            slot.StallRetryAt = now + TimeSpan.FromSeconds(Math.Max(1, watch.StallRetrySec));
            slot.NextContinuousAttempt = slot.StallRetryAt.Value;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Stops a running actuator whose <c>RequiresActuatorOn</c> actuator is no longer on (e.g. a doser when the circulation pump stops).</summary>
    private async Task EnforceRequiredActuatorAsync(Slot slot)
    {
        if (!slot.On || slot.Config.Safety?.RequiresActuatorOn is not { } requiredId || requiredId == slot.Config.Id || IsOn(requiredId)) return;
        await slot.Gate.WaitAsync();
        try
        {
            if (!slot.On || SafetyGuard.CheckRequiredActuator(slot.Config, Peers()) is not { } reason) return;
            await StopLockedAsync(slot, StopReasons.Interlock, reason);
            _logger.LogWarning("Safety: {ActuatorName} switched off: {Reason}", slot.Config.Name, reason);
            _reporter.Event(EventSeverities.Critical, EventCategories.Safety, $"'{slot.Config.Name}' switched off: {reason}", actuatorId: slot.Config.Id,
                code: SafetyEventCodes.PumpNotRunning);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Reports a safety event at most once per <paramref name="interval"/> per actuator and key. Caller holds the slot gate.</summary>
    private void ReportLimited(Slot slot, string key, TimeSpan interval, string severity, string message, string? code)
    {
        var now = _time.GetUtcNow();
        if (slot.LastEvents.TryGetValue(key, out var last) && now - last < interval) return;
        slot.LastEvents[key] = now;
        _logger.Log(severity == EventSeverities.Critical ? LogLevel.Error : LogLevel.Warning, "Safety: {Message}", message);
        _reporter.Event(severity, EventCategories.Safety, message, actuatorId: slot.Config.Id, code: code);
    }

    private ActuatorPeers Peers() => new(_slots.Values.Select(s => s.Config).ToList(), IsOn);

    private static string Num(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Secs(TimeSpan t) => Math.Round(t.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static string ConfigJson(ActuatorConfig cfg) => JsonSerializer.Serialize(cfg, ClientJson.Options);

    private static bool SameHardware(ActuatorConfig a, ActuatorConfig b) =>
        a.PluginId == b.PluginId && a.Kind == b.Kind &&
        JsonSerializer.Serialize(a.Settings, ClientJson.Options) == JsonSerializer.Serialize(b.Settings, ClientJson.Options);

    private sealed record ActiveRun(Guid RunId, Guid? CommandId, Guid? ScheduleId, string Source, DateTimeOffset StartedAt, double? FlowMlPerSec, string PlannedStopReason, bool AckOnEnd)
    {
        public CancellationTokenSource Timer { get; } = new();
    }

    private sealed class Slot(ActuatorConfig config, IActuatorPlugin? plugin, DateTimeOffset since)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ActuatorConfig Config { get; set; } = config;
        public IActuatorPlugin? Plugin { get; } = plugin;
        public IActuator? Device { get; set; }
        public string? CreateError { get; set; }
        public bool On { get; set; }
        public bool OffFailed { get; set; }
        public double? Level { get; set; }
        public DateTimeOffset Since { get; set; } = since;
        public ActiveRun? Run { get; set; }
        /// <summary>An explicit off keeps a continuous run off until an explicit start or a change of this actuator's config.</summary>
        public bool OffLatched { get; set; }
        /// <summary>After a flow stall, continuous re-assertion waits until this time.</summary>
        public DateTimeOffset? StallRetryAt { get; set; }
        public DateTimeOffset NextContinuousAttempt { get; set; }
        /// <summary>The flow watchdog has seen sufficient flow during the current run.</summary>
        public bool FlowSeen { get; set; }
        public Dictionary<string, DateTimeOffset> LastEvents { get; } = [];
    }
}
