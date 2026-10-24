using Microsoft.Extensions.Time.Testing;
using SlopGarden.Emulator.Simulation;

namespace SlopGarden.Client.Tests.Emulator;

public sealed class GardenSimulatorTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Midnight);

    private GardenSimulator Create(params SensorDefinition[] sensors) => Create(sensors, [], []);

    private GardenSimulator Create(SensorDefinition[] sensors, ActuatorDefinition[] actuators, CouplingDefinition[] couplings) =>
        new(new ScenarioDocument { Name = "test", Sensors = [.. sensors], Actuators = [.. actuators], Couplings = [.. couplings] },
            _time, TimeZoneInfo.Utc, new Random(42));

    private static SensorDefinition Sensor(string id, SignalModel model, double noise = 0) =>
        new() { Id = id, Name = id, Metric = "m", Unit = "u", Model = model, Noise = noise, Decimals = 6 };

    [Fact]
    public void Constant_model_returns_its_value()
    {
        var sim = Create(Sensor("tank", new SignalModel { Type = SignalModelTypes.Constant, Value = 86 }));
        sim.AdvanceSimulated(3600);
        Assert.Equal(86, sim.ReadValue("tank").Value);
    }

    [Fact]
    public void Sine_model_follows_the_daily_cycle()
    {
        // Peak 6 hours after the phase, trough 18 hours after.
        var sim = Create(Sensor("air", new SignalModel { Type = SignalModelTypes.Sine, Min = 10, Max = 30, PeriodSec = 86_400, Phase = 0 }));
        Assert.Equal(20, sim.TrueValue("air"), 6);
        sim.AdvanceSimulated(6 * 3600);
        Assert.Equal(30, sim.TrueValue("air"), 6);
        sim.AdvanceSimulated(12 * 3600);
        Assert.Equal(10, sim.TrueValue("air"), 6);
    }

    [Fact]
    public void Sine_model_respects_the_floor()
    {
        var sim = Create(Sensor("light", new SignalModel { Type = SignalModelTypes.Sine, Min = -1000, Max = 1000, Floor = 0 }));
        sim.AdvanceSimulated(18 * 3600);
        Assert.Equal(0, sim.TrueValue("light"));
    }

    [Fact]
    public void Drying_model_decreases_by_rate_and_stops_at_floor()
    {
        var sim = Create(Sensor("soil", new SignalModel { Type = SignalModelTypes.Drying, Start = 40, RatePerHour = 2, Floor = 35 }));
        sim.AdvanceSimulated(3600);
        Assert.Equal(38, sim.TrueValue("soil"), 6);
        sim.AdvanceSimulated(10 * 3600);
        Assert.Equal(35, sim.TrueValue("soil"), 6);
    }

    [Fact]
    public void Random_walk_stays_within_bounds()
    {
        var sim = Create(Sensor("hum", new SignalModel { Type = SignalModelTypes.RandomWalk, Start = 60, Step = 5, Min = 40, Max = 80 }));
        for (var i = 0; i < 500; i++)
        {
            sim.AdvanceSimulated(60);
            Assert.InRange(sim.TrueValue("hum"), 40, 80);
        }
    }

    [Fact]
    public void Noise_is_applied_to_reads_but_not_to_the_true_value()
    {
        var sim = Create(Sensor("tank", new SignalModel { Type = SignalModelTypes.Constant, Value = 50 }, noise: 1));
        var reads = Enumerable.Range(0, 50).Select(_ => sim.ReadValue("tank").Value).ToList();
        Assert.Equal(50, sim.TrueValue("tank"));
        Assert.True(reads.Distinct().Count() > 10);
        Assert.InRange(reads.Average(), 49, 51);
    }

    [Fact]
    public void Running_valve_raises_soil_moisture_and_lowers_the_tank()
    {
        var sim = Create(
            [
                Sensor("soil", new SignalModel { Type = SignalModelTypes.Drying, Start = 30, RatePerHour = 0, Floor = 0, Ceiling = 90 }),
                Sensor("tank", new SignalModel { Type = SignalModelTypes.Drying, Start = 80, RatePerHour = 0, Floor = 0, Ceiling = 100 }),
            ],
            [new ActuatorDefinition { Id = "valve", Name = "Valve", Kind = "valve" }],
            [
                new CouplingDefinition { ActuatorId = "valve", SensorId = "soil", RatePerMinute = 2 },
                new CouplingDefinition { ActuatorId = "valve", SensorId = "tank", RatePerMinute = -0.5 },
            ]);

        sim.AdvanceSimulated(600);
        Assert.Equal(30, sim.TrueValue("soil"), 6);

        sim.SetActuatorState("valve", true, null, "test");
        sim.AdvanceSimulated(300);
        Assert.Equal(40, sim.TrueValue("soil"), 6);
        Assert.Equal(77.5, sim.TrueValue("tank"), 6);

        sim.SetActuatorState("valve", false, null, "test");
        sim.AdvanceSimulated(300);
        Assert.Equal(40, sim.TrueValue("soil"), 6);

        sim.SetActuatorState("valve", true, null, "test");
        sim.AdvanceSimulated(3600);
        Assert.Equal(90, sim.TrueValue("soil"), 6);
    }

    [Fact]
    public void Coupling_scales_with_level_and_decays_on_sine_sensors()
    {
        var sim = Create(
            [Sensor("air", new SignalModel { Type = SignalModelTypes.Sine, Min = 20, Max = 20 })],
            [new ActuatorDefinition { Id = "heater", Name = "Heater", Kind = "heater" }],
            [new CouplingDefinition { ActuatorId = "heater", SensorId = "air", RatePerMinute = 1 }]);
        sim.SetActuatorState("heater", true, 0.5, "test");
        sim.AdvanceSimulated(60);
        var heated = sim.TrueValue("air");
        Assert.InRange(heated, 20.4, 20.5);

        sim.SetActuatorState("heater", false, null, "test");
        sim.AdvanceSimulated(4 * 3600);
        Assert.InRange(sim.TrueValue("air"), 20, 20.01);
    }

    [Fact]
    public void Time_scale_speeds_up_real_time_advances()
    {
        var sim = Create(Sensor("soil", new SignalModel { Type = SignalModelTypes.Drying, Start = 100, RatePerHour = 6 }));
        sim.SetTimeScale(60);
        sim.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(40, sim.TrueValue("soil"), 6);
        Assert.Equal(Midnight.AddHours(10), sim.SimulatedTime);
        Assert.Throws<ArgumentException>(() => sim.SetTimeScale(0));
    }

    [Fact]
    public void Override_wins_until_it_expires_or_is_cleared()
    {
        var sim = Create(Sensor("tank", new SignalModel { Type = SignalModelTypes.Constant, Value = 86 }));
        sim.SetOverride("tank", 5, durationSec: 60);
        Assert.Equal(5, sim.ReadValue("tank").Value);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(86, sim.ReadValue("tank").Value);

        sim.SetOverride("tank", 7, durationSec: null);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(7, sim.ReadValue("tank").Value);
        sim.ClearOverride("tank");
        Assert.Equal(86, sim.ReadValue("tank").Value);
    }

    [Fact]
    public void Sensors_can_be_added_updated_and_removed()
    {
        var sim = Create();
        var changes = 0;
        sim.DefinitionsChanged += () => changes++;

        sim.CreateSensor(Sensor("ph", new SignalModel { Type = SignalModelTypes.Constant, Value = 6.5 }));
        Assert.Throws<InvalidOperationException>(() => sim.CreateSensor(Sensor("ph", new SignalModel { Type = SignalModelTypes.Constant, Value = 1 })));
        Assert.Throws<ArgumentException>(() => sim.CreateSensor(Sensor("bad id!", new SignalModel())));
        sim.UpdateSensor("ph", new SensorUpdate("pH probe", null, null, new SignalModel { Type = SignalModelTypes.Constant, Value = 7 }, null, null, null));
        Assert.Equal("pH probe", sim.GetSensorView("ph").Name);
        Assert.Equal(7, sim.ReadValue("ph").Value);
        sim.DeleteSensor("ph");
        Assert.Throws<KeyNotFoundException>(() => sim.ReadValue("ph"));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Export_round_trips_the_scenario()
    {
        var sim = new GardenSimulator(DefaultScenario.Create(), _time, TimeZoneInfo.Utc, new Random(1));
        var exported = sim.Export();
        var copy = new GardenSimulator(exported, _time, TimeZoneInfo.Utc, new Random(1));
        Assert.Equal(sim.ListSensors().Select(s => s.Id), copy.ListSensors().Select(s => s.Id));
        Assert.Equal(sim.ListActuators().Select(a => a.Id), copy.ListActuators().Select(a => a.Id));
        Assert.NotEmpty(exported.Couplings);
    }

    [Fact]
    public void Default_scenario_has_coupled_valves_and_cameras()
    {
        var scenario = DefaultScenario.Create();
        Assert.Contains(scenario.Couplings, c => c.ActuatorId == "tomato-valve" && c.SensorId == "tomato-soil-moisture" && c.RatePerMinute > 0);
        Assert.Contains(scenario.Cameras, c => c.Id == "tomato-cam");
    }
}
