using System.Text.Json.Nodes;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record GardenDto(
    Guid Id, string Name, string Description, string Environment, string LocationSource, double? Latitude, double? Longitude, double? ElevationM,
    string? LocationLabel, string TimezoneId, string TimezoneSource, string? HardinessZone, string? ClimateNotes, string AiContext, string AiAutonomy,
    string DefaultGrowingMedium, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static GardenDto From(Garden g) => new(g.Id, g.Name, g.Description, g.Environment, g.LocationSource, g.Latitude, g.Longitude, g.ElevationM,
        g.LocationLabel, g.TimezoneId, g.TimezoneSource, g.HardinessZone, g.ClimateNotes, g.AiContext, g.AiAutonomy, g.DefaultGrowingMedium, g.CreatedAt, g.UpdatedAt);
}

public sealed record BedDto(
    Guid Id, Guid GardenId, string Name, string Kind, string? Environment, double? WidthCm, double? LengthCm, double? DepthCm, string? SoilType,
    string? SoilNotes, string IrrigationType, string SunExposure, double? LayoutX, double? LayoutY, string AiContext, string Notes, int PlantCount,
    DateTimeOffset CreatedAt, string? GrowingMedium, string? HydroSystem, Guid? ReservoirId)
{
    public static BedDto From(Bed b, int plantCount) => new(b.Id, b.GardenId, b.Name, b.Kind, b.Environment, b.WidthCm, b.LengthCm, b.DepthCm,
        b.SoilType, b.SoilNotes, b.IrrigationType, b.SunExposure, b.LayoutX, b.LayoutY, b.AiContext, b.Notes, plantCount, b.CreatedAt,
        b.GrowingMedium, b.HydroSystem, b.ReservoirId);
}

public sealed record PlantDto(
    Guid Id, Guid BedId, Guid GardenId, Guid? PlantProfileId, string? ProfileName, string Name, string? Variety, int Quantity, string Stage,
    string StartedFrom, DateOnly? PlantedAt, DateOnly? ExpectedHarvestStart, DateOnly? ExpectedHarvestEnd, string? HarvestPredictionNotes,
    string AiContext, string Notes, double? LayoutX, double? LayoutY, DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt)
{
    public static PlantDto From(Plant p, Guid gardenId) => new(p.Id, p.BedId, gardenId, p.PlantProfileId, p.PlantProfile?.CommonName, p.Name,
        p.Variety, p.Quantity, p.Stage, p.StartedFrom, p.PlantedAt, p.ExpectedHarvestStart, p.ExpectedHarvestEnd, p.HarvestPredictionNotes,
        p.AiContext, p.Notes, p.LayoutX, p.LayoutY, p.CreatedAt, p.RemovedAt);
}

public sealed record DeviceDto(
    Guid Id, string Name, string HardwareId, string Hostname, string Status, string DiscoveredVia, string? IpAddress, string? LocalEndpoint,
    string Os, string Architecture, string ClientVersion, Guid? GardenId, DateTimeOffset? ApprovedAt, string? ReportedTimezoneId,
    double? ReportedLatitude, double? ReportedLongitude, string? ReportedLocationLabel, int DesiredConfigVersion, int? AppliedConfigVersion,
    JsonArray ConfigErrors, DateTimeOffset? LastSeenAt, bool IsOnline, int BufferedReadings, DateTimeOffset CreatedAt,
    int UnsignedPlugins, int InvalidPlugins)
{
    public static DeviceDto From(Device d, int unsigned = 0, int invalid = 0) => new(d.Id, d.Name, d.HardwareId, d.Hostname, d.Status, d.DiscoveredVia,
        d.IpAddress, d.LocalEndpoint, d.Os, d.Architecture, d.ClientVersion, d.GardenId, d.ApprovedAt, d.ReportedTimezoneId, d.ReportedLatitude,
        d.ReportedLongitude, d.ReportedLocationLabel, d.DesiredConfigVersion, d.AppliedConfigVersion, d.ConfigErrors, d.LastSeenAt, d.IsOnline,
        d.BufferedReadings, d.CreatedAt, unsigned, invalid);
}

public sealed record ChannelDto(
    Guid Id, string Key, string Metric, string Unit, string? DisplayName, CalibrationConfig? Calibration, string CalibrationStatus,
    DateTimeOffset? CalibratedAt, double? MinValid, double? MaxValid, double? AlertLow, double? AlertHigh, double? LastValue, double? LastRawValue,
    short? LastQuality, DateTimeOffset? LastValueAt)
{
    public static ChannelDto From(SensorChannel c) => new(c.Id, c.Key, c.Metric, c.Unit, c.DisplayName, c.Calibration, c.CalibrationStatus,
        c.CalibratedAt, c.MinValid, c.MaxValid, c.AlertLow, c.AlertHigh, c.LastValue, c.LastRawValue, c.LastQuality, c.LastValueAt);
}

public sealed record SensorDto(
    Guid Id, Guid DeviceId, string? DeviceName, string Name, string PluginId, JsonObject Settings, int ReadIntervalSec, bool Enabled,
    string TargetType, Guid GardenId, Guid? BedId, Guid? PlantId, string Status, string? LastError, IReadOnlyList<ChannelDto> Channels, Guid? ReservoirId)
{
    public static SensorDto From(Sensor s) => new(s.Id, s.DeviceId, s.Device?.Name, s.Name, s.PluginId, s.Settings, s.ReadIntervalSec, s.Enabled,
        s.TargetType, s.GardenId, s.BedId, s.PlantId, s.Status, s.LastError, s.Channels.OrderBy(c => c.Key).Select(ChannelDto.From).ToList(), s.ReservoirId);
}

public sealed record ActuatorDto(
    Guid Id, Guid DeviceId, string? DeviceName, string Name, string Kind, string PluginId, JsonObject Settings, bool Enabled, string FailsafeState,
    double? FlowRateMlPerMin, string TargetType, Guid GardenId, Guid? BedId, Guid? PlantId, bool IsOn, double? Level, DateTimeOffset? StateSince,
    string Status, string? LastError, Guid? ReservoirId, string? Role, bool ContinuousRun, Guid? FlowChannelId, double? FlowMinWhenOn, int? FlowGraceSec,
    double? FlowMaxWhenOff, bool StopOnStall)
{
    public static ActuatorDto From(Actuator a) => new(a.Id, a.DeviceId, a.Device?.Name, a.Name, a.Kind, a.PluginId, a.Settings, a.Enabled,
        a.FailsafeState, a.FlowRateMlPerMin, a.TargetType, a.GardenId, a.BedId, a.PlantId, a.IsOn, a.Level, a.StateSince, a.Status, a.LastError,
        a.ReservoirId, a.Role, a.ContinuousRun, a.FlowChannelId, a.FlowMinWhenOn, a.FlowGraceSec, a.FlowMaxWhenOff, a.StopOnStall);
}

public sealed record CameraDto(
    Guid Id, Guid DeviceId, string? DeviceName, string Name, string PluginId, JsonObject Settings, int CaptureWidth, int CaptureHeight,
    int FrameIntervalSec, bool Enabled, string TargetType, Guid GardenId, Guid? BedId, Guid? PlantId, DateTimeOffset? LastFrameAt, string Status,
    string SnapshotUrl, Guid? ReservoirId)
{
    public static CameraDto From(Camera c) => new(c.Id, c.DeviceId, c.Device?.Name, c.Name, c.PluginId, c.Settings, c.CaptureWidth, c.CaptureHeight,
        c.FrameIntervalSec, c.Enabled, c.TargetType, c.GardenId, c.BedId, c.PlantId, c.LastFrameAt, c.Status, $"/api/cameras/{c.Id}/snapshot", c.ReservoirId);
}

public sealed record UserDto(Guid Id, string Username, string DisplayName, string? Email, string Role, bool IsDisabled, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt)
{
    public static UserDto From(User u) => new(u.Id, u.Username, u.DisplayName, u.Email, u.Role, u.IsDisabled, u.CreatedAt, u.LastLoginAt);
}
