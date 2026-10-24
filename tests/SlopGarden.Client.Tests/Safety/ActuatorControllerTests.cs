using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Server;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Tests.Safety;

public sealed class ActuatorControllerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryRunLedger _ledger = new();
    private readonly ChannelValueStore _values = new();
    private readonly RecordingReporter _reporter = new();
    private readonly FakeActuatorPlugin _plugin = new();
    private readonly ActuatorController _controller;

    public ActuatorControllerTests()
    {
        var registry = new PluginRegistry([], [], NullLogger<PluginRegistry>.Instance);
        registry.Load();
        registry.RegisterBuiltin(_plugin);
        _controller = new ActuatorController(registry, new SafetyGuard(_ledger, _values, _time), _ledger, _values, _reporter,
            Configs.EmptyServices, _time, Configs.NullLogs);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _controller.DisposeAsync();

    private async Task<FakeActuator> ApplyAsync(ActuatorConfig actuator, EmergencyStopState? estop = null, string tz = "UTC")
    {
        var errors = new List<string>();
        await _controller.ApplyAsync(Configs.Document(timeZoneId: tz, actuators: [actuator], estop: estop), errors);
        Assert.Empty(errors);
        return _plugin.Devices[actuator.Id];
    }

    private ActuatorCommandMessage Command(ActuatorConfig a, string action, double? duration = null, double? amount = null, TimeSpan? expiresIn = null) =>
        new(Guid.NewGuid(), a.Id, action, duration, amount, null, _time.GetUtcNow(), _time.GetUtcNow() + (expiresIn ?? TimeSpan.FromMinutes(5)), "test", null);

    /// <summary>Advances fake time in small steps (timers are armed on background tasks) until the condition holds.</summary>
    private async Task<TimeSpan> AdvanceUntilAsync(Func<bool> condition, TimeSpan step, TimeSpan max)
    {
        var advanced = TimeSpan.Zero;
        while (!condition())
        {
            if (advanced >= max) throw new TimeoutException($"condition not met after advancing {advanced}");
            await Task.Delay(5);
            _time.Advance(step);
            advanced += step;
        }
        return advanced;
    }

    [Fact]
    public async Task Apply_creates_actuator_in_failsafe_off_state()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        Assert.False(device.On);
        Assert.Equal([false], device.Changes);
        Assert.Contains(_reporter.States, s => s.ActuatorId == a.Id && !s.On && s.Reason == "failsafe");
    }

    [Fact]
    public async Task Failsafe_on_actuator_starts_on_unless_emergency_stopped()
    {
        var a = Configs.Actuator(failsafe: "on", kind: ActuatorKinds.Fan);
        var device = await ApplyAsync(a);
        Assert.True(device.On);
        Assert.True(_controller.IsOn(a.Id));

        await _controller.SetEmergencyStopAsync(new EmergencyStopState(true, [], "test"));
        Assert.False(device.On);
    }

    [Fact]
    public async Task Pulse_command_switches_on_then_off_and_reports_the_run()
    {
        var a = Configs.Actuator(flow: 120);
        var device = await ApplyAsync(a);
        var cmd = Command(a, ActuatorActions.Pulse, duration: 30);

        var outcome = await _controller.ExecuteCommandAsync(cmd);

        Assert.Equal(CommandStatuses.Running, outcome.Status);
        Assert.True(device.On);
        Assert.Equal([CommandStatuses.Received, CommandStatuses.Running], _reporter.AckStatuses(cmd.CommandId));

        var advanced = await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        Assert.InRange(advanced.TotalSeconds, 30, 31);
        await Wait.UntilAsync(() => _reporter.AckStatuses(cmd.CommandId).Contains(CommandStatuses.Completed), Timeout, "the pulse is acked completed");

        var run = Assert.Single(_reporter.Runs);
        Assert.Equal(cmd.CommandId, run.CommandId);
        Assert.Equal(RunSources.Command, run.Source);
        Assert.Equal(StopReasons.Completed, run.StopReason);
        Assert.InRange(run.DurationSec, 30, 31);
        Assert.InRange(run.AmountMl!.Value, 60, 62);
        Assert.Equal(run.EndedAt, _ledger.LastEnd(a.Id));
    }

    [Fact]
    public async Task Expired_command_is_acked_expired_and_ignored()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        var cmd = Command(a, ActuatorActions.Pulse, duration: 10, expiresIn: TimeSpan.FromSeconds(-1));

        var outcome = await _controller.ExecuteCommandAsync(cmd);

        Assert.Equal(CommandStatuses.Expired, outcome.Status);
        Assert.Equal([CommandStatuses.Expired], _reporter.AckStatuses(cmd.CommandId));
        Assert.False(device.On);
        Assert.Equal([false], device.Changes);
    }

    [Fact]
    public async Task Command_for_unknown_actuator_is_rejected()
    {
        await ApplyAsync(Configs.Actuator());
        var cmd = Command(Configs.Actuator(), ActuatorActions.Pulse, duration: 10);
        var outcome = await _controller.ExecuteCommandAsync(cmd);
        Assert.Equal(CommandStatuses.Rejected, outcome.Status);
        Assert.Equal([CommandStatuses.Received, CommandStatuses.Rejected], _reporter.AckStatuses(cmd.CommandId));
    }

    [Fact]
    public async Task Over_limit_command_is_rejected_and_never_switches_on()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 60));
        var device = await ApplyAsync(a);
        var cmd = Command(a, ActuatorActions.Pulse, duration: 120);

        var outcome = await _controller.ExecuteCommandAsync(cmd);

        Assert.Equal(CommandStatuses.Rejected, outcome.Status);
        Assert.Contains("daily run limit", outcome.Reason);
        var last = _reporter.Acks.Last(x => x.CommandId == cmd.CommandId);
        Assert.Equal(CommandStatuses.Rejected, last.Status);
        Assert.Contains("daily run limit", last.Reason);
        Assert.DoesNotContain(true, device.Changes);
    }

    [Fact]
    public async Task Daily_limit_accumulates_over_completed_runs()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 300));
        var device = await ApplyAsync(a);
        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 200))).Status);
        await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the first run is recorded");

        var second = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 200));
        Assert.Equal(CommandStatuses.Rejected, second.Status);
        Assert.Contains("daily run limit", second.Reason);
    }

    [Fact]
    public async Task Minimum_interval_applies_after_a_run()
    {
        var a = Configs.Actuator(safety: Configs.Safety(minBetween: 600));
        var device = await ApplyAsync(a);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 10));
        await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the first run is recorded");

        var tooSoon = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 10));
        Assert.Equal(CommandStatuses.Rejected, tooSoon.Status);

        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 10))).Status);
    }

    [Fact]
    public async Task Interlock_tripping_while_running_switches_off_immediately()
    {
        var tank = Guid.NewGuid();
        var a = Configs.Actuator(kind: ActuatorKinds.Pump, safety: Configs.Safety(maxRun: 600,
            interlocks: new SafetyInterlock("min", tank, "lt", 15, "tank level below 15%")));
        var device = await ApplyAsync(a);
        _values.Set(tank, new ChannelValue(60, _time.GetUtcNow(), ReadingQuality.Good));
        var cmd = Command(a, ActuatorActions.Pulse, duration: 300);
        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(cmd)).Status);
        _time.Advance(TimeSpan.FromSeconds(42));

        _values.Set(tank, new ChannelValue(12, _time.GetUtcNow(), ReadingQuality.Good));

        await Wait.UntilAsync(() => !device.On, Timeout, "the interlock switches the pump off");
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the run is reported");
        var run = Assert.Single(_reporter.Runs);
        Assert.Equal(StopReasons.Interlock, run.StopReason);
        Assert.Equal(42, run.DurationSec, 3);
        await Wait.UntilAsync(() => _reporter.AckStatuses(cmd.CommandId).Contains(CommandStatuses.Cancelled), Timeout, "the command is acked cancelled");
        Assert.Contains(_reporter.Events, e => e.Severity == EventSeverities.Critical && e.Category == EventCategories.Safety && e.ActuatorId == a.Id);
        Assert.False(_controller.IsOn(a.Id));

        var retry = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 10));
        Assert.Equal(CommandStatuses.Rejected, retry.Status);
        Assert.Contains("tank level below 15%", retry.Reason);
    }

    [Fact]
    public async Task Unrelated_channel_updates_do_not_stop_a_running_actuator()
    {
        var tank = Guid.NewGuid();
        var a = Configs.Actuator(safety: Configs.Safety(interlocks: new SafetyInterlock("min", tank, "lt", 15, "tank")));
        var device = await ApplyAsync(a);
        _values.Set(tank, new ChannelValue(60, _time.GetUtcNow(), ReadingQuality.Good));
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 300));
        _values.Set(Guid.NewGuid(), new ChannelValue(0, _time.GetUtcNow(), ReadingQuality.Good));
        _values.Set(tank, new ChannelValue(50, _time.GetUtcNow(), ReadingQuality.Good));
        await Task.Delay(50);
        Assert.True(device.On);
    }

    [Fact]
    public async Task Emergency_stop_switches_running_actuators_off_and_blocks_new_runs()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        var cmd = Command(a, ActuatorActions.Pulse, duration: 300);
        await _controller.ExecuteCommandAsync(cmd);
        Assert.True(device.On);

        await _controller.SetEmergencyStopAsync(new EmergencyStopState(true, [], "pipe burst"));

        Assert.False(device.On);
        var run = Assert.Single(_reporter.Runs);
        Assert.Equal(StopReasons.EmergencyStop, run.StopReason);
        Assert.Contains(CommandStatuses.Cancelled, _reporter.AckStatuses(cmd.CommandId));
        Assert.Contains(_reporter.Events, e => e.Severity == EventSeverities.Critical && e.Message.Contains("pipe burst"));

        var blocked = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.On));
        Assert.Equal(CommandStatuses.Rejected, blocked.Status);
        Assert.Contains("emergency stop", blocked.Reason);

        await _controller.SetEmergencyStopAsync(EmergencyStopState.None);
        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 5))).Status);
    }

    [Fact]
    public async Task Emergency_stop_in_config_document_is_applied()
    {
        var a = Configs.Actuator();
        await ApplyAsync(a, new EmergencyStopState(false, [a.Id], null));
        var outcome = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 5));
        Assert.Equal(CommandStatuses.Rejected, outcome.Status);
    }

    [Fact]
    public async Task Off_command_ends_a_running_pulse()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        var pulse = Command(a, ActuatorActions.Pulse, duration: 300);
        await _controller.ExecuteCommandAsync(pulse);
        _time.Advance(TimeSpan.FromSeconds(20));

        var off = Command(a, ActuatorActions.Off);
        var outcome = await _controller.ExecuteCommandAsync(off);

        Assert.Equal(CommandStatuses.Completed, outcome.Status);
        Assert.False(device.On);
        Assert.Equal(StopReasons.OffCommand, Assert.Single(_reporter.Runs).StopReason);
        Assert.Contains(CommandStatuses.Cancelled, _reporter.AckStatuses(pulse.CommandId));
        Assert.Equal([CommandStatuses.Received, CommandStatuses.Completed], _reporter.AckStatuses(off.CommandId));

        // The cancelled timer must not switch anything later.
        _time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(20);
        Assert.Single(_reporter.Runs);
    }

    [Fact]
    public async Task On_command_completes_immediately_and_auto_offs_at_max_run_time()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxRun: 60));
        var device = await ApplyAsync(a);
        var cmd = Command(a, ActuatorActions.On);

        var outcome = await _controller.ExecuteCommandAsync(cmd);

        Assert.Equal(CommandStatuses.Running, outcome.Status);
        Assert.Equal([CommandStatuses.Received, CommandStatuses.Running, CommandStatuses.Completed], _reporter.AckStatuses(cmd.CommandId));
        await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the run is reported");
        Assert.Equal(StopReasons.MaxRunTime, _reporter.Runs.Single().StopReason);
    }

    [Fact]
    public async Task Switch_on_failure_is_reported_and_the_actuator_forced_off()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        device.FailOn = true;
        var cmd = Command(a, ActuatorActions.Pulse, duration: 10);

        var outcome = await _controller.ExecuteCommandAsync(cmd);

        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Contains(CommandStatuses.Failed, _reporter.AckStatuses(cmd.CommandId));
        Assert.False(_controller.IsOn(a.Id));
        Assert.Contains(_reporter.Events, e => e.Category == EventCategories.Actuator && e.Severity == EventSeverities.Error);
    }

    [Fact]
    public async Task Switch_off_failure_is_critical_and_retried_until_the_output_is_off()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.On));
        device.FailOff = true;
        var off = Command(a, ActuatorActions.Off);

        var outcome = await _controller.ExecuteCommandAsync(off);

        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Contains(CommandStatuses.Failed, _reporter.AckStatuses(off.CommandId));
        Assert.True(_controller.IsOn(a.Id));
        Assert.Contains(_reporter.Events, e => e.Severity == EventSeverities.Critical && e.Message.Contains("could not switch", StringComparison.OrdinalIgnoreCase));

        device.FailOff = false;
        await AdvanceUntilAsync(() => !_controller.IsOn(a.Id), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Assert.False(device.On);
    }

    [Fact]
    public async Task Plugin_failing_to_create_reports_a_config_error_without_throwing()
    {
        _plugin.ThrowOnCreate = true;
        var a = Configs.Actuator();
        var errors = new List<string>();
        await _controller.ApplyAsync(Configs.Document(actuators: [a]), errors);

        Assert.Contains(errors, e => e.Contains("hardware missing"));
        var outcome = await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 5));
        Assert.Equal(CommandStatuses.Failed, outcome.Status);
        Assert.Contains("not available", outcome.Reason);
    }

    [Fact]
    public async Task Unknown_plugin_is_a_config_error()
    {
        var errors = new List<string>();
        await _controller.ApplyAsync(Configs.Document(actuators: [Configs.Actuator(pluginId: "nope")]), errors);
        Assert.Contains(errors, e => e.Contains("unknown or unavailable plugin 'nope'"));
    }

    [Fact]
    public async Task Disabling_a_running_actuator_stops_it()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 300));

        await _controller.ApplyAsync(Configs.Document(actuators: [a with { Enabled = false }]), []);

        Assert.False(device.On);
        Assert.Equal(StopReasons.Reconfigured, Assert.Single(_reporter.Runs).StopReason);
    }

    [Fact]
    public async Task Removing_a_running_actuator_stops_and_disposes_it()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 300));

        await _controller.ApplyAsync(Configs.Document(), []);

        Assert.False(device.On);
        Assert.True(device.Disposed);
        Assert.Equal(StopReasons.Reconfigured, Assert.Single(_reporter.Runs).StopReason);
    }

    [Fact]
    public async Task Changing_hardware_settings_recreates_the_device()
    {
        var a = Configs.Actuator(settings: new() { ["pin"] = 17 });
        var first = await ApplyAsync(a);
        await _controller.ApplyAsync(Configs.Document(actuators: [a with { Settings = new() { ["pin"] = 27 } }]), []);
        Assert.True(first.Disposed);
        Assert.NotSame(first, _plugin.Devices[a.Id]);
    }

    [Fact]
    public async Task Changing_only_limits_keeps_the_device_and_applies_new_limits()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        await _controller.ApplyAsync(Configs.Document(actuators: [a with { Safety = Configs.Safety(maxRun: 5) }]), []);
        Assert.False(device.Disposed);
        Assert.Same(device, _plugin.Devices[a.Id]);

        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 100));
        var advanced = await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.InRange(advanced.TotalSeconds, 5, 6);
    }

    [Fact]
    public async Task Schedule_dose_runs_for_amount_over_flow_rate()
    {
        var a = Configs.Actuator(flow: 60, kind: ActuatorKinds.FertilizerDoser);
        var device = await ApplyAsync(a);
        var schedule = new ScheduleConfig(Guid.NewGuid(), "Feed", a.Id, "0 7 * * *", "UTC", 0, 20, true);

        var outcome = await _controller.RunScheduleAsync(schedule);

        Assert.Equal(CommandStatuses.Running, outcome.Status);
        var advanced = await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.InRange(advanced.TotalSeconds, 20, 21);
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the run is reported");
        var run = _reporter.Runs.Single();
        Assert.Equal(RunSources.Schedule, run.Source);
        Assert.Equal(schedule.Id, run.ScheduleId);
        Assert.Null(run.CommandId);
    }

    [Fact]
    public async Task Rejected_schedule_raises_a_safety_warning()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 10));
        await ApplyAsync(a);
        var schedule = new ScheduleConfig(Guid.NewGuid(), "Morning", a.Id, "0 6 * * *", "UTC", 60, null, true);

        var outcome = await _controller.RunScheduleAsync(schedule);

        Assert.Equal(CommandStatuses.Rejected, outcome.Status);
        Assert.Contains(_reporter.Events, e => e.Category == EventCategories.Safety && e.Severity == EventSeverities.Warning && e.Message.Contains("Morning"));
    }

    [Fact]
    public async Task Local_requests_are_subject_to_the_same_limits()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxRun: 10));
        var device = await ApplyAsync(a);
        var outcome = await _controller.ExecuteLocalAsync(a.Id, new ActuatorRequest(ActuatorActions.Pulse, DurationSec: 600));
        Assert.Equal(CommandStatuses.Running, outcome.Status);
        Assert.Contains("capped", outcome.Reason);
        var advanced = await AdvanceUntilAsync(() => !device.On, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.InRange(advanced.TotalSeconds, 10, 11);
    }

    [Fact]
    public async Task Shutdown_switches_everything_to_failsafe_and_reports_runs()
    {
        var a = Configs.Actuator();
        var device = await ApplyAsync(a);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Pulse, duration: 300));

        await _controller.ShutdownAsync();

        Assert.False(device.On);
        Assert.True(device.Disposed);
        Assert.Equal(StopReasons.Shutdown, Assert.Single(_reporter.Runs).StopReason);
    }

    [Fact]
    public async Task Current_states_reflect_running_actuators()
    {
        var a = Configs.Actuator();
        var b = Configs.Actuator();
        await _controller.ApplyAsync(Configs.Document(actuators: [a, b]), []);
        await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.On));
        var states = _controller.CurrentStates().ToDictionary(s => s.ActuatorId);
        Assert.True(states[a.Id].On);
        Assert.False(states[b.Id].On);
    }
}
