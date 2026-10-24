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
    public static readonly string[] Component = [Garden, Bed, Plant];
}

public static class ScopeTypes
{
    public const string Global = "global";
    public const string Garden = "garden";
    public const string Bed = "bed";
    public const string Plant = "plant";
    public const string Device = "device";
    public const string Actuator = "actuator";
    public static readonly string[] All = [Global, Garden, Bed, Plant, Device, Actuator];
}

public static class SafetyLimitTypes
{
    public const string MaxRunSeconds = "max_run_seconds";
    public const string MaxDailyRunSeconds = "max_daily_run_seconds";
    public const string MinSecondsBetweenRuns = "min_seconds_between_runs";
    public const string MaxDailyAmountMl = "max_daily_amount_ml";
    public const string TankLowLockout = "tank_low_lockout";
    public const string MaxTemperatureCutoff = "max_temperature_cutoff";
    public const string MinTemperatureCutoff = "min_temperature_cutoff";

    public static readonly string[] Numeric = [MaxRunSeconds, MaxDailyRunSeconds, MinSecondsBetweenRuns, MaxDailyAmountMl];
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

public static class ScheduleKinds
{
    public const string Actuator = "actuator";
    public const string AiReview = "ai_review";
    public const string Report = "report";
    public const string CameraSnapshot = "camera_snapshot";
    public static readonly string[] All = [Actuator, AiReview, Report, CameraSnapshot];
}
