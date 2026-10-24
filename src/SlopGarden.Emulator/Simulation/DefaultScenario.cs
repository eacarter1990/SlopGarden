using SlopGarden.Contracts;

namespace SlopGarden.Emulator.Simulation;

public static class DefaultScenario
{
    private const double Day = 86_400;

    public static ScenarioDocument Create() => new()
    {
        Name = "Demo garden",
        TimeScale = 1,
        Sensors =
        [
            Sensor("tomato-soil-moisture", "Tomato bed soil moisture", Metrics.SoilMoisturePct, 0.15,
                new() { Type = SignalModelTypes.Drying, Start = 42, RatePerHour = 0.6, Floor = 8, Ceiling = 100 }),
            Sensor("tomato-soil-temperature", "Tomato bed soil temperature", Metrics.SoilTemperatureC, 0.05,
                new() { Type = SignalModelTypes.Sine, Min = 15, Max = 21, PeriodSec = Day, Phase = 9 * 3600 }),
            Sensor("basil-soil-moisture", "Basil pot soil moisture", Metrics.SoilMoisturePct, 0.2,
                new() { Type = SignalModelTypes.Drying, Start = 55, RatePerHour = 0.9, Floor = 5, Ceiling = 100 }),
            Sensor("lettuce-soil-moisture", "Lettuce bed soil moisture", Metrics.SoilMoisturePct, 0.15,
                new() { Type = SignalModelTypes.Drying, Start = 63, RatePerHour = 0.4, Floor = 10, Ceiling = 100 }),
            Sensor("greenhouse-air-temperature", "Greenhouse air temperature", Metrics.AirTemperatureC, 0.1,
                new() { Type = SignalModelTypes.Sine, Min = 18, Max = 27, PeriodSec = Day, Phase = 8 * 3600 }),
            Sensor("greenhouse-air-humidity", "Greenhouse air humidity", Metrics.AirHumidityPct, 0.3,
                new() { Type = SignalModelTypes.RandomWalk, Min = 45, Max = 85, Start = 62, Step = 0.08 }),
            Sensor("greenhouse-light", "Greenhouse light", Metrics.LightLux, 120,
                new() { Type = SignalModelTypes.Sine, Min = -45_000, Max = 65_000, PeriodSec = Day, Phase = 7 * 3600, Floor = 0 },
                decimals: 0, offsetDecaySec: 10),
            Sensor("water-tank-level", "Water tank level", Metrics.WaterLevelPct, 0.05,
                new() { Type = SignalModelTypes.Constant, Value = 86, Floor = 0, Ceiling = 100 }),

            // Hydroponic NFT rack: EC and pH drift up as plants take up water, the reservoir evaporates slowly.
            Sensor("hydro-ec", "NFT reservoir EC", Metrics.EcMsCm, 0.01,
                new() { Type = SignalModelTypes.Drying, Start = 1.6, RatePerHour = -0.02, Floor = 0, Ceiling = 6 }),
            Sensor("hydro-ph", "NFT reservoir pH", Metrics.Ph, 0.02,
                new() { Type = SignalModelTypes.Drying, Start = 6.0, RatePerHour = -0.03, Floor = 2, Ceiling = 12 }),
            Sensor("hydro-water-temperature", "NFT reservoir water temperature", Metrics.WaterTemperatureC, 0.03,
                new() { Type = SignalModelTypes.Sine, Min = 18, Max = 23, PeriodSec = Day, Phase = 10 * 3600 }),
            Sensor("hydro-reservoir-level", "NFT reservoir level", Metrics.WaterLevelPct, 0.05,
                new() { Type = SignalModelTypes.Drying, Start = 80, RatePerHour = 0.4, Floor = 0, Ceiling = 100 }, decimals: 1),
            Sensor("hydro-flow", "NFT channel flow", Metrics.FlowLitersPerMin, 0.03,
                new() { Type = SignalModelTypes.Constant, Value = 0, Floor = 0 }, decimals: 1),
            Sensor("hydro-dissolved-oxygen", "NFT reservoir dissolved oxygen", Metrics.DissolvedOxygenMgL, 0.03,
                new() { Type = SignalModelTypes.Drying, Start = 7.5, RatePerHour = 0.6, Floor = 2.5, Ceiling = 7.6 }),
        ],
        Actuators =
        [
            new() { Id = "tomato-valve", Name = "Tomato bed valve", Kind = ActuatorKinds.Valve },
            new() { Id = "lettuce-valve", Name = "Lettuce bed valve", Kind = ActuatorKinds.Valve },
            new() { Id = "basil-drip", Name = "Basil drip valve", Kind = ActuatorKinds.Valve },
            new() { Id = "main-pump", Name = "Main pump", Kind = ActuatorKinds.Pump },
            new() { Id = "grow-light", Name = "Grow light", Kind = ActuatorKinds.Light },
            new() { Id = "heater", Name = "Greenhouse heater", Kind = ActuatorKinds.Heater },
            new() { Id = "fertilizer-doser", Name = "Fertilizer doser", Kind = ActuatorKinds.FertilizerDoser },
            new() { Id = "nft-pump", Name = "NFT circulation pump", Kind = ActuatorKinds.Pump },
            new() { Id = "air-pump", Name = "Reservoir air pump", Kind = ActuatorKinds.Pump },
            new() { Id = "doser-nutrient-a", Name = "Nutrient A doser", Kind = ActuatorKinds.FertilizerDoser },
            new() { Id = "doser-nutrient-b", Name = "Nutrient B doser", Kind = ActuatorKinds.FertilizerDoser },
            new() { Id = "doser-ph-down", Name = "pH down doser", Kind = ActuatorKinds.FertilizerDoser },
            new() { Id = "doser-ph-up", Name = "pH up doser", Kind = ActuatorKinds.FertilizerDoser },
        ],
        Cameras =
        [
            new() { Id = "tomato-cam", Name = "Tomato bed cam", Image = "tomato-bed.jpg" },
            new() { Id = "lettuce-cam", Name = "Lettuce bed cam", Image = "lettuce-bed.jpg" },
            new() { Id = "hydro-cam", Name = "NFT rack cam", Image = "nft-rack.jpg" },
        ],
        Couplings =
        [
            Couple("tomato-valve", "tomato-soil-moisture", 2.0),
            Couple("tomato-valve", "water-tank-level", -0.3),
            Couple("lettuce-valve", "lettuce-soil-moisture", 1.5),
            Couple("lettuce-valve", "water-tank-level", -0.3),
            Couple("basil-drip", "basil-soil-moisture", 1.2),
            Couple("basil-drip", "water-tank-level", -0.1),
            Couple("main-pump", "water-tank-level", -0.8),
            Couple("grow-light", "greenhouse-light", 60_000),
            Couple("heater", "greenhouse-air-temperature", 0.25),
            WhileOn("nft-pump", "hydro-flow", 3.2),
            Couple("air-pump", "hydro-dissolved-oxygen", 0.04),
            Couple("doser-nutrient-a", "hydro-ec", 2.0),
            Couple("doser-nutrient-b", "hydro-ec", 2.0),
            Couple("doser-ph-down", "hydro-ph", -1.5),
            Couple("doser-ph-up", "hydro-ph", 1.5),
        ],
    };

    private static SensorDefinition Sensor(string id, string name, string metric, double noise, SignalModel model, int decimals = 2, double offsetDecaySec = 1800) => new()
    {
        Id = id,
        Name = name,
        Metric = metric,
        Unit = Metrics.DefaultUnits.GetValueOrDefault(metric, ""),
        Model = model,
        Noise = noise,
        Decimals = decimals,
        OffsetDecaySec = offsetDecaySec,
    };

    private static CouplingDefinition Couple(string actuatorId, string sensorId, double ratePerMinute) =>
        new() { ActuatorId = actuatorId, SensorId = sensorId, RatePerMinute = ratePerMinute };

    private static CouplingDefinition WhileOn(string actuatorId, string sensorId, double value) =>
        new() { ActuatorId = actuatorId, SensorId = sensorId, Mode = CouplingModes.WhileOn, Value = value };
}
