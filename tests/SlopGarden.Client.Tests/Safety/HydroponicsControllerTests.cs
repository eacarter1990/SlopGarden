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

/// <summary>Continuous runs, the flow watchdog and reservoir dosing through <see cref="ActuatorController"/>.</summary>
public sealed class HydroponicsControllerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryRunLedger _ledger = new();
    private readonly ChannelValueStore _values = new();
    private readonly RecordingReporter _reporter = new();
    private readonly FakeActuatorPlugin _plugin = new();
    private readonly ActuatorController _controller;
    private readonly Guid _flowChannel = Guid.NewGuid();
    private int _version;

    public HydroponicsControllerTests()
    {
        var registry = new PluginRegistry([], [], NullLogger<PluginRegistry>.Instance);
        registry.Load();
        registry.RegisterBuiltin(_plugin);
        _controller = new ActuatorController(registry, new SafetyGuard(_ledger, _values, _time), _ledger, _values, _reporter,
            Configs.EmptyServices, _time, Configs.NullLogs);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _controller.DisposeAsync();

    private ActuatorConfig Pump(ActuatorSafety? safety = null, FlowWatch? watch = null, bool continuous = true) =>
        Configs.Actuator(kind: ActuatorKinds.Pump, safety: safety)
        with { Name = "NFT pump", Role = ActuatorRoles.CirculationPump, ContinuousRun = continuous, FlowWatch = watch };

    private FlowWatch Watch(double minOn = 1.0, int grace = 20, double? maxOff = null, bool stop = true, int retry = 300) =>
        new(_flowChannel, minOn, grace, maxOff, stop, retry);

    private static ActuatorConfig Doser(string name, string role, Guid? reservoir = null, int? reservoirWait = null, Guid? requires = null) =>
        Configs.Actuator(flow: 60, kind: ActuatorKinds.FertilizerDoser,
            safety: new ActuatorSafety(null, null, null, null, [], null, reservoirWait, requires))
        with { Name = name, Role = role, ReservoirId = reservoir };

    private async Task ApplyAsync(EmergencyStopState? estop = null, int? version = null, params ActuatorConfig[] actuators)
    {
        var errors = new List<string>();
        await _controller.ApplyAsync(Configs.Document(version: version ?? ++_version, actuators: actuators, estop: estop), errors);
        Assert.Empty(errors);
    }

    private Task ApplyAsync(params ActuatorConfig[] actuators) => ApplyAsync(null, null, actuators);

    private ActuatorCommandMessage Command(ActuatorConfig a, string action, double? duration = null, double? amount = null) =>
        new(Guid.NewGuid(), a.Id, action, duration, amount, null, _time.GetUtcNow(), _time.GetUtcNow() + TimeSpan.FromMinutes(5), "test", null);

    /// <summary>Advances fake time and runs one supervision pass, as the background supervisor does every second.</summary>
    private async Task TickAsync(TimeSpan advance)
    {
        _time.Advance(advance);
        await _controller.SuperviseAsync();
    }

    private void Flow(double litersPerMinute, short quality = ReadingQuality.Good) =>
        _values.Set(_flowChannel, new ChannelValue(litersPerMinute, _time.GetUtcNow(), quality));

    private IReadOnlyList<DeviceEventReport> EventsWith(string? code) => _reporter.Events.Where(e => e.Code == code).ToList();

    [Fact]
    public async Task Continuous_run_starts_on_apply_and_is_not_capped_by_run_limits()
    {
        var pump = Pump(Configs.Safety(maxRun: 60, maxDaily: 120, minBetween: 600));
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];

        Assert.True(device.On);
        Assert.Contains(_reporter.States, s => s.ActuatorId == pump.Id && s.On && s.Reason == "continuous run");
        Assert.Contains(_controller.CurrentStates(), s => s.ActuatorId == pump.Id && s.On && s.Reason == RunSources.Continuous);

        for (var i = 0; i < 10; i++) await TickAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.True(device.On);
        Assert.Empty(_reporter.Runs);
    }

    [Fact]
    public async Task Continuous_run_is_reasserted_after_an_interlock_stop_and_reports_the_ended_run()
    {
        var tank = Guid.NewGuid();
        _values.Set(tank, new ChannelValue(60, _time.GetUtcNow(), ReadingQuality.Good));
        var pump = Pump(Configs.Safety(interlocks: new SafetyInterlock("min", tank, "lt", 15, "reservoir below 15%")));
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];
        Assert.True(device.On);

        _time.Advance(TimeSpan.FromMinutes(10));
        _values.Set(tank, new ChannelValue(10, _time.GetUtcNow(), ReadingQuality.Good));
        await Wait.UntilAsync(() => !device.On, Timeout, "the interlock stops the pump");
        await Wait.UntilAsync(() => _reporter.Runs.Count == 1, Timeout, "the continuous run is reported");
        var run = Assert.Single(_reporter.Runs);
        Assert.Equal((RunSources.Continuous, StopReasons.Interlock), (run.Source, run.StopReason));
        Assert.Equal(600, run.DurationSec, 1);
        await Wait.UntilAsync(() => EventsWith(SafetyEventCodes.InterlockTripped).Count == 1, Timeout, "the interlock trip is reported");

        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.False(device.On);
        Assert.Single(EventsWith(SafetyEventCodes.PumpNotRunning));
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.Single(EventsWith(SafetyEventCodes.PumpNotRunning));

        _values.Set(tank, new ChannelValue(70, _time.GetUtcNow(), ReadingQuality.Good));
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.True(device.On);
    }

    [Fact]
    public async Task Explicit_off_latches_a_continuous_run_until_an_explicit_start_or_a_config_change()
    {
        var pump = Pump();
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];
        Assert.True(device.On);

        Assert.Equal(CommandStatuses.Completed, (await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.Off))).Status);
        Assert.False(device.On);
        var run = Assert.Single(_reporter.Runs);
        Assert.Equal(StopReasons.OffCommand, run.StopReason);

        await TickAsync(TimeSpan.FromMinutes(2));
        await ApplyAsync(null, _version, pump);
        await ApplyAsync(pump);
        await TickAsync(TimeSpan.FromMinutes(2));
        Assert.False(device.On);

        await ApplyAsync(pump with { Name = "NFT pump (renamed)" });
        Assert.True(device.On);

        await _controller.ExecuteLocalAsync(pump.Id, new ActuatorRequest(ActuatorActions.Off));
        await TickAsync(TimeSpan.FromMinutes(1));
        Assert.False(device.On);
        var pulse = await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.Pulse, duration: 10));
        Assert.Equal(CommandStatuses.Running, pulse.Status);
        for (var i = 0; i < 20 && device.On; i++)
        {
            await Task.Delay(5);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        await Wait.UntilAsync(() => !device.On, Timeout, "the pulse ends");
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.True(device.On);
    }

    [Fact]
    public async Task Disabling_continuous_run_stops_the_pump()
    {
        var pump = Pump();
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];
        await ApplyAsync(pump with { ContinuousRun = false });
        Assert.False(device.On);
        Assert.Equal(StopReasons.Reconfigured, Assert.Single(_reporter.Runs).StopReason);
        await TickAsync(TimeSpan.FromMinutes(1));
        Assert.False(device.On);
    }

    [Fact]
    public async Task Emergency_stop_forces_a_continuous_run_off_until_cleared()
    {
        var pump = Pump();
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];

        await _controller.SetEmergencyStopAsync(new EmergencyStopState(true, [], "leak"));
        Assert.False(device.On);
        Assert.Equal(StopReasons.EmergencyStop, Assert.Single(_reporter.Runs).StopReason);
        await TickAsync(TimeSpan.FromMinutes(5));
        Assert.False(device.On);

        await ApplyAsync(new EmergencyStopState(false, [pump.Id], "pump only"), null, pump);
        Assert.False(device.On);

        await _controller.SetEmergencyStopAsync(EmergencyStopState.None);
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.True(device.On);
    }

    [Fact]
    public async Task Flow_stall_raises_a_critical_event_stops_the_pump_and_retries_after_the_stall_retry_interval()
    {
        var pump = Pump(watch: Watch(grace: 20, retry: 300));
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];

        _time.Advance(TimeSpan.FromSeconds(5));
        Flow(0);
        await TickAsync(TimeSpan.FromSeconds(10));
        Assert.True(device.On);

        _time.Advance(TimeSpan.FromSeconds(10));
        Flow(0);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.False(device.On);
        var stall = Assert.Single(EventsWith(SafetyEventCodes.FlowStall));
        Assert.Equal((EventSeverities.Critical, EventCategories.Safety, pump.Id), (stall.Severity, stall.Category, stall.ActuatorId));
        Assert.StartsWith("NFT pump: no flow (0.0 L/min) for 26 s", stall.Message);
        Assert.Contains("retrying in 300 s", stall.Message);
        var run = Assert.Single(_reporter.Runs);
        Assert.Equal((RunSources.Continuous, StopReasons.FlowStall), (run.Source, run.StopReason));

        for (var i = 0; i < 9; i++) await TickAsync(TimeSpan.FromSeconds(30));
        Assert.False(device.On);
        await TickAsync(TimeSpan.FromSeconds(30));
        Assert.True(device.On);

        _time.Advance(TimeSpan.FromSeconds(21));
        Flow(0);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.False(device.On);
        Assert.Equal(2, _reporter.Runs.Count);

        await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.On));
        Assert.True(device.On);
        _time.Advance(TimeSpan.FromSeconds(21));
        Flow(3.1);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.True(device.On);

        await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.Off));
        Flow(0.1);
        await TickAsync(TimeSpan.FromSeconds(30));
        await ApplyAsync(pump with { ReservoirId = Guid.NewGuid() });
        Assert.True(device.On);
    }

    [Fact]
    public async Task Flow_drop_while_running_is_reported_and_rate_limited_when_not_stopping()
    {
        var pump = Pump(watch: Watch(grace: 10, stop: false));
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];
        _time.Advance(TimeSpan.FromSeconds(15));
        Flow(3.2);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(EventsWith(SafetyEventCodes.FlowStall));

        Flow(0.4);
        await TickAsync(TimeSpan.FromSeconds(1));
        var stall = Assert.Single(EventsWith(SafetyEventCodes.FlowStall));
        Assert.Contains("flow dropped to 0.4 L/min (minimum 1.0) while running", stall.Message);
        Assert.True(device.On);

        for (var i = 0; i < 4; i++)
        {
            Flow(0.4);
            await TickAsync(TimeSpan.FromSeconds(55));
        }
        Assert.Single(EventsWith(SafetyEventCodes.FlowStall));
        _time.Advance(TimeSpan.FromSeconds(80));
        Flow(0.4);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, EventsWith(SafetyEventCodes.FlowStall).Count);
        Assert.True(device.On);
    }

    [Fact]
    public async Task Unexpected_flow_while_off_raises_a_rate_limited_warning()
    {
        var valve = Configs.Actuator(kind: ActuatorKinds.Valve) with { Name = "Top-up valve", Role = ActuatorRoles.TopUpValve, FlowWatch = Watch(grace: 10, maxOff: 0.2) };
        await ApplyAsync(valve);

        _time.Advance(TimeSpan.FromSeconds(5));
        Flow(1.5);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(EventsWith(SafetyEventCodes.UnexpectedFlow));

        _time.Advance(TimeSpan.FromSeconds(10));
        Flow(1.5);
        await TickAsync(TimeSpan.FromSeconds(1));
        var warning = Assert.Single(EventsWith(SafetyEventCodes.UnexpectedFlow));
        Assert.Equal((EventSeverities.Warning, valve.Id), (warning.Severity, warning.ActuatorId));
        Assert.Contains("is off but 1.5 L/min is flowing", warning.Message);

        _time.Advance(TimeSpan.FromMinutes(2));
        Flow(1.5);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Single(EventsWith(SafetyEventCodes.UnexpectedFlow));
        _time.Advance(TimeSpan.FromMinutes(4));
        Flow(0.1);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Single(EventsWith(SafetyEventCodes.UnexpectedFlow));
    }

    [Fact]
    public async Task Silent_flow_sensor_warns_without_stopping()
    {
        var pump = Pump(watch: Watch(grace: 10));
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];

        await TickAsync(TimeSpan.FromSeconds(29));
        Assert.Empty(_reporter.Events);
        await TickAsync(TimeSpan.FromSeconds(2));
        var silent = Assert.Single(_reporter.Events);
        Assert.Equal((EventSeverities.Warning, EventCategories.Safety, (string?)null), (silent.Severity, silent.Category, silent.Code));
        Assert.Contains("flow sensor sent no reading", silent.Message);
        Assert.True(device.On);

        await TickAsync(TimeSpan.FromMinutes(10));
        Assert.Single(_reporter.Events);

        _values.Set(_flowChannel, new ChannelValue(3, _time.GetUtcNow() - TimeSpan.FromMinutes(2), ReadingQuality.Good));
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Single(_reporter.Events);

        Flow(3);
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.Single(_reporter.Events);
        await TickAsync(TimeSpan.FromSeconds(61));
        Assert.Equal(2, _reporter.Events.Count);
        Assert.True(device.On);
    }

    [Fact]
    public async Task Dosing_requires_the_circulation_pump_and_stops_when_it_goes_off()
    {
        var pump = Pump();
        var doser = Doser("pH down", ActuatorRoles.DosePhDown, requires: pump.Id);
        await ApplyAsync(pump, doser);
        var doserDevice = _plugin.Devices[doser.Id];

        await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.Off));
        var rejected = await _controller.ExecuteCommandAsync(Command(doser, ActuatorActions.Dose, amount: 5));
        Assert.Equal(CommandStatuses.Rejected, rejected.Status);
        Assert.Equal("requires 'NFT pump' to be on, but it is off", rejected.Reason);

        await _controller.ExecuteCommandAsync(Command(pump, ActuatorActions.On));
        var dose = await _controller.ExecuteCommandAsync(Command(doser, ActuatorActions.Dose, amount: 5));
        Assert.Equal(CommandStatuses.Running, dose.Status);
        Assert.True(doserDevice.On);

        await _controller.ExecuteLocalAsync(pump.Id, new ActuatorRequest(ActuatorActions.Off));
        await TickAsync(TimeSpan.FromSeconds(1));
        Assert.False(doserDevice.On);
        var stop = Assert.Single(EventsWith(SafetyEventCodes.PumpNotRunning));
        Assert.Equal((EventSeverities.Critical, doser.Id), (stop.Severity, stop.ActuatorId));
        Assert.Contains(_reporter.Runs, r => r.ActuatorId == doser.Id && r.StopReason == StopReasons.Interlock);
    }

    [Fact]
    public async Task Reservoir_mixing_wait_blocks_the_second_doser()
    {
        var reservoir = Guid.NewGuid();
        var a = Doser("Nutrient A", ActuatorRoles.DoseNutrientA, reservoir, reservoirWait: 300);
        var b = Doser("Nutrient B", ActuatorRoles.DoseNutrientB, reservoir, reservoirWait: 300);
        await ApplyAsync(a, b);
        var deviceA = _plugin.Devices[a.Id];

        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(Command(a, ActuatorActions.Dose, amount: 5))).Status);
        var during = await _controller.ExecuteCommandAsync(Command(b, ActuatorActions.Dose, amount: 5));
        Assert.Equal(CommandStatuses.Rejected, during.Status);
        Assert.Contains("'Nutrient A' is dosing", during.Reason);

        for (var i = 0; i < 20 && deviceA.On; i++)
        {
            await Task.Delay(5);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        await Wait.UntilAsync(() => _reporter.Runs.Any(r => r.ActuatorId == a.Id), Timeout, "the dose of A completes");
        var after = await _controller.ExecuteCommandAsync(Command(b, ActuatorActions.Dose, amount: 5));
        Assert.Equal(CommandStatuses.Rejected, after.Status);
        Assert.Contains("mixing", after.Reason);

        _time.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal(CommandStatuses.Running, (await _controller.ExecuteCommandAsync(Command(b, ActuatorActions.Dose, amount: 5))).Status);
    }

    [Fact]
    public async Task Interlock_trip_event_carries_the_interlock_code()
    {
        var tank = Guid.NewGuid();
        _values.Set(tank, new ChannelValue(60, _time.GetUtcNow(), ReadingQuality.Good));
        var valve = Configs.Actuator(safety: Configs.Safety(maxRun: 600, interlocks: new SafetyInterlock("min", tank, "lt", 15, "tank low")));
        await ApplyAsync(valve);
        await _controller.ExecuteCommandAsync(Command(valve, ActuatorActions.Pulse, duration: 60));
        _values.Set(tank, new ChannelValue(5, _time.GetUtcNow(), ReadingQuality.Good));
        var e = await Wait.ForAsync(() => EventsWith(SafetyEventCodes.InterlockTripped).FirstOrDefault(), Timeout, "the interlock trips");
        Assert.Equal((EventSeverities.Critical, valve.Id), (e.Severity, e.ActuatorId));
    }

    [Fact]
    public async Task Supervision_survives_a_failing_actuator()
    {
        var pump = Pump();
        await ApplyAsync(pump);
        var device = _plugin.Devices[pump.Id];
        await _controller.SetEmergencyStopAsync(new EmergencyStopState(true, [], "test"));
        device.FailOn = true;
        await _controller.SetEmergencyStopAsync(EmergencyStopState.None);
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.False(device.On);
        Assert.Contains(_reporter.Events, e => e.Category == EventCategories.Actuator && e.Message.Contains("could not be switched on"));
        device.FailOn = false;
        await TickAsync(TimeSpan.FromSeconds(31));
        Assert.True(device.On);
    }
}
