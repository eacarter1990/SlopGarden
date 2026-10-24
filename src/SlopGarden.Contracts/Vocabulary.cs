namespace SlopGarden.Contracts;

/// <summary>
/// Well-known metric identifiers. Metrics are open strings so plugins can introduce new ones;
/// these are the ones the server, UI and AI understand out of the box.
/// </summary>
public static class Metrics
{
    public const string SoilMoisturePct = "soil_moisture_pct";
    public const string SoilTemperatureC = "soil_temperature_c";
    public const string AirTemperatureC = "air_temperature_c";
    public const string AirHumidityPct = "air_humidity_pct";
    public const string AirPressureHpa = "air_pressure_hpa";
    public const string LightLux = "light_lux";
    public const string Par = "par_umol_m2_s";
    public const string WaterLevelPct = "water_level_pct";
    public const string WaterTemperatureC = "water_temperature_c";
    public const string Ph = "ph";
    public const string EcMsCm = "ec_ms_cm";
    public const string Co2Ppm = "co2_ppm";
    public const string Rain = "rain_detected";
    public const string FlowLitersPerMin = "flow_l_min";
    public const string Voltage = "voltage_v";
    public const string Digital = "digital";
    public const string DissolvedOxygenMgL = "dissolved_oxygen_mg_l";
    public const string TdsPpm = "tds_ppm";
    public const string OrpMv = "orp_mv";

    public static readonly IReadOnlyDictionary<string, string> DefaultUnits = new Dictionary<string, string>
    {
        [SoilMoisturePct] = "%",
        [SoilTemperatureC] = "°C",
        [AirTemperatureC] = "°C",
        [AirHumidityPct] = "%",
        [AirPressureHpa] = "hPa",
        [LightLux] = "lx",
        [Par] = "µmol/m²/s",
        [WaterLevelPct] = "%",
        [WaterTemperatureC] = "°C",
        [Ph] = "pH",
        [EcMsCm] = "mS/cm",
        [Co2Ppm] = "ppm",
        [Rain] = "bool",
        [FlowLitersPerMin] = "L/min",
        [Voltage] = "V",
        [Digital] = "bool",
        [DissolvedOxygenMgL] = "mg/L",
        [TdsPpm] = "ppm",
        [OrpMv] = "mV",
    };

    /// <summary>Short names for people. Alerts and the web use these instead of the stored id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [SoilMoisturePct] = "Soil moisture",
        [SoilTemperatureC] = "Soil temp",
        [AirTemperatureC] = "Air temp",
        [AirHumidityPct] = "Humidity",
        [AirPressureHpa] = "Pressure",
        [LightLux] = "Light",
        [Par] = "PAR",
        [WaterLevelPct] = "Tank level",
        [WaterTemperatureC] = "Water temp",
        [Ph] = "pH",
        [EcMsCm] = "EC",
        [Co2Ppm] = "CO₂",
        [Rain] = "Rain",
        [FlowLitersPerMin] = "Flow",
        [Voltage] = "Voltage",
        [Digital] = "Digital",
        [DissolvedOxygenMgL] = "Dissolved O₂",
        [TdsPpm] = "TDS",
        [OrpMv] = "ORP",
    };

    public static string Label(string metric) =>
        Labels.TryGetValue(metric, out var label) ? label : metric.Replace('_', ' ');
}

public static class ActuatorKinds
{
    public const string Pump = "pump";
    public const string Valve = "valve";
    public const string Light = "light";
    public const string Heater = "heater";
    public const string Fan = "fan";
    public const string FertilizerDoser = "fertilizer_doser";
    public const string Mister = "mister";
    public const string Other = "other";

    public static readonly string[] All = [Pump, Valve, Light, Heater, Fan, FertilizerDoser, Mister, Other];

    /// <summary>Kinds that move water and are subject to watering limits and tank-low lockout.</summary>
    public static bool IsWatering(string kind) => kind is Pump or Valve or Mister;
}

/// <summary>What an actuator does in the system. Roles decide which safety limits and watchdogs apply.</summary>
public static class ActuatorRoles
{
    public const string Irrigation = "irrigation";
    public const string CirculationPump = "circulation_pump";
    public const string AirPump = "air_pump";
    public const string DoseNutrientA = "dose_nutrient_a";
    public const string DoseNutrientB = "dose_nutrient_b";
    public const string DosePhUp = "dose_ph_up";
    public const string DosePhDown = "dose_ph_down";
    public const string DoseCalMag = "dose_calmag";
    public const string DoseAdditive = "dose_additive";
    public const string Chiller = "chiller";
    public const string TopUpValve = "top_up_valve";
    public const string Drain = "drain";

    public static readonly string[] All = [Irrigation, CirculationPump, AirPump, DoseNutrientA, DoseNutrientB, DosePhUp, DosePhDown, DoseCalMag, DoseAdditive, Chiller, TopUpValve, Drain];

    public static bool IsDosing(string? role) => role is not null && role.StartsWith("dose_", StringComparison.Ordinal);

    public static string? DoseGroup(string? role) => role switch
    {
        DosePhUp or DosePhDown => "ph",
        _ when IsDosing(role) => "nutrient",
        _ => null,
    };

    /// <summary>
    /// Mixing wait: after a dose, the reservoir must mix before the next one, except that parts of a nutrient
    /// (A, B, Cal-Mag, additives) may follow each other directly. The same doser twice, nutrient→pH, pH→nutrient
    /// and pH→pH all wait.
    /// </summary>
    public static bool NeedsMixingWait(string? previousRole, string? nextRole) =>
        previousRole == nextRole || DoseGroup(previousRole) != DoseGroup(nextRole) || DoseGroup(nextRole) == "ph";

    /// <summary>Pumps that are meant to run for long periods; per-run/per-day watering limits don't apply to them.</summary>
    public static bool IsContinuous(string? role) => role is CirculationPump or AirPump;
}

public static class SafetyEventCodes
{
    public const string FlowStall = "flow_stall";
    public const string UnexpectedFlow = "unexpected_flow";
    public const string PumpNotRunning = "pump_not_running";
    public const string InterlockTripped = "interlock_tripped";
}

public static class ActuatorActions
{
    public const string On = "on";
    public const string Off = "off";
    /// <summary>Turn on for <c>durationSec</c>, then off.</summary>
    public const string Pulse = "pulse";
    /// <summary>Dispense <c>amount</c> millilitres (dosers and metered pumps).</summary>
    public const string Dose = "dose";
}

public static class CommandStatuses
{
    public const string Pending = "pending";
    public const string Blocked = "blocked";
    public const string Sent = "sent";
    public const string Received = "received";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

public static class ReadingQuality
{
    public const short Good = 0;
    public const short Suspect = 1;
    public const short OutOfRange = 2;
    public const short Error = 3;
    public const short ClockUnsynced = 4;
}

public static class PluginKinds
{
    public const string Sensor = "sensor";
    public const string Actuator = "actuator";
    public const string Camera = "camera";
}

public static class PluginTrust
{
    /// <summary>Compiled into the SlopGarden client.</summary>
    public const string Builtin = "builtin";
    /// <summary>External assembly whose signature verifies against a trusted catalog key.</summary>
    public const string Signed = "signed";
    /// <summary>External assembly without a valid signature. Allowed, but the UI warns.</summary>
    public const string Unsigned = "unsigned";
    /// <summary>Signature present but does not match the assembly hash or the key is revoked.</summary>
    public const string Invalid = "invalid";
}

public static class Discovery
{
    public const int UdpPort = 47474;
    public const string DiscoverMessageType = "slopgarden.discover";
    public const string ServerMessageType = "slopgarden.server";
    public const int ClientLocalApiPort = 8787;

    /// <summary>Client advertisement: a device waiting to be paired. TXT carries hw, ver and name. Port is the local API (8787).</summary>
    public const string MdnsClientService = "_slopgarden._tcp.local";
    /// <summary>Server advertisement. TXT carries <c>url=http://…</c>.</summary>
    public const string MdnsServerService = "_slopgarden-server._tcp.local";
    public const int MdnsPort = 5353;
    public const string MdnsGroup = "224.0.0.251";
}

/// <summary>Where a camera looks. The role is the camera's target: the garden, one bed, or one plant.</summary>
public static class CameraRoles
{
    public const string Garden = "garden";
    public const string Bed = "bed";
    public const string Plant = "plant";
    public static readonly string[] All = [Garden, Bed, Plant];
}

public static class DeviceStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Revoked = "revoked";
}
