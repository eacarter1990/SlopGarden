namespace SlopGarden.Server.Data;

public static class Roles
{
    public const string Admin = "admin";
    public const string ReadWrite = "read_write";
    public const string ReadOnly = "read_only";

    public static readonly string[] All = [Admin, ReadWrite, ReadOnly];

    public static int Rank(string role) => role switch
    {
        Admin => 3,
        ReadWrite => 2,
        ReadOnly => 1,
        _ => 0,
    };

    public static bool AtLeast(string role, string required) => Rank(role) >= Rank(required);
}

public static class PatScopes
{
    public const string Read = "read";
    public const string Propose = "propose";
    public const string Write = "write";
    public static readonly string[] All = [Read, Propose, Write];
}

public static class TargetTypes
{
    public const string Garden = "garden";
    public const string Bed = "bed";
    public const string Plant = "plant";
    public const string Reservoir = "reservoir";
    public static readonly string[] Component = [Garden, Bed, Plant, Reservoir];
}

public static class ScopeTypes
{
    public const string Global = "global";
    public const string Garden = "garden";
    public const string Bed = "bed";
    public const string Plant = "plant";
    public const string Device = "device";
    public const string Actuator = "actuator";
    public const string Reservoir = "reservoir";
    public static readonly string[] All = [Global, Garden, Bed, Plant, Reservoir, Device, Actuator];
}

public static class SafetyLimitTypes
{
    public const string MaxRunSeconds = "max_run_seconds";
    public const string MaxDailyRunSeconds = "max_daily_run_seconds";
    public const string MinSecondsBetweenRuns = "min_seconds_between_runs";
    public const string MaxDailyAmountMl = "max_daily_amount_ml";
    public const string MaxHourlyAmountMl = "max_hourly_amount_ml";
    public const string MinSecondsBetweenDoses = "min_seconds_between_doses";
    public const string TankLowLockout = "tank_low_lockout";
    public const string MaxTemperatureCutoff = "max_temperature_cutoff";
    public const string MinTemperatureCutoff = "min_temperature_cutoff";

    public static readonly string[] Numeric = [MaxRunSeconds, MaxDailyRunSeconds, MinSecondsBetweenRuns, MaxDailyAmountMl, MaxHourlyAmountMl, MinSecondsBetweenDoses];
    public static readonly string[] Interlocks = [TankLowLockout, MaxTemperatureCutoff, MinTemperatureCutoff];
    public static readonly string[] All = [.. Numeric, .. Interlocks];

    public static string DefaultOperator(string limitType) => limitType switch
    {
        TankLowLockout => "lt",
        MaxTemperatureCutoff => "gt",
        MinTemperatureCutoff => "lt",
        _ => "lt",
    };
}

public static class ActorTypes
{
    public const string User = "user";
    public const string Ai = "ai";
    public const string Pat = "pat";
    public const string Device = "device";
    public const string System = "system";
    public const string Schedule = "schedule";
}

public static class ProposalStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Executed = "executed";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public static class Severities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static int Rank(string s) => s switch { Critical => 3, Warning => 2, _ => 1 };
}

public static class AiJobs
{
    public const string Chat = "chat";
    public const string ScheduledReview = "scheduled_review";
    public const string Vision = "vision";
    public const string Reports = "reports";
    public const string Calibration = "calibration";
    public static readonly string[] All = [Chat, ScheduledReview, Vision, Reports, Calibration];
}

public static class ReportCadence
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string OnDemand = "on_demand";
    public static readonly string[] Scheduled = [Daily, Weekly, Monthly];

    public static string Cron(string cadence) => cadence switch
    {
        Weekly => "0 7 * * 1",
        Monthly => "0 7 1 * *",
        _ => "0 7 * * *",
    };

    public static string Title(string cadence) => cadence switch
    {
        Weekly => "Weekly growth report",
        Monthly => "Monthly growth report",
        OnDemand => "Growth report",
        _ => "Daily growth report",
    };
}

public static class ScheduleKinds
{
    public const string Actuator = "actuator";
    public const string AiReview = "ai_review";
    public const string Report = "report";
    public const string CameraSnapshot = "camera_snapshot";
    public static readonly string[] All = [Actuator, AiReview, Report, CameraSnapshot];
}

public static class GrowingMedia
{
    public const string Soil = "soil";
    public const string Soilless = "soilless";
    public const string Hydroponic = "hydroponic";
    public static readonly string[] All = [Soil, Soilless, Hydroponic];
}

/// <summary>How the water side of a hydroponic system behaves; drives defaults, watchdogs and AI guidance.</summary>
public sealed record HydroSystemInfo(
    string Id,
    string Label,
    string PumpPattern,
    string Description,
    IReadOnlyList<string> KeyMetrics,
    string Guidance);

public static class HydroSystems
{
    public const string Continuous = "continuous";
    public const string Timed = "timed";
    public const string Passive = "passive";

    public static readonly IReadOnlyList<HydroSystemInfo> All =
    [
        new("nft", "NFT (nutrient film technique)", Continuous, "A thin film of solution flows continuously through channels past bare roots.",
            ["flow_l_min", "ec_ms_cm", "ph", "water_temperature_c", "water_level_pct"],
            "The circulation pump must run 24/7; roots dry out within minutes to hours if flow stops, so a flow stall is critical."),
        new("dwc", "DWC (deep water culture)", Continuous, "Roots hang in an aerated reservoir; an air pump and air stones supply oxygen.",
            ["dissolved_oxygen_mg_l", "ec_ms_cm", "ph", "water_temperature_c", "water_level_pct"],
            "The air pump must run 24/7; keep water below ~22 °C so it holds enough oxygen. Root rot risk rises above 24 °C."),
        new("rdwc", "RDWC (recirculating DWC)", Continuous, "Several DWC sites linked to a control bucket with a circulation pump and aeration.",
            ["flow_l_min", "dissolved_oxygen_mg_l", "ec_ms_cm", "ph", "water_temperature_c", "water_level_pct"],
            "Both the circulation and air pumps must run continuously; watch flow and water temperature."),
        new("ebb_flow", "Ebb & flow (flood & drain)", Timed, "The tray floods on a timer, then drains back to the reservoir.",
            ["water_level_pct", "flow_l_min", "ec_ms_cm", "ph", "water_temperature_c"],
            "Flood cycles on a schedule (e.g. 15 min every 3–4 h by day). A flood that never raises the level means a pump or valve fault."),
        new("drip_recirculating", "Drip (recirculating)", Timed, "Emitters drip solution onto media; runoff returns to the reservoir.",
            ["flow_l_min", "ec_ms_cm", "ph", "water_temperature_c", "water_level_pct"],
            "Timed pulses; clogged emitters show as low flow. Clean filters regularly."),
        new("drip_to_waste", "Drip to waste", Timed, "Fresh solution drips onto coco/rockwool; runoff is discarded.",
            ["ec_ms_cm", "ph", "flow_l_min", "water_level_pct"],
            "Timed pulses sized for 10–20 % runoff; compare runoff EC/pH with the feed to judge salt build-up."),
        new("kratky", "Kratky (passive)", Passive, "No pumps: plants sit above a static reservoir and drink it down; an air gap forms for the roots.",
            ["water_level_pct", "ec_ms_cm", "ph", "water_temperature_c"],
            "Don't top the reservoir back up to full — the air gap feeds the roots oxygen. Watch level and EC."),
        new("wick", "Wick (passive)", Passive, "Wicks draw solution from the reservoir into the media.",
            ["water_level_pct", "ec_ms_cm", "ph"],
            "Low-maintenance but slow; keep EC moderate to avoid salt build-up in the media."),
        new("aeroponic", "Aeroponics", Timed, "Roots hang in air and are misted with solution on a short cycle.",
            ["flow_l_min", "ec_ms_cm", "ph", "water_temperature_c", "water_level_pct"],
            "Misting cycles every few minutes; roots dry in minutes if the pump or nozzles fail, so stalls are critical."),
    ];

    public static readonly string[] Ids = [.. All.Select(s => s.Id)];

    public static HydroSystemInfo? Find(string? id) => All.FirstOrDefault(s => s.Id == id);
}
