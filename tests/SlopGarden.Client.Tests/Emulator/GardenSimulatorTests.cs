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

    [Fact]
    public void While_on_coupling_drives_the_sensor_only_while_the_actuator_runs()
    {
        var sim = Create(
            [Sensor("flow", new SignalModel { Type = SignalModelTypes.Constant, Value = 0, Floor = 0 })],
            [new ActuatorDefinition { Id = "pump", Name = "Pump", Kind = "pump" }],
            [new CouplingDefinition { ActuatorId = "pump", SensorId = "flow", Mode = CouplingModes.WhileOn, Value = 3.2 }]);

        Assert.Equal(0, sim.ReadValue("flow").Value);
        sim.SetActuatorState("pump", true, null, "test");
        Assert.Equal(3.2, sim.ReadValue("flow").Value, 6);
        sim.AdvanceSimulated(600);
        Assert.Equal(3.2, sim.TrueValue("flow"), 6);
        sim.SetActuatorState("pump", true, 0.5, "test");
        Assert.Equal(1.6, sim.TrueValue("flow"), 6);
        sim.SetActuatorState("pump", false, null, "test");
        Assert.Equal(0, sim.ReadValue("flow").Value);
    }

    [Fact]
    public void While_on_reading_includes_the_sensor_noise()
    {
        var sim = Create(
            [Sensor("flow", new SignalModel { Type = SignalModelTypes.Constant, Value = 0, Floor = 0 }, noise: 0.1)],
            [new ActuatorDefinition { Id = "pump", Name = "Pump", Kind = "pump" }],
            [new CouplingDefinition { ActuatorId = "pump", SensorId = "flow", Mode = CouplingModes.WhileOn, Value = 3.2 }]);
        sim.SetActuatorState("pump", true, null, "test");
        var reads = Enumerable.Range(0, 50).Select(_ => sim.ReadValue("flow").Value).ToList();
        Assert.True(reads.Distinct().Count() > 10);
        Assert.InRange(reads.Average(), 3.1, 3.3);
    }

    [Fact]
    public void Stalled_actuator_reports_on_but_its_couplings_are_inactive()
    {
        var sim = Create(
            [
                Sensor("flow", new SignalModel { Type = SignalModelTypes.Constant, Value = 0, Floor = 0 }),
                Sensor("ec", new SignalModel { Type = SignalModelTypes.Drying, Start = 1.5, RatePerHour = 0 }),
            ],
            [
                new ActuatorDefinition { Id = "pump", Name = "Pump", Kind = "pump" },
                new ActuatorDefinition { Id = "doser", Name = "Doser", Kind = "fertilizer_doser" },
            ],
            [
                new CouplingDefinition { ActuatorId = "pump", SensorId = "flow", Mode = CouplingModes.WhileOn, Value = 3.2 },
                new CouplingDefinition { ActuatorId = "doser", SensorId = "ec", RatePerMinute = 2 },
            ]);
        sim.SetActuatorState("pump", true, null, "test");
        sim.SetActuatorState("doser", true, null, "test");

        var view = sim.SetActuatorFault("pump", stalled: true);
        sim.SetActuatorFault("doser", stalled: true);
        Assert.True(view.On);
        Assert.True(view.Stalled);
        Assert.True(sim.GetActuator("pump").Stalled);
        Assert.Equal(0, sim.ReadValue("flow").Value);
        sim.AdvanceSimulated(60);
        Assert.Equal(1.5, sim.TrueValue("ec"), 6);

        sim.SetActuatorFault("pump", stalled: false);
        sim.SetActuatorFault("doser", stalled: false);
        Assert.False(sim.GetActuator("pump").Stalled);
        Assert.Equal(3.2, sim.ReadValue("flow").Value, 6);
        sim.AdvanceSimulated(30);
        Assert.Equal(2.5, sim.TrueValue("ec"), 6);
        Assert.Throws<KeyNotFoundException>(() => sim.SetActuatorFault("nope", true));
    }

    [Fact]
    public void Default_scenario_includes_the_hydroponic_rack_without_changing_existing_ids()
    {
        var sim = new GardenSimulator(DefaultScenario.Create(), _time, TimeZoneInfo.Utc, new Random(3));
        var sensors = sim.ListSensors().ToDictionary(s => s.Id);
        foreach (var id in new[] { "tomato-soil-moisture", "basil-soil-moisture", "lettuce-soil-moisture", "greenhouse-air-temperature", "water-tank-level" })
            Assert.Contains(id, sensors.Keys);
        Assert.Equal("ec_ms_cm", sensors["hydro-ec"].Metric);
        Assert.Equal("ph", sensors["hydro-ph"].Metric);
        Assert.Equal("water_temperature_c", sensors["hydro-water-temperature"].Metric);
        Assert.Equal("water_level_pct", sensors["hydro-reservoir-level"].Metric);
        Assert.Equal("flow_l_min", sensors["hydro-flow"].Metric);
        Assert.Equal("dissolved_oxygen_mg_l", sensors["hydro-dissolved-oxygen"].Metric);
        var actuators = sim.ListActuators().ToDictionary(a => a.Id);
        Assert.Equal("pump", actuators["nft-pump"].Kind);
        Assert.Equal("pump", actuators["air-pump"].Kind);
        foreach (var id in new[] { "doser-nutrient-a", "doser-nutrient-b", "doser-ph-down", "doser-ph-up", "fertilizer-doser" })
            Assert.Equal("fertilizer_doser", actuators[id].Kind);
        Assert.Contains(sim.ListCameras(), c => c.Id == "hydro-cam" && c.Image == "nft-rack.jpg");
        Assert.Contains(sim.ListCameras(), c => c.Id == "lettuce-cam");

        Assert.Equal(0, sim.TrueValue("hydro-flow"));
        sim.SetActuatorState("nft-pump", true, null, "test");
        Assert.Equal(3.2, sim.TrueValue("hydro-flow"), 6);

        var ec = sim.TrueValue("hydro-ec");
        sim.SetActuatorState("doser-nutrient-a", true, null, "test");
        sim.AdvanceSimulated(15);
        sim.SetActuatorState("doser-nutrient-a", false, null, "test");
        Assert.InRange(sim.TrueValue("hydro-ec") - ec, 0.49, 0.51);

        var ph = sim.TrueValue("hydro-ph");
        sim.SetActuatorState("doser-ph-down", true, null, "test");
        sim.AdvanceSimulated(10);
        Assert.InRange(sim.TrueValue("hydro-ph") - ph, -0.26, -0.24);
    }

    [Fact]
    public void Bundled_nft_rack_image_is_embedded()
    {
        var names = typeof(GardenSimulator).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("nft-rack.jpg", StringComparison.Ordinal));
    }
}
