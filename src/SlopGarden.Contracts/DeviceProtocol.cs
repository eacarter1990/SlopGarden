using System.Text.Json.Nodes;

namespace SlopGarden.Contracts;

// ---------------------------------------------------------------------------------------------
// Registration (HTTP). Client → POST /api/device-api/register, then polls
// GET /api/device-api/register/{deviceId}?secret=... until an Admin approves it.
// ---------------------------------------------------------------------------------------------

public sealed record DeviceEnvironmentInfo(
    string TimeZoneId,
    double UtcOffsetMinutes,
    DateTimeOffset LocalTime,
    double? Latitude,
    double? Longitude,
    string? LocationLabel);

public sealed record PluginInfo(
    string PluginId,
    string Version,
    string DisplayName,
    IReadOnlyList<string> Kinds,
    string Sha256,
    string Source,
    string Trust,
    string? Publisher,
    string? SignatureKeyId,
    string? Description,
    IReadOnlyList<PluginComponentInfo>? Components = null);

/// <summary>One sensor/actuator/camera component inside a plugin, with the JSON Schema of its settings.</summary>
public sealed record PluginComponentInfo(
    string Id,
    string Kind,
    string DisplayName,
    string Description,
    JsonObject SettingsSchema,
    IReadOnlyList<string>? ActuatorKinds = null);

public sealed record DeviceRegistrationRequest(
    string HardwareId,
    string Hostname,
    string Os,
    string Architecture,
    string ClientVersion,
    string RegistrationSecret,
    string DiscoveredVia,
    string? LocalEndpoint,
    DeviceEnvironmentInfo Environment,
    IReadOnlyList<PluginInfo> Plugins);

public sealed record DeviceRegistrationResponse(Guid DeviceId, string Status, string? Message);

/// <summary>Returned while polling. <see cref="DeviceToken"/> is delivered exactly once after approval.</summary>
public sealed record DeviceRegistrationStatus(Guid DeviceId, string Status, string? DeviceToken, string? Message);

/// <summary>UDP broadcast payloads used for LAN auto-discovery.</summary>
public sealed record DiscoveryProbe(string Type, string HardwareId, string Hostname, string ClientVersion);
public sealed record DiscoveryAnnouncement(string Type, string ServerUrl, string ServerName, string ServerVersion);

/// <summary>Body of POST http://{client-ip}:8787/pair sent by the server for "add device by IP" and for mDNS.</summary>
public sealed record ManualPairRequest(string ServerUrl, string? Via = null);

public sealed record ClientLocalStatus(
    string HardwareId,
    string Hostname,
    string ClientVersion,
    string State,
    string? ServerUrl,
    Guid? DeviceId,
    bool Connected,
    int? AppliedConfigVersion,
    IReadOnlyList<PluginInfo> Plugins);

// ---------------------------------------------------------------------------------------------
// Live channel: SignalR hub at /hubs/device, authenticated with "Authorization: Bearer {deviceToken}"
// (or ?access_token=). Method names below are the hub method names.
// ---------------------------------------------------------------------------------------------

public static class DeviceHubMethods
{
    // client → server
    public const string Hello = "Hello";
    public const string ReportReadings = "ReportReadings";
    public const string ReportActuatorState = "ReportActuatorState";
    public const string ReportActuatorRun = "ReportActuatorRun";
    public const string AckCommand = "AckCommand";
    public const string ReportEvent = "ReportEvent";
    public const string ReportConfigApplied = "ReportConfigApplied";
    public const string ReportDiscoveredHardware = "ReportDiscoveredHardware";

    // server → client
    public const string ApplyConfig = "ApplyConfig";
    public const string ExecuteCommand = "ExecuteCommand";
    public const string CaptureFrame = "CaptureFrame";
    public const string EmergencyStop = "EmergencyStop";
    public const string RequestDiscovery = "RequestDiscovery";
    /// <summary>Ask the client to replace itself with a published container image. Image updates only — never a safety or approval bypass.</summary>
    public const string ApplyUpdate = "ApplyUpdate";

    // client → server
    public const string ReportUpdate = "ReportUpdate";
}

public sealed record DeviceHello(
    string ClientVersion,
    string Os,
    string Architecture,
    string Hostname,
    int? AppliedConfigVersion,
    DeviceEnvironmentInfo Environment,
    IReadOnlyList<PluginInfo> Plugins,
    int BufferedReadings,
    /// <summary>False when this process is not a container that can replace itself. Null from an older client, which is still offered an update.</summary>
    bool? CanSelfUpdate = null);

public sealed record ReadingSample(Guid ChannelId, DateTimeOffset Time, double Value, double? RawValue, short Quality);

public sealed record ReadingBatch(Guid BatchId, IReadOnlyList<ReadingSample> Readings);

public sealed record ReadingBatchAck(Guid BatchId, int Accepted);

public sealed record ActuatorStateReport(Guid ActuatorId, bool On, double? Level, DateTimeOffset Since, Guid? CommandId, string Reason);

/// <summary>A completed actuator run. Reported for commands and for schedules the client ran on its own (including offline).</summary>
public sealed record ActuatorRunReport(
    Guid RunId,
    Guid ActuatorId,
    Guid? CommandId,
    Guid? ScheduleId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationSec,
    double? AmountMl,
    string Source,
    string? StopReason);

public sealed record CommandAck(Guid CommandId, string Status, string? Reason, DateTimeOffset Time);

public sealed record DeviceEventReport(
    DateTimeOffset Time,
    string Severity,
    string Category,
    string Message,
    Guid? SensorId,
    Guid? ActuatorId,
    Guid? CameraId,
    string? Code = null);

public sealed record ConfigAppliedReport(int Version, bool Success, IReadOnlyList<string> Errors);

public sealed record DiscoveredChannel(string Key, string Metric, string Unit);

public sealed record DiscoveredHardwareItem(
    string PluginId,
    string Kind,
    string Address,
    string SuggestedName,
    JsonObject SuggestedSettings,
    IReadOnlyList<DiscoveredChannel> Channels,
    string? ActuatorKind);

public sealed record DiscoveredHardwareReport(IReadOnlyList<DiscoveredHardwareItem> Items);

public sealed record ActuatorCommandMessage(
    Guid CommandId,
    Guid ActuatorId,
    string Action,
    double? DurationSec,
    double? AmountMl,
    double? Level,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Source,
    string? Reason);

/// <summary>Ask the client to capture a frame and upload it to
/// POST /api/device-api/cameras/{cameraId}/frames?requestId={RequestId} (body: image bytes).</summary>
public sealed record CaptureFrameRequest(Guid RequestId, Guid CameraId, int? Width, int? Height);

/// <summary>Server → client. <paramref name="Image"/> is a container reference such as <c>ghcr.io/owner/slopgarden-client:0.2.3</c>.</summary>
public sealed record ApplyUpdateCommand(string Version, string Image);

/// <summary>Client → server after an update is accepted, skipped, or fails before the process is replaced.</summary>
public sealed record UpdateResultReport(string Version, string Status, string? Error);

public sealed record EmergencyStopState(bool DeviceStopped, IReadOnlyList<Guid> StoppedActuatorIds, string? Reason)
{
    public static readonly EmergencyStopState None = new(false, [], null);

    public bool IsStopped(Guid actuatorId) => DeviceStopped || StoppedActuatorIds.Contains(actuatorId);
}
