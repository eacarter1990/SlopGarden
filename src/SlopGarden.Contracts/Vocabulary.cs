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
    };
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
}

public static class DeviceStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Revoked = "revoked";
}
