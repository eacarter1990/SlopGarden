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
}

public sealed record CommandOutcome(string Status, string? Reason);

/// <summary>Owns the actuator instances and executes every on/off/pulse/dose request through the <see cref="SafetyGuard"/>.</summary>
public sealed class ActuatorController : IAsyncDisposable
{
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

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
                        existing.Config = cfg;
                        if (!cfg.Enabled && existing.On) await StopLockedAsync(existing, StopReasons.Reconfigured, "actuator disabled");
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

    private async Task<CommandOutcome> StartAsync(Slot slot, ActuatorRequest request, string source, Guid? commandId, Guid? scheduleId)
    {
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Device is null) return new CommandOutcome(CommandStatuses.Failed, $"actuator not available: {slot.CreateError ?? "not created"}");

            if (request.Action == ActuatorActions.Off)
            {
                if (slot.On) await StopLockedAsync(slot, StopReasons.OffCommand, "switched off");
                else if (!await TrySetDeviceAsync(slot, false)) return new CommandOutcome(CommandStatuses.Failed, "could not switch off");
                if (commandId is { } offId) _reporter.Ack(offId, CommandStatuses.Completed, null);
                return new CommandOutcome(CommandStatuses.Completed, null);
            }

            var decision = _guard.Evaluate(slot.Config, _timeZoneId, _emergencyStop, request, running: slot.On);
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
            _logger.LogInformation("{ActuatorName} on ({Source}{Duration}){Note}", slot.Config.Name, source,
                decision.DurationSec is { } d ? $", {d.ToString("0.#", CultureInfo.InvariantCulture)}s" : "", decision.Note is { } n ? $": {n}" : "");
            _reporter.ActuatorState(new ActuatorStateReport(slot.Config.Id, true, request.Level, now, commandId, source));
            if (commandId is { } id)
            {
                _reporter.Ack(id, CommandStatuses.Running, decision.Note);
                if (!ackOnEnd) _reporter.Ack(id, CommandStatuses.Completed, decision.Note);
            }
            if (decision.DurationSec is { } duration) StartTimer(slot, run, TimeSpan.FromSeconds(duration));
            return new CommandOutcome(CommandStatuses.Running, decision.Note);
        }
        finally
        {
            slot.Gate.Release();
        }
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
                StopReasons.Completed or StopReasons.MaxRunTime or StopReasons.DailyLimit => CommandStatuses.Completed,
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch {ActuatorName} {State}", slot.Config.Name, on ? "on" : "off");
            _reporter.Event(on ? EventSeverities.Error : EventSeverities.Critical, EventCategories.Actuator,
                $"Could not switch '{slot.Config.Name}' {(on ? "on" : "off")}: {ex.Message}", actuatorId: slot.Config.Id);
            return false;
        }
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
            _reporter.Event(EventSeverities.Critical, EventCategories.Safety, $"'{slot.Config.Name}' switched off: {reason}", actuatorId: slot.Config.Id);
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
        public double? Level { get; set; }
        public DateTimeOffset Since { get; set; } = since;
        public ActiveRun? Run { get; set; }
    }
}
