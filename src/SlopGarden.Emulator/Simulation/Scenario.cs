namespace SlopGarden.Emulator.Simulation;

/// <summary>A complete emulated garden: what exists and how it behaves. Serialized as the scenario file.</summary>
public sealed class ScenarioDocument
{
    public string Name { get; set; } = "Demo garden";
    public double TimeScale { get; set; } = 1;
    public List<SensorDefinition> Sensors { get; set; } = [];
    public List<ActuatorDefinition> Actuators { get; set; } = [];
    public List<CameraDefinition> Cameras { get; set; } = [];
    public List<CouplingDefinition> Couplings { get; set; } = [];
}

public sealed class SensorDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Unit { get; set; } = "";
    public SignalModel Model { get; set; } = new();
    /// <summary>Standard deviation of the gaussian noise added to every read.</summary>
    public double Noise { get; set; }
    public int Decimals { get; set; } = 2;
    /// <summary>Time constant (simulated seconds) with which coupling offsets on stateless models fade out.</summary>
    public double OffsetDecaySec { get; set; } = 1800;
}

/// <summary>
/// Signal model. <c>constant</c>(value), <c>sine</c>(min, max, periodSec, phase = seconds into the local-time
/// period where the wave crosses its midpoint rising), <c>random_walk</c>(min, max, step per √s, start),
/// <c>drying</c>(start, ratePerHour, floor). <c>floor</c>/<c>ceiling</c> clamp the final value of any model.
/// </summary>
public sealed class SignalModel
{
    public string Type { get; set; } = SignalModelTypes.Constant;
    public double? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? PeriodSec { get; set; }
    public double? Phase { get; set; }
    public double? Step { get; set; }
    public double? Start { get; set; }
    public double? RatePerHour { get; set; }
    public double? Floor { get; set; }
    public double? Ceiling { get; set; }
}

public static class SignalModelTypes
{
    public const string Constant = "constant";
    public const string Sine = "sine";
    public const string RandomWalk = "random_walk";
    public const string Drying = "drying";

    public static readonly string[] All = [Constant, Sine, RandomWalk, Drying];
}

public sealed class ActuatorDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "other";
}

public sealed class CameraDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Name of a bundled image (<c>tomato-bed.jpg</c>, <c>lettuce-bed.jpg</c>) or a path to a JPEG/PNG file.</summary>
    public string Image { get; set; } = "tomato-bed.jpg";
}

/// <summary>
/// Links an actuator to a sensor. <c>rate</c> (default): while <see cref="ActuatorId"/> is on, <see cref="SensorId"/> changes by
/// <see cref="RatePerMinute"/> (scaled by the actuator level) per simulated minute. <c>while_on</c>: while the actuator is on,
/// the sensor reads <see cref="Value"/> (scaled by the level, plus the sensor's noise) instead of its model, e.g. a flow meter.
/// A stalled actuator (fault injection) is still on, but its couplings are inactive.
/// </summary>
public sealed class CouplingDefinition
{
    public string ActuatorId { get; set; } = "";
    public string SensorId { get; set; } = "";
    public string Mode { get; set; } = CouplingModes.Rate;
    public double RatePerMinute { get; set; }
    public double? Value { get; set; }
}

public static class CouplingModes
{
    public const string Rate = "rate";
    public const string WhileOn = "while_on";
}
