using System.Text.Json.Nodes;
using SlopGarden.Contracts;

namespace SlopGarden.Server.Data;

public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Sensors, actuators and cameras target a garden, a bed or a single plant.</summary>
public interface ITargeted
{
    string TargetType { get; set; }
    Guid GardenId { get; set; }
    Guid? BedId { get; set; }
    Guid? PlantId { get; set; }
    Guid? ReservoirId { get; set; }
}

// ----------------------------------------------------------------------------- access

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.ReadOnly;
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public class PersonalAccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = "";
    public string TokenPrefix { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string ActorType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public string ActorLabel { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public JsonObject Details { get; set; } = new();
}

// ----------------------------------------------------------------------------- settings

public class ServerSetting
{
    public string Key { get; set; } = "";
    public JsonNode? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

public class Secret : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ----------------------------------------------------------------------------- garden domain

public class Garden : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Environment { get; set; } = "outdoor";
    public string LocationSource { get; set; } = "device";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? ElevationM { get; set; }
    public string? LocationLabel { get; set; }
    public string TimezoneId { get; set; } = "UTC";
    public string TimezoneSource { get; set; } = "device";
    public string? HardinessZone { get; set; }
    public string? ClimateNotes { get; set; }
    public string AiContext { get; set; } = "";
    public string AiAutonomy { get; set; } = "standing_approvals";
    /// <summary>soil | soilless | hydroponic — used by beds that don't set their own.</summary>
    public string DefaultGrowingMedium { get; set; } = "soil";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<Bed> Beds { get; set; } = [];
}

public class Bed : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GardenId { get; set; }
    public Garden? Garden { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "raised_bed";
    public string? Environment { get; set; }
    public double? WidthCm { get; set; }
    public double? LengthCm { get; set; }
    public double? DepthCm { get; set; }
    public string? SoilType { get; set; }
    public string? SoilNotes { get; set; }
    public string IrrigationType { get; set; } = "drip";
    public string SunExposure { get; set; } = "full_sun";
    /// <summary>soil | soilless | hydroponic; null inherits the garden default.</summary>
    public string? GrowingMedium { get; set; }
    /// <summary>For hydroponic beds: nft, dwc, rdwc, ebb_flow, drip_recirculating, drip_to_waste, kratky, wick, aeroponic.</summary>
    public string? HydroSystem { get; set; }
    public Guid? ReservoirId { get; set; }
    public double? LayoutX { get; set; }
    public double? LayoutY { get; set; }
    public string AiContext { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<Plant> Plants { get; set; } = [];
}

public class PlantProfile : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string? ScientificName { get; set; }
    public string? Family { get; set; }
    public string Category { get; set; } = "vegetable";
    public string Description { get; set; } = "";
    public string WaterNeed { get; set; } = "medium";
    public double? SoilMoistureMinPct { get; set; }
    public double? SoilMoistureMaxPct { get; set; }
    public double? LightHoursMin { get; set; }
    public double? LightHoursMax { get; set; }
    public double? DliMin { get; set; }
    public double? AirTempMinC { get; set; }
    public double? AirTempMaxC { get; set; }
    public double? SoilTempMinC { get; set; }
    public double? HumidityMinPct { get; set; }
    public double? HumidityMaxPct { get; set; }
    public double? PhMin { get; set; }
    public double? PhMax { get; set; }
    public double? EcMin { get; set; }
    public double? EcMax { get; set; }
    public string? FertilizerNotes { get; set; }
    public int? DaysToGermination { get; set; }
    public int? DaysToMaturityMin { get; set; }
    public int? DaysToMaturityMax { get; set; }
    public double? SpacingCm { get; set; }
    public string FrostTolerance { get; set; } = "none";
    public JsonArray Sources { get; set; } = [];
    public bool IsBuiltin { get; set; }
    public string CreatedByType { get; set; } = ActorTypes.System;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Plant : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BedId { get; set; }
    public Bed? Bed { get; set; }
    public Guid? PlantProfileId { get; set; }
    public PlantProfile? PlantProfile { get; set; }
    public string Name { get; set; } = "";
    public string? Variety { get; set; }
    public int Quantity { get; set; } = 1;
    public double? LayoutX { get; set; }
    public double? LayoutY { get; set; }
    public string Stage { get; set; } = "vegetative";
    public string StartedFrom { get; set; } = "unknown";
    public DateOnly? PlantedAt { get; set; }
    public DateOnly? ExpectedHarvestStart { get; set; }
    public DateOnly? ExpectedHarvestEnd { get; set; }
    public string? HarvestPredictionNotes { get; set; }
    public string AiContext { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RemovedAt { get; set; }
}

public class CareTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = TargetTypes.Plant;
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public Guid? ReservoirId { get; set; }
    public string Metric { get; set; } = "";
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? IdealValue { get; set; }
    public string Source { get; set; } = "user";
    public string? Rationale { get; set; }
    public string UpdatedByType { get; set; } = ActorTypes.User;
    public Guid? UpdatedById { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GardenId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public Guid? ReservoirId { get; set; }
    public string Kind { get; set; } = "note";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public double? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Notes { get; set; } = "";
    public Guid? MediaId { get; set; }
    public string CreatedByType { get; set; } = ActorTypes.User;
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A nutrient-solution tank feeding one or more hydroponic beds. Water-side sensors and pumps/dosers target it.</summary>
public class Reservoir : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GardenId { get; set; }
    public string Name { get; set; } = "";
    public double? VolumeLiters { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    public int? ChangeIntervalDays { get; set; } = 14;
    public double ToppedUpLitersSinceChange { get; set; }
    public string AiContext { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
}

// ----------------------------------------------------------------------------- devices & plugins

public class Device : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Status { get; set; } = DeviceStatuses.Pending;
    public string DiscoveredVia { get; set; } = "broadcast";
    public string? IpAddress { get; set; }
    public string? LocalEndpoint { get; set; }
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public Guid? GardenId { get; set; }
    public string RegistrationSecretHash { get; set; } = "";
    public string? CredentialHash { get; set; }
    /// <summary>Plain token held only between approval and the client's next poll; then cleared.</summary>
    public string? PendingCredentialCiphertext { get; set; }
    public DateTimeOffset? CredentialIssuedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ReportedTimezoneId { get; set; }
    public double? ReportedUtcOffsetMin { get; set; }
    public double? ReportedLatitude { get; set; }
    public double? ReportedLongitude { get; set; }
    public string? ReportedLocationLabel { get; set; }
    public int DesiredConfigVersion { get; set; }
    public int? AppliedConfigVersion { get; set; }
    public JsonArray ConfigErrors { get; set; } = [];
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool IsOnline { get; set; }
    public int BufferedReadings { get; set; }
    /// <summary>When true, an outdated client image is applied without a per-update click. Safety limits and AI approvals are unchanged.</summary>
    public bool AutoUpdate { get; set; }
    /// <summary>False when the client reported it is not a container that can replace itself. Null until a client says.</summary>
    public bool? CanSelfUpdate { get; set; }
    public string? UpdateStatus { get; set; }
    public string? UpdateError { get; set; }
    /// <summary>Latest release version we already notified about, so a device is not announced twice.</summary>
    public string? NotifiedUpdateVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DeviceConfigRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public int Version { get; set; }
    public JsonObject Document { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAt { get; set; }
    public bool? Success { get; set; }
    public JsonArray Errors { get; set; } = [];
}

public class DevicePlugin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string PluginId { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Kinds { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public string Source { get; set; } = "builtin";
    public string Trust { get; set; } = PluginTrust.Builtin;
    public string? Publisher { get; set; }
    public string? SignatureKeyId { get; set; }
    public string? Description { get; set; }
    public JsonArray Components { get; set; } = [];
    public Guid? CatalogEntryId { get; set; }
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DiscoveredHardware
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string PluginId { get; set; } = "";
    public string Kind { get; set; } = PluginKinds.Sensor;
    public string Address { get; set; } = "";
    public string SuggestedName { get; set; } = "";
    public JsonObject SuggestedSettings { get; set; } = new();
    public JsonArray Channels { get; set; } = [];
    public string? ActuatorKind { get; set; }
    public Guid? AdoptedComponentId { get; set; }
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PluginSigningKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string KeyId { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Algorithm { get; set; } = PluginSigning.Algorithm;
    public string PublicKeySpki { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class PluginCatalogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public List<string> Kinds { get; set; } = [];
    public string Description { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Signature { get; set; } = "";
    public Guid? SigningKeyId { get; set; }
    public PluginSigningKey? SigningKey { get; set; }
    public string Status { get; set; } = "approved";
    public string? HomepageUrl { get; set; }
    public JsonObject SettingsSchema { get; set; } = new();
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class HardwareCatalogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "sensor";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Protocol { get; set; } = "";
    public List<string> Metrics { get; set; } = [];
    public string PluginId { get; set; } = "";
    public bool Recommended { get; set; }
    public string TestedStatus { get; set; } = "untested";
    public string Notes { get; set; } = "";
    public string? DatasheetUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public string Rid { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FilePath { get; set; } = "";
    /// <summary>Container image reference, for example ghcr.io/owner/slopgarden-client:0.2.3.</summary>
    public string Image { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DeviceUpdate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid ReleaseId { get; set; }
    public string Status { get; set; } = "available";
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
}

// ----------------------------------------------------------------------------- components & telemetry

public class Sensor : ITimestamped, ITargeted
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public string Name { get; set; } = "";
    public string PluginId { get; set; } = "";
    public Guid? HardwareCatalogId { get; set; }
    public JsonObject Settings { get; set; } = new();
    public int ReadIntervalSec { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public string TargetType { get; set; } = TargetTypes.Garden;
    public Guid GardenId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public Guid? ReservoirId { get; set; }
    public string Status { get; set; } = "ok";
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SensorChannel> Channels { get; set; } = [];
}

public class SensorChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SensorId { get; set; }
    public Sensor? Sensor { get; set; }
    public string Key { get; set; } = "value";
    public string Metric { get; set; } = "";
    public string Unit { get; set; } = "";
    public string? DisplayName { get; set; }
    public CalibrationConfig? Calibration { get; set; }
    public string CalibrationStatus { get; set; } = "uncalibrated";
    public DateTimeOffset? CalibratedAt { get; set; }
    public double? MinValid { get; set; }
    public double? MaxValid { get; set; }
    public double? AlertLow { get; set; }
    public double? AlertHigh { get; set; }
    public double? LastValue { get; set; }
    public double? LastRawValue { get; set; }
    public short? LastQuality { get; set; }
    public DateTimeOffset? LastValueAt { get; set; }
}

public class Reading
{
    public Guid ChannelId { get; set; }
    public DateTimeOffset Time { get; set; }
    public double Value { get; set; }
    public double? RawValue { get; set; }
    public short Quality { get; set; }
}

public class Actuator : ITimestamped, ITargeted
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ActuatorKinds.Valve;
    public string PluginId { get; set; } = "";
    public JsonObject Settings { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string FailsafeState { get; set; } = "off";
    public double? FlowRateMlPerMin { get; set; }
    public string TargetType { get; set; } = TargetTypes.Garden;
    public Guid GardenId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public Guid? ReservoirId { get; set; }
    /// <summary>See <c>ActuatorRoles</c>: irrigation, circulation_pump, air_pump, dose_* …</summary>
    public string? Role { get; set; }
    /// <summary>Kept on by the device (NFT/DWC circulation and air pumps).</summary>
    public bool ContinuousRun { get; set; }
    public Guid? FlowChannelId { get; set; }
    public double? FlowMinWhenOn { get; set; }
    public int? FlowGraceSec { get; set; }
    public double? FlowMaxWhenOff { get; set; }
    public bool StopOnStall { get; set; } = true;
    public bool IsOn { get; set; }
    public double? Level { get; set; }
    public DateTimeOffset? StateSince { get; set; }
    public string Status { get; set; } = "ok";
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ActuatorCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActuatorId { get; set; }
    public string Action { get; set; } = ActuatorActions.Pulse;
    public double? DurationSec { get; set; }
    public double? AmountMl { get; set; }
    public double? Level { get; set; }
    public string Reason { get; set; } = "";
    public string RequestedByType { get; set; } = ActorTypes.User;
    public Guid? RequestedById { get; set; }
    public Guid? ProposalId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string Status { get; set; } = CommandStatuses.Pending;
    public string? BlockedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(2);
}

public class ActuatorRun
{
    public Guid Id { get; set; }
    public Guid ActuatorId { get; set; }
    public Guid? CommandId { get; set; }
    public Guid? ScheduleId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationSec { get; set; }
    public double? AmountMl { get; set; }
    public string Source { get; set; } = "command";
    public string? StopReason { get; set; }
}

public class Camera : ITimestamped, ITargeted
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public string Name { get; set; } = "";
    public string PluginId { get; set; } = "";
    public JsonObject Settings { get; set; } = new();
    public int CaptureWidth { get; set; } = 1280;
    public int CaptureHeight { get; set; } = 720;
    public int FrameIntervalSec { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public string TargetType { get; set; } = TargetTypes.Garden;
    public Guid GardenId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public Guid? ReservoirId { get; set; }
    public DateTimeOffset? LastFrameAt { get; set; }
    public string Status { get; set; } = "ok";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Media
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "camera_frame";
    public Guid? CameraId { get; set; }
    public Guid? GardenId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetainUntil { get; set; }
}

/// <summary>A stored growth report: narrative, conditions, harvest predictions and the camera frames it was written from.</summary>
public class GrowthReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GardenId { get; set; }
    /// <summary>daily, weekly, monthly, or on_demand.</summary>
    public string Kind { get; set; } = "on_demand";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public JsonObject Conditions { get; set; } = new();
    public JsonArray Predictions { get; set; } = [];
    public JsonArray ImageIds { get; set; } = [];
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public Guid? ScheduleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DeviceEvent
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Severity { get; set; } = Severities.Info;
    public string Category { get; set; } = "system";
    public string Message { get; set; } = "";
    public Guid? SensorId { get; set; }
    public Guid? ActuatorId { get; set; }
    public Guid? CameraId { get; set; }
}

// ----------------------------------------------------------------------------- automation & safety

public class Schedule : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GardenId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ScheduleKinds.Actuator;
    public string Cron { get; set; } = "0 7 * * *";
    public string TimezoneId { get; set; } = "UTC";
    public Guid? ActuatorId { get; set; }
    public int? DurationSec { get; set; }
    public double? AmountMl { get; set; }
    public string? TargetType { get; set; }
    public Guid? BedId { get; set; }
    public Guid? PlantId { get; set; }
    public JsonObject Payload { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string CreatedByType { get; set; } = ActorTypes.User;
    public Guid? CreatedById { get; set; }
    public Guid? ProposalId { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScheduleRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public JsonObject Details { get; set; } = new();
}

public class SafetyLimit : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = ScopeTypes.Global;
    public Guid? ScopeId { get; set; }
    public string LimitType { get; set; } = SafetyLimitTypes.MaxRunSeconds;
    public string? ActuatorKind { get; set; }
    public double Value { get; set; }
    public Guid? ChannelId { get; set; }
    public string? Operator { get; set; }
    public bool Enabled { get; set; } = true;
    public string Notes { get; set; } = "";
    public string CreatedByType { get; set; } = ActorTypes.User;
    public Guid? CreatedById { get; set; }
    public Guid? ProposalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EmergencyStop
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = ScopeTypes.Global;
    public Guid? ScopeId { get; set; }
    public bool Active { get; set; } = true;
    public string Reason { get; set; } = "";
    public string ActivatedByType { get; set; } = ActorTypes.User;
    public Guid? ActivatedById { get; set; }
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ReleasedById { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

// ----------------------------------------------------------------------------- AI

public class AiProvider : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public Guid? ApiKeySecretId { get; set; }
    public string DefaultModel { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AiModelAssignment
{
    public string Job { get; set; } = AiJobs.Chat;
    public Guid ProviderId { get; set; }
    public AiProvider? Provider { get; set; }
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxOutputTokens { get; set; }
    public int ContextMessageLimit { get; set; } = 30;
}

public class ChatConversation : ITimestamped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New conversation";
    public Guid? GardenId { get; set; }
    public JsonObject LastPageContext { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public int Seq { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public JsonArray? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public JsonObject? PageContext { get; set; }
    public Guid? ProposalId { get; set; }
    public Guid? ProviderId { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AiProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public string Origin { get; set; } = "chat";
    public string RequestedByType { get; set; } = ActorTypes.Ai;
    public Guid? RequestedById { get; set; }
    public string ToolName { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public JsonArray Changes { get; set; } = [];
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public Guid? GardenId { get; set; }
    public string Risk { get; set; } = "low";
    /// <summary>Whether "always allow" may be offered (never for dosing).</summary>
    public bool AllowStanding { get; set; } = true;
    public string Status { get; set; } = ProposalStatuses.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public JsonNode? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class AiStandingApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ToolName { get; set; } = "";
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public JsonObject Constraints { get; set; } = new();
    public Guid? CreatedFromProposalId { get; set; }
    public Guid ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class AiActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? GardenId { get; set; }
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string Kind { get; set; } = "tool";
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "running";
    public Guid? ConversationId { get; set; }
    public Guid? ProposalId { get; set; }
    public Guid? CommandId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

// ----------------------------------------------------------------------------- notifications

public class NotificationChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "browser";
    public string Name { get; set; } = "";
    public Guid? SecretId { get; set; }
    public JsonObject Settings { get; set; } = new();
    public string MinSeverity { get; set; } = Severities.Info;
    public List<string> Categories { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? GardenId { get; set; }
    public string Severity { get; set; } = Severities.Info;
    public string Category { get; set; } = "system";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NotificationId { get; set; }
    public Guid ChannelId { get; set; }
    public string Status { get; set; } = "sent";
    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
}

public class NotificationMute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = TargetTypes.Plant;
    public Guid ScopeId { get; set; }
    public string? Category { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public string? Reason { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
