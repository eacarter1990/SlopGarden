using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed class DeviceRegistry(
    SlopDbContext db,
    IDataProtectionProvider dp,
    NotificationService notifications,
    ConfigPushQueue configQueue,
    CameraFrameStore frames,
    LiveNotifier live)
{
    private readonly IDataProtector _protector = dp.CreateProtector("SlopGarden.DeviceCredential.v1");

    public async Task<DeviceRegistrationResponse> RegisterAsync(DeviceRegistrationRequest req, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.HardwareId) || string.IsNullOrWhiteSpace(req.RegistrationSecret))
            throw AppException.Bad("hardwareId and registrationSecret are required");

        var secretHash = TokenUtil.Hash(req.RegistrationSecret);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.HardwareId == req.HardwareId, ct);
        var isNew = device is null;
        if (device is null)
        {
            device = new Device
            {
                HardwareId = req.HardwareId,
                Name = req.Hostname,
                RegistrationSecretHash = secretHash,
                Status = DeviceStatuses.Pending,
            };
            db.Devices.Add(device);
        }
        else if (device.RegistrationSecretHash != secretHash)
        {
            // A reinstalled client (new secret) must be approved again.
            device.RegistrationSecretHash = secretHash;
            device.Status = DeviceStatuses.Pending;
            device.CredentialHash = null;
            device.PendingCredentialCiphertext = null;
        }
        else if (device.Status == DeviceStatuses.Approved)
        {
            // Same client lost its token: rotate the credential.
            IssueCredential(device);
        }

        device.Hostname = req.Hostname;
        device.Os = req.Os;
        device.Architecture = req.Architecture;
        device.ClientVersion = req.ClientVersion;
        device.DiscoveredVia = req.DiscoveredVia;
        device.IpAddress = ip;
        device.LocalEndpoint = req.LocalEndpoint;
        device.LastSeenAt = DateTimeOffset.UtcNow;
        ApplyEnvironment(device, req.Environment);
        await db.SaveChangesAsync(ct);
        await UpsertPluginsAsync(device.Id, req.Plugins, ct);

        if (isNew || device.Status == DeviceStatuses.Pending)
        {
            await notifications.CreateAsync(new NotificationRequest(null, Severities.Info, "device",
                $"New device waiting for approval: {device.Hostname}",
                $"`{device.Hostname}` ({device.Os} {device.Architecture}, client {device.ClientVersion}) found via {device.DiscoveredVia} from {ip ?? "unknown"}. An Admin can approve it under **Devices**.",
                "device", device.Id, $"pending:{device.Id}"), ct);
            await live.Changed("devices");
        }
        return new DeviceRegistrationResponse(device.Id, device.Status, device.Status == DeviceStatuses.Pending ? "Waiting for an Admin to approve this device" : null);
    }

    public async Task<DeviceRegistrationStatus> PollAsync(Guid deviceId, string secret, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw AppException.NotFound("Device");
        if (device.RegistrationSecretHash != TokenUtil.Hash(secret)) throw AppException.Forbidden("Registration secret mismatch");
        device.LastSeenAt = DateTimeOffset.UtcNow;
        string? token = null;
        if (device.Status == DeviceStatuses.Approved && device.PendingCredentialCiphertext is { } cipher)
        {
            token = _protector.Unprotect(cipher);
            device.PendingCredentialCiphertext = null;
        }
        await db.SaveChangesAsync(ct);
        return new DeviceRegistrationStatus(device.Id, device.Status, token, device.Status switch
        {
            DeviceStatuses.Pending => "Waiting for approval",
            DeviceStatuses.Rejected => "Rejected by an Admin",
            DeviceStatuses.Revoked => "Credential revoked; re-register",
            _ => token is null ? "Approved (token already delivered)" : "Approved",
        });
    }

    private void IssueCredential(Device device)
    {
        var token = TokenUtil.NewToken(TokenUtil.DevicePrefix);
        device.CredentialHash = TokenUtil.Hash(token);
        device.PendingCredentialCiphertext = _protector.Protect(token);
        device.CredentialIssuedAt = DateTimeOffset.UtcNow;
    }

    public async Task<Device> ApproveAsync(Guid deviceId, Guid? gardenId, string? name, Actor actor, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw AppException.Forbidden("Only Admins can approve devices");
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw AppException.NotFound("Device");
        gardenId ??= await db.Gardens.Where(g => g.ArchivedAt == null).OrderBy(g => g.CreatedAt).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
        device.Status = DeviceStatuses.Approved;
        device.ApprovedAt = DateTimeOffset.UtcNow;
        device.ApprovedBy = actor.UserId;
        device.GardenId = gardenId;
        if (!string.IsNullOrWhiteSpace(name)) device.Name = name;
        IssueCredential(device);

        if (gardenId is { } gid && await db.Gardens.FirstOrDefaultAsync(g => g.Id == gid, ct) is { } garden)
            InheritEnvironment(garden, device);

        db.Audit(actor, "device.approve", "device", device.Id, new() { ["gardenId"] = gardenId?.ToString() });
        await db.SaveChangesAsync(ct);
        configQueue.Dirty(device.Id);
        await live.Changed("devices");
        return device;
    }

    public async Task<Device> SetStatusAsync(Guid deviceId, string status, Actor actor, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw AppException.Forbidden("Only Admins can change device status");
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw AppException.NotFound("Device");
        device.Status = status;
        device.CredentialHash = null;
        device.PendingCredentialCiphertext = null;
        db.Audit(actor, $"device.{status}", "device", device.Id);
        await db.SaveChangesAsync(ct);
        await live.Changed("devices");
        return device;
    }

    /// <summary>Deletes the device and everything configured on it: sensors, cameras, actuators, readings, frames, and schedules that drive those actuators. Plants and beds stay.</summary>
    public async Task<DeviceRemoved> RemoveAsync(Guid deviceId, Actor actor, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw AppException.Forbidden("Only Admins can remove devices");
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw AppException.NotFound("Device");
        var sensorIds = await db.Sensors.Where(s => s.DeviceId == deviceId).Select(s => s.Id).ToListAsync(ct);
        var channelIds = await db.SensorChannels.Where(c => sensorIds.Contains(c.SensorId)).Select(c => c.Id).ToListAsync(ct);
        var actuatorIds = await db.Actuators.Where(a => a.DeviceId == deviceId).Select(a => a.Id).ToListAsync(ct);
        var cameraIds = await db.Cameras.Where(c => c.DeviceId == deviceId).Select(c => c.Id).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (cameraIds.Count > 0)
            await db.Media.Where(m => m.CameraId != null && cameraIds.Contains(m.CameraId.Value)).ExecuteDeleteAsync(ct);
        await db.DeviceUpdates.Where(u => u.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.SafetyLimits.Where(l =>
            (l.ChannelId != null && channelIds.Contains(l.ChannelId.Value))
            || (l.ScopeId == deviceId && l.ScopeType == ScopeTypes.Device)
            || (l.ScopeId != null && l.ScopeType == ScopeTypes.Actuator && actuatorIds.Contains(l.ScopeId.Value)))
            .ExecuteDeleteAsync(ct);
        await db.EmergencyStops.Where(s =>
            (s.ScopeId == deviceId && s.ScopeType == ScopeTypes.Device)
            || (s.ScopeId != null && s.ScopeType == ScopeTypes.Actuator && actuatorIds.Contains(s.ScopeId.Value)))
            .ExecuteDeleteAsync(ct);
        await db.NotificationMutes.Where(m =>
            (m.ScopeType == "device" && m.ScopeId == deviceId)
            || (m.ScopeType == "sensor" && sensorIds.Contains(m.ScopeId))
            || (m.ScopeType == "actuator" && actuatorIds.Contains(m.ScopeId)))
            .ExecuteDeleteAsync(ct);

        db.Audit(actor, "device.remove", "device", device.Id, new()
        {
            ["name"] = device.Name,
            ["sensors"] = sensorIds.Count,
            ["actuators"] = actuatorIds.Count,
            ["cameras"] = cameraIds.Count,
        });
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var id in cameraIds) frames.Drop(id);
        await live.Changed("devices", "sensors", "actuators", "cameras", "safety");
        return new DeviceRemoved(device.Id, device.Name, sensorIds.Count, actuatorIds.Count, cameraIds.Count);
    }

    /// <summary>Location and timezone are inherited from the client unless the user set them manually.</summary>
    public static void InheritEnvironment(Garden garden, Device device)
    {
        if (garden.TimezoneSource == "device" && !string.IsNullOrWhiteSpace(device.ReportedTimezoneId))
            garden.TimezoneId = device.ReportedTimezoneId!;
        if (garden.LocationSource == "device" && device.ReportedLatitude is not null && device.ReportedLongitude is not null)
        {
            garden.Latitude = device.ReportedLatitude;
            garden.Longitude = device.ReportedLongitude;
            garden.LocationLabel = device.ReportedLocationLabel ?? garden.LocationLabel;
        }
    }

    public static void ApplyEnvironment(Device device, DeviceEnvironmentInfo? env)
    {
        if (env is null) return;
        device.ReportedTimezoneId = env.TimeZoneId;
        device.ReportedUtcOffsetMin = env.UtcOffsetMinutes;
        device.ReportedLatitude = env.Latitude;
        device.ReportedLongitude = env.Longitude;
        device.ReportedLocationLabel = env.LocationLabel;
    }

    public async Task UpsertPluginsAsync(Guid deviceId, IReadOnlyList<PluginInfo>? plugins, CancellationToken ct)
    {
        if (plugins is null) return;
        var existing = await db.DevicePlugins.Where(p => p.DeviceId == deviceId).ToListAsync(ct);
        var catalog = await db.PluginCatalog.AsNoTracking().Where(c => c.Status == "approved").ToListAsync(ct);
        foreach (var info in plugins)
        {
            var row = existing.FirstOrDefault(p => p.PluginId == info.PluginId);
            if (row is null)
            {
                row = new DevicePlugin { DeviceId = deviceId, PluginId = info.PluginId };
                db.DevicePlugins.Add(row);
            }
            row.Version = info.Version;
            row.DisplayName = info.DisplayName;
            row.Kinds = info.Kinds.ToList();
            row.Sha256 = info.Sha256;
            row.Source = info.Source;
            row.Trust = info.Trust;
            row.Publisher = info.Publisher;
            row.SignatureKeyId = info.SignatureKeyId;
            row.Description = info.Description;
            row.Components = System.Text.Json.JsonSerializer.SerializeToNode(info.Components ?? [], SlopJson.Options)!.AsArray();
            row.CatalogEntryId = catalog.FirstOrDefault(c => c.PluginId == info.PluginId && c.Version == info.Version && c.Sha256 == info.Sha256)?.Id;
            row.ReportedAt = DateTimeOffset.UtcNow;
        }
        foreach (var gone in existing.Where(e => plugins.All(p => p.PluginId != e.PluginId))) db.DevicePlugins.Remove(gone);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertDiscoveredAsync(Guid deviceId, DiscoveredHardwareReport report, CancellationToken ct)
    {
        var existing = await db.DiscoveredHardware.Where(d => d.DeviceId == deviceId).ToListAsync(ct);
        foreach (var item in report.Items)
        {
            var row = existing.FirstOrDefault(e => e.PluginId == item.PluginId && e.Address == item.Address);
            if (row is null)
            {
                row = new DiscoveredHardware { DeviceId = deviceId, PluginId = item.PluginId, Address = item.Address };
                db.DiscoveredHardware.Add(row);
            }
            row.Kind = item.Kind;
            row.SuggestedName = item.SuggestedName;
            row.SuggestedSettings = item.SuggestedSettings;
            row.Channels = System.Text.Json.JsonSerializer.SerializeToNode(item.Channels, SlopJson.Options)!.AsArray();
            row.ActuatorKind = item.ActuatorKind;
            row.ReportedAt = DateTimeOffset.UtcNow;
        }
        if (report.Complete != false)
        {
            var seen = report.Items.Select(i => (i.PluginId, i.Address)).ToHashSet();
            foreach (var gone in existing.Where(e => e.AdoptedComponentId is null && !seen.Contains((e.PluginId, e.Address))))
                db.DiscoveredHardware.Remove(gone);
        }
        await db.SaveChangesAsync(ct);
        await live.Changed("devices");
    }
}

public sealed record DeviceRemoved(Guid DeviceId, string Name, int Sensors, int Actuators, int Cameras);
