using Microsoft.Extensions.Time.Testing;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Tests.Safety;

public sealed class SafetyGuardTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRunLedger _ledger = new();
    private readonly ChannelValueStore _values = new();
    private FakeTimeProvider _time = null!;
    private SafetyGuard _guard = null!;

    public SafetyGuardTests()
    {
        UseClock(Noon);
    }

    private void UseClock(DateTimeOffset now)
    {
        _time = new FakeTimeProvider(now);
        _guard = new SafetyGuard(_ledger, _values, _time);
    }

    private SafetyDecision Evaluate(ActuatorConfig actuator, ActuatorRequest request, EmergencyStopState? estop = null, bool running = false, string tz = "UTC") =>
        _guard.Evaluate(actuator, tz, estop ?? EmergencyStopState.None, request, running);

    private static ActuatorRequest On(double? duration = null) => new(ActuatorActions.On, DurationSec: duration);
    private static ActuatorRequest Pulse(double duration) => new(ActuatorActions.Pulse, DurationSec: duration);
    private static ActuatorRequest Dose(double ml) => new(ActuatorActions.Dose, AmountMl: ml);

    private void RecordRun(Guid actuatorId, DateTimeOffset start, double seconds, double? ml = null) =>
        _ledger.Record(new RunRecord(Guid.NewGuid(), actuatorId, start, start.AddSeconds(seconds), ml));

    [Fact]
    public void Off_is_always_allowed_even_when_stopped_disabled_or_running()
    {
        var a = Configs.Actuator(enabled: false, safety: Configs.Safety(maxRun: 1, maxDaily: 1));
        RecordRun(a.Id, Noon.AddMinutes(-5), 10);
        var decision = Evaluate(a, new ActuatorRequest(ActuatorActions.Off), new EmergencyStopState(true, [], "flood"), running: true);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Unknown_action_is_rejected()
    {
        var decision = Evaluate(Configs.Actuator(), new ActuatorRequest("explode"));
        Assert.False(decision.Allowed);
        Assert.Contains("unknown action", decision.Reason);
    }

    [Fact]
    public void Device_emergency_stop_blocks_every_actuator()
    {
        var decision = Evaluate(Configs.Actuator(), Pulse(5), new EmergencyStopState(true, [], "leak"));
        Assert.False(decision.Allowed);
        Assert.Contains("emergency stop", decision.Reason);
        Assert.Contains("leak", decision.Reason);
    }

    [Fact]
    public void Actuator_emergency_stop_blocks_only_that_actuator()
    {
        var stopped = Configs.Actuator();
        var other = Configs.Actuator();
        var estop = new EmergencyStopState(false, [stopped.Id], null);
        Assert.False(Evaluate(stopped, Pulse(5), estop).Allowed);
        Assert.True(Evaluate(other, Pulse(5), estop).Allowed);
    }

    [Fact]
    public void Disabled_actuator_is_rejected()
    {
        var decision = Evaluate(Configs.Actuator(enabled: false), Pulse(5));
        Assert.False(decision.Allowed);
        Assert.Contains("disabled", decision.Reason);
    }

    [Fact]
    public void Running_actuator_rejects_a_second_start()
    {
        var decision = Evaluate(Configs.Actuator(), Pulse(5), running: true);
        Assert.False(decision.Allowed);
        Assert.Contains("already running", decision.Reason);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Level_outside_zero_to_one_is_rejected(double level)
    {
        Assert.False(Evaluate(Configs.Actuator(), new ActuatorRequest(ActuatorActions.On, Level: level)).Allowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Pulse_requires_positive_duration(double duration)
    {
        var decision = Evaluate(Configs.Actuator(), Pulse(duration));
        Assert.False(decision.Allowed);
        Assert.Contains("durationSec", decision.Reason);
    }

    [Fact]
    public void Unlimited_on_runs_until_switched_off()
    {
        var decision = Evaluate(Configs.Actuator(), On());
        Assert.True(decision.Allowed);
        Assert.Null(decision.DurationSec);
    }

    [Fact]
    public void Pulse_within_max_run_time_keeps_its_duration()
    {
        var decision = Evaluate(Configs.Actuator(safety: Configs.Safety(maxRun: 60)), Pulse(45));
        Assert.True(decision.Allowed);
        Assert.Equal(45, decision.DurationSec);
        Assert.Equal(StopReasons.Completed, decision.StopReason);
    }

    [Fact]
    public void Pulse_longer_than_max_run_time_is_capped()
    {
        var decision = Evaluate(Configs.Actuator(safety: Configs.Safety(maxRun: 60)), Pulse(600));
        Assert.True(decision.Allowed);
        Assert.Equal(60, decision.DurationSec);
        Assert.Contains("capped", decision.Note);
    }

    [Fact]
    public void On_without_duration_gets_auto_off_at_max_run_time()
    {
        var decision = Evaluate(Configs.Actuator(safety: Configs.Safety(maxRun: 120)), On());
        Assert.True(decision.Allowed);
        Assert.Equal(120, decision.DurationSec);
        Assert.Equal(StopReasons.MaxRunTime, decision.StopReason);
    }

    [Fact]
    public void Dose_requires_amount_and_flow_rate()
    {
        Assert.Contains("amountMl", Evaluate(Configs.Actuator(flow: 100), Dose(0)).Reason);
        Assert.Contains("flowRateMlPerMin", Evaluate(Configs.Actuator(), Dose(50)).Reason);
    }

    [Fact]
    public void Dose_duration_follows_flow_rate()
    {
        var decision = Evaluate(Configs.Actuator(flow: 120), Dose(50));
        Assert.True(decision.Allowed);
        Assert.Equal(25, decision.DurationSec!.Value, 6);
        Assert.Equal(50, decision.AmountMl);
    }

    [Fact]
    public void Dose_needing_more_than_max_run_time_is_rejected_not_truncated()
    {
        var decision = Evaluate(Configs.Actuator(flow: 60, safety: Configs.Safety(maxRun: 30)), Dose(100));
        Assert.False(decision.Allowed);
        Assert.Contains("max run time", decision.Reason);
    }

    [Fact]
    public void Minimum_interval_between_runs_is_enforced()
    {
        var a = Configs.Actuator(safety: Configs.Safety(minBetween: 600));
        RecordRun(a.Id, Noon.AddSeconds(-400), 100);
        var blocked = Evaluate(a, Pulse(10));
        Assert.False(blocked.Allowed);
        Assert.Contains("between runs", blocked.Reason);

        _time.Advance(TimeSpan.FromSeconds(301));
        Assert.True(Evaluate(a, Pulse(10)).Allowed);
    }

    [Fact]
    public void Daily_run_limit_rejects_pulses_that_would_exceed_it()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 300));
        RecordRun(a.Id, Noon.AddHours(-3), 250);
        var decision = Evaluate(a, Pulse(60));
        Assert.False(decision.Allowed);
        Assert.Contains("daily run limit", decision.Reason);
        Assert.True(Evaluate(a, Pulse(50)).Allowed);
    }

    [Fact]
    public void Daily_run_limit_caps_on_commands_to_the_remaining_time()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxRun: 600, maxDaily: 300));
        RecordRun(a.Id, Noon.AddHours(-3), 250);
        var decision = Evaluate(a, On());
        Assert.True(decision.Allowed);
        Assert.Equal(50, decision.DurationSec!.Value, 6);
        Assert.Equal(StopReasons.DailyLimit, decision.StopReason);
    }

    [Fact]
    public void Exhausted_daily_run_limit_blocks_everything_but_off()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 300));
        RecordRun(a.Id, Noon.AddHours(-3), 300);
        Assert.False(Evaluate(a, On()).Allowed);
        Assert.False(Evaluate(a, Pulse(1)).Allowed);
        Assert.True(Evaluate(a, new ActuatorRequest(ActuatorActions.Off)).Allowed);
    }

    [Fact]
    public void Daily_run_limit_resets_at_local_midnight()
    {
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 300));
        RecordRun(a.Id, Noon.AddHours(-3), 300);
        Assert.False(Evaluate(a, Pulse(10)).Allowed);
        _time.SetUtcNow(new DateTimeOffset(2026, 6, 11, 0, 0, 1, TimeSpan.Zero));
        Assert.True(Evaluate(a, Pulse(10)).Allowed);
    }

    [Fact]
    public void Daily_amount_limit_rejects_doses_that_would_exceed_it()
    {
        var a = Configs.Actuator(flow: 600, safety: Configs.Safety(maxDailyMl: 1000));
        RecordRun(a.Id, Noon.AddHours(-1), 90, ml: 900);
        var decision = Evaluate(a, Dose(200));
        Assert.False(decision.Allowed);
        Assert.Contains("daily amount limit", decision.Reason);
        Assert.True(Evaluate(a, Dose(100)).Allowed);
    }

    [Fact]
    public void Daily_amount_limit_caps_on_commands_and_blocks_pulses_over_it()
    {
        var a = Configs.Actuator(flow: 600, safety: Configs.Safety(maxDailyMl: 1000));
        RecordRun(a.Id, Noon.AddHours(-1), 90, ml: 900);
        var on = Evaluate(a, On());
        Assert.True(on.Allowed);
        Assert.Equal(10, on.DurationSec!.Value, 6);
        Assert.Equal(100, on.AmountMl!.Value, 6);
        Assert.Equal(StopReasons.DailyLimit, on.StopReason);
        Assert.False(Evaluate(a, Pulse(11)).Allowed);
    }

    [Fact]
    public void Exhausted_daily_amount_blocks_doses()
    {
        var a = Configs.Actuator(flow: 600, safety: Configs.Safety(maxDailyMl: 1000));
        RecordRun(a.Id, Noon.AddHours(-1), 100, ml: 1000);
        var decision = Evaluate(a, Dose(1));
        Assert.False(decision.Allowed);
        Assert.Contains("daily amount limit reached", decision.Reason);
    }

    [Fact]
    public void Limits_combine_to_the_most_restrictive()
    {
        var a = Configs.Actuator(flow: 60, safety: Configs.Safety(maxRun: 100, maxDaily: 1000, maxDailyMl: 1000));
        RecordRun(a.Id, Noon.AddHours(-1), 950, ml: 950);
        var decision = Evaluate(a, On());
        Assert.True(decision.Allowed);
        Assert.Equal(50, decision.DurationSec!.Value, 6);
    }

    [Fact]
    public void Daily_totals_use_the_local_day_of_the_device_time_zone()
    {
        // 2026-01-15 00:30 in Berlin (UTC+1) is 2026-01-14 23:30 UTC.
        UseClock(new DateTimeOffset(2026, 1, 14, 23, 30, 0, TimeSpan.Zero));
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 1000));
        // 23:50–00:10 Berlin time: 10 minutes belong to the new local day.
        RecordRun(a.Id, new DateTimeOffset(2026, 1, 14, 22, 50, 0, TimeSpan.Zero), 1200, ml: 200);
        // 21:00 Berlin on the previous local day (same UTC day).
        RecordRun(a.Id, new DateTimeOffset(2026, 1, 14, 20, 0, 0, TimeSpan.Zero), 600);

        var berlin = _guard.GetDailyUsage(a.Id, "Europe/Berlin");
        Assert.Equal(600, berlin.RunSeconds, 6);
        Assert.Equal(100, berlin.AmountMl, 6);

        var utc = _guard.GetDailyUsage(a.Id, "UTC");
        Assert.Equal(1800, utc.RunSeconds, 6);

        Assert.True(Evaluate(a, Pulse(300), tz: "Europe/Berlin").Allowed);
        Assert.False(Evaluate(a, Pulse(300), tz: "UTC").Allowed);
    }

    [Fact]
    public void Daily_totals_handle_zones_behind_utc()
    {
        // 2026-06-10 02:00 UTC is still 2026-06-09 19:00 in Los Angeles (UTC-7).
        UseClock(new DateTimeOffset(2026, 6, 10, 2, 0, 0, TimeSpan.Zero));
        var a = Configs.Actuator(safety: Configs.Safety(maxDaily: 600));
        RecordRun(a.Id, new DateTimeOffset(2026, 6, 9, 20, 0, 0, TimeSpan.Zero), 600);
        Assert.False(Evaluate(a, Pulse(1), tz: "America/Los_Angeles").Allowed);
        Assert.True(Evaluate(a, Pulse(1), tz: "UTC").Allowed);
    }

    private (ActuatorConfig Actuator, Guid Channel) WithTankInterlock()
    {
        var channel = Guid.NewGuid();
        var a = Configs.Actuator(kind: ActuatorKinds.Pump, safety: Configs.Safety(interlocks:
            new SafetyInterlock("min", channel, "lt", 15, "tank level below 15%")));
        return (a, channel);
    }

    [Fact]
    public void Tripped_interlock_blocks_start()
    {
        var (a, channel) = WithTankInterlock();
        _values.Set(channel, new ChannelValue(10, Noon, ReadingQuality.Good));
        var decision = Evaluate(a, Pulse(10));
        Assert.False(decision.Allowed);
        Assert.Contains("tank level below 15%", decision.Reason);
    }

    [Fact]
    public void Satisfied_interlock_allows_start()
    {
        var (a, channel) = WithTankInterlock();
        _values.Set(channel, new ChannelValue(60, Noon, ReadingQuality.Good));
        Assert.True(Evaluate(a, Pulse(10)).Allowed);
    }

    [Fact]
    public void Interlock_without_a_reading_fails_closed()
    {
        var (a, _) = WithTankInterlock();
        var decision = Evaluate(a, Pulse(10));
        Assert.False(decision.Allowed);
        Assert.Contains("no recent reading", decision.Reason);
    }

    [Fact]
    public void Interlock_with_a_stale_reading_fails_closed()
    {
        var (a, channel) = WithTankInterlock();
        _values.Set(channel, new ChannelValue(60, Noon - SafetyGuard.InterlockMaxAge - TimeSpan.FromSeconds(1), ReadingQuality.Good));
        Assert.False(Evaluate(a, Pulse(10)).Allowed);
    }

    [Fact]
    public void Interlock_with_an_error_reading_fails_closed()
    {
        var (a, channel) = WithTankInterlock();
        _values.Set(channel, new ChannelValue(60, Noon, ReadingQuality.Error));
        Assert.False(Evaluate(a, Pulse(10)).Allowed);
    }

    [Fact]
    public void Running_check_only_reacts_to_tripped_values_not_missing_ones()
    {
        var (a, channel) = WithTankInterlock();
        Assert.Null(_guard.CheckInterlocks(a, requireFresh: false));
        _values.Set(channel, new ChannelValue(14.9, Noon, ReadingQuality.Good));
        Assert.NotNull(_guard.CheckInterlocks(a, requireFresh: false));
    }

    [Theory]
    [InlineData("lt", 10, 9.9, true)]
    [InlineData("lt", 10, 10, false)]
    [InlineData("lte", 10, 10, true)]
    [InlineData("gt", 32, 32.1, true)]
    [InlineData("gt", 32, 32, false)]
    [InlineData("gte", 32, 32, true)]
    [InlineData("bogus", 32, 100, false)]
    public void Interlock_operators(string op, double threshold, double value, bool tripped)
    {
        Assert.Equal(tripped, new SafetyInterlock("x", Guid.NewGuid(), op, threshold, "x").IsTripped(value));
    }

    private static ActuatorConfig Doser(string name, string role, Guid? reservoir = null, double? maxHourlyMl = null, int? reservoirWait = null, Guid? requires = null) =>
        Configs.Actuator(flow: 60, kind: ActuatorKinds.FertilizerDoser,
            safety: new ActuatorSafety(null, null, null, null, [], maxHourlyMl, reservoirWait, requires))
        with { Name = name, Role = role, ReservoirId = reservoir };

    private static ActuatorPeers Peers(Func<Guid, bool> isOn, params ActuatorConfig[] actuators) => new(actuators, isOn);

    [Fact]
    public void Hourly_amount_limit_counts_the_last_60_minutes_including_the_request()
    {
        var a = Doser("Nutrient A", ActuatorRoles.DoseNutrientA, maxHourlyMl: 20);
        RecordRun(a.Id, Noon.AddHours(-2), 30, ml: 30);
        RecordRun(a.Id, Noon.AddMinutes(-40), 10, ml: 10);

        Assert.True(Evaluate(a, Dose(10)).Allowed);
        var rejected = Evaluate(a, Dose(15));
        Assert.False(rejected.Allowed);
        Assert.Contains("hourly amount limit", rejected.Reason);
        Assert.Contains("10 of 20 ml left", rejected.Reason);
        Assert.False(Evaluate(a, Pulse(11)).Allowed);
    }

    [Fact]
    public void Hourly_amount_uses_duration_times_flow_for_runs_without_an_amount_and_caps_on()
    {
        var a = Doser("pH down", ActuatorRoles.DosePhDown, maxHourlyMl: 20);
        RecordRun(a.Id, Noon.AddMinutes(-10), 15);
        var on = Evaluate(a, On());
        Assert.True(on.Allowed);
        Assert.Equal(5, on.DurationSec!.Value, 3);
        Assert.Equal(StopReasons.HourlyLimit, on.StopReason);

        RecordRun(a.Id, Noon.AddMinutes(-5), 5);
        var reached = Evaluate(a, Dose(1));
        Assert.False(reached.Allowed);
        Assert.Contains("hourly amount limit reached", reached.Reason);
    }

    [Fact]
    public void Hourly_amount_limit_without_a_flow_rate_fails_closed()
    {
        var a = Doser("Additive", ActuatorRoles.DoseAdditive, maxHourlyMl: 20) with { FlowRateMlPerMin = null };
        Assert.Contains("flowRateMlPerMin", Evaluate(a, Pulse(1)).Reason);
    }

    [Fact]
    public void Reservoir_mixing_wait_applies_across_dosers_of_the_same_reservoir()
    {
        var reservoir = Guid.NewGuid();
        var a = Doser("Nutrient A", ActuatorRoles.DoseNutrientA, reservoir, reservoirWait: 300);
        var b = Doser("pH Down", ActuatorRoles.DosePhDown, reservoir, reservoirWait: 300);
        var partB = Doser("Nutrient B", ActuatorRoles.DoseNutrientB, reservoir, reservoirWait: 300);
        var other = Doser("Other tank", ActuatorRoles.DoseNutrientA, Guid.NewGuid(), reservoirWait: 300);
        var pump = Configs.Actuator(kind: ActuatorKinds.Pump) with { Role = ActuatorRoles.CirculationPump, ReservoirId = reservoir };
        var peers = Peers(_ => false, a, b, partB, other, pump);
        RecordRun(a.Id, Noon.AddSeconds(-110), 10, ml: 10);
        RecordRun(pump.Id, Noon.AddSeconds(-5), 4);

        var rejected = _guard.Evaluate(b, "UTC", EmergencyStopState.None, Dose(5), false, peers);
        Assert.False(rejected.Allowed);
        Assert.Contains("mixing", rejected.Reason);
        Assert.Contains("'Nutrient A' finished 100s ago", rejected.Reason);
        Assert.False(_guard.Evaluate(a, "UTC", EmergencyStopState.None, Dose(5), false, peers).Allowed);
        Assert.True(_guard.Evaluate(other, "UTC", EmergencyStopState.None, Dose(5), false, peers).Allowed);
        Assert.True(_guard.Evaluate(partB, "UTC", EmergencyStopState.None, Dose(5), false, peers).Allowed);

        _time.Advance(TimeSpan.FromSeconds(200));
        Assert.True(_guard.Evaluate(b, "UTC", EmergencyStopState.None, Dose(5), false, peers).Allowed);

        var whileDosing = _guard.Evaluate(b, "UTC", EmergencyStopState.None, Dose(5), false, Peers(id => id == a.Id, a, b));
        Assert.False(whileDosing.Allowed);
        Assert.Contains("'Nutrient A' is dosing", whileDosing.Reason);
    }

    [Fact]
    public void Requires_actuator_on_rejects_while_the_circulation_pump_is_off()
    {
        var pump = Configs.Actuator(kind: ActuatorKinds.Pump) with { Name = "NFT pump", Role = ActuatorRoles.CirculationPump };
        var doser = Doser("pH up", ActuatorRoles.DosePhUp, requires: pump.Id);

        var off = _guard.Evaluate(doser, "UTC", EmergencyStopState.None, Dose(2), false, Peers(_ => false, pump, doser));
        Assert.False(off.Allowed);
        Assert.Equal("requires 'NFT pump' to be on, but it is off", off.Reason);
        Assert.True(_guard.Evaluate(doser, "UTC", EmergencyStopState.None, Dose(2), false, Peers(id => id == pump.Id, pump, doser)).Allowed);
        Assert.False(Evaluate(doser, Dose(2)).Allowed);
        Assert.Contains("not configured", _guard.Evaluate(doser, "UTC", EmergencyStopState.None, Dose(2), false, Peers(_ => true, doser)).Reason);
        Assert.True(Evaluate(doser, new ActuatorRequest(ActuatorActions.Off)).Allowed);
    }

    [Theory]
    [InlineData(ActuatorRoles.CirculationPump)]
    [InlineData(ActuatorRoles.AirPump)]
    public void Continuous_roles_ignore_per_run_daily_and_interval_limits(string role)
    {
        var pump = Configs.Actuator(kind: ActuatorKinds.Pump, flow: 3000, safety: Configs.Safety(maxRun: 60, maxDaily: 100, minBetween: 600, maxDailyMl: 50))
            with { Role = role };
        RecordRun(pump.Id, Noon.AddSeconds(-120), 100, ml: 5000);

        var decision = Evaluate(pump, On());
        Assert.True(decision.Allowed, decision.Reason);
        Assert.Null(decision.DurationSec);
        Assert.Equal(StopReasons.Completed, decision.StopReason);

        var irrigation = pump with { Role = ActuatorRoles.Irrigation };
        Assert.False(Evaluate(irrigation, On()).Allowed);
    }

    [Fact]
    public void Continuous_roles_still_obey_interlocks_and_emergency_stop()
    {
        var (a, channel) = WithTankInterlock();
        var pump = a with { Role = ActuatorRoles.CirculationPump };
        _values.Set(channel, new ChannelValue(10, Noon, ReadingQuality.Good));
        Assert.False(Evaluate(pump, On()).Allowed);
        _values.Set(channel, new ChannelValue(60, Noon, ReadingQuality.Good));
        Assert.True(Evaluate(pump, On()).Allowed);
        Assert.False(Evaluate(pump, On(), new EmergencyStopState(true, [], "leak")).Allowed);
    }
}
