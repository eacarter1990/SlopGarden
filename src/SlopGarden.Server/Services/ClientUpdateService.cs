using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed record ClientReleaseInfo(string Version, string Image);

/// <summary>
/// Compares each device's reported version with the configured client image and notifies (and, when the device
/// opts in, applies) an update. This is an image update only. It does not approve AI proposals or relax safety limits.
/// </summary>
public sealed class ClientUpdateService(
    IServiceScopeFactory scopes,
    IOptions<ServerOptions> options,
    DeviceConnections connections,
    IHubContext<DeviceHub> hub,
    ReleaseCatalog releases,
    ServerImageUpdater serverUpdater,
    ILogger<ClientUpdateService> log) : BackgroundService
{
    public const string SettingKey = "client_release";

    public static async Task<ClientReleaseInfo> ReadAsync(SlopDbContext db, CancellationToken ct)
    {
        var row = await db.ServerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        var version = row?.Value?["version"]?.ToString() ?? "";
        var image = row?.Value?["image"]?.ToString() ?? "";
        return new ClientReleaseInfo(version, image);
    }

    public static async Task SaveAsync(SlopDbContext db, string version, string image, Guid? userId, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        var value = new JsonObject { ["version"] = version.Trim(), ["image"] = image.Trim() };
        if (row is null) db.ServerSettings.Add(new ServerSetting { Key = SettingKey, Value = value, UpdatedBy = userId });
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = userId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> PushAsync(Guid deviceId, bool manual, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var release = await ResolveAsync(db, ct);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw AppException.NotFound("Device");
        if (string.IsNullOrWhiteSpace(release.Version) || string.IsNullOrWhiteSpace(release.Image))
            throw AppException.Bad("Set the client version and container image before updating a device.");
        if (device.CanSelfUpdate == false)
            throw AppException.Bad("This device is running as a program, not a container, so the device list cannot install an image on it.");
        if (!manual && !ClientVersions.IsOutdated(device.ClientVersion, release.Version))
            return "already current";
        if (!device.IsOnline || connections.Get(device.Id) is not { } conn)
        {
            device.UpdateStatus = "pending";
            device.UpdateError = "Device is offline. The update is sent the next time it connects.";
            await db.SaveChangesAsync(ct);
            return device.UpdateError;
        }
        device.UpdateStatus = "updating";
        device.UpdateError = null;
        device.NotifiedUpdateVersion = release.Version;
        await db.SaveChangesAsync(ct);
        await hub.Clients.Client(conn).SendAsync(DeviceHubMethods.ApplyUpdate, new ApplyUpdateCommand(release.Version, release.Image), ct);
        await notifications.CreateAsync(new NotificationRequest(device.GardenId, Severities.Info, "device",
            $"Updating {device.Name}", $"Sending client {release.Version} ({release.Image}).", "device", device.Id), ct);
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedFromConfigurationAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Client update check failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SeedFromConfigurationAsync(CancellationToken ct)
    {
        var version = options.Value.ClientReleaseVersion;
        var image = options.Value.ClientReleaseImage;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(image)) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var current = await ReadAsync(db, ct);
        if (!string.IsNullOrWhiteSpace(current.Version)) return;
        await SaveAsync(db, version, image, null, ct);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var release = await ResolveAsync(db, ct);
        await MaybeUpdateServerAsync(db, release, ct);
        if (string.IsNullOrWhiteSpace(release.Version)) return;
        var devices = await db.Devices.Where(d => d.Status == DeviceStatuses.Approved).ToListAsync(ct);
        foreach (var device in devices)
        {
            if (!ClientVersions.IsOutdated(device.ClientVersion, release.Version))
            {
                if (device.UpdateStatus is "updating" or "pending" or "failed") device.UpdateStatus = "current";
                continue;
            }
            if (device.NotifiedUpdateVersion != release.Version)
            {
                device.NotifiedUpdateVersion = release.Version;
                await notifications.CreateAsync(new NotificationRequest(device.GardenId, Severities.Info, "device",
                    $"Update available for {device.Name}",
                    $"{device.Name} is running {device.ClientVersion}. {release.Version} is published{(string.IsNullOrWhiteSpace(release.Image) ? "" : $" ({release.Image})")}.",
                    "device", device.Id, $"client-update:{device.Id}:{release.Version}"), ct);
            }
            if (device.AutoUpdate && device.CanSelfUpdate != false && device.UpdateStatus != "updating" && device.IsOnline && connections.Get(device.Id) is { } conn
                && !string.IsNullOrWhiteSpace(release.Image))
            {
                device.UpdateStatus = "updating";
                device.UpdateError = null;
                await hub.Clients.Client(conn).SendAsync(DeviceHubMethods.ApplyUpdate, new ApplyUpdateCommand(release.Version, release.Image), ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The selected release: latest, or the version an Admin pinned. Both images come from that release.</summary>
    public async Task<ClientReleaseInfo> ResolveAsync(SlopDbContext db, CancellationToken ct)
    {
        var published = await releases.ListAsync(ct);
        var track = await ReleaseCatalog.ReadTrackAsync(db, releases.Owner, ct);
        var chosen = ReleaseImages.Choose(published, track.Mode, track.Version, releases.Owner);
        if (chosen is null) return await ReadAsync(db, ct);
        await releases.RememberAsync(db, chosen, ct);
        return new ClientReleaseInfo(chosen.Version, chosen.ClientImage);
    }

    public async Task UpdateServerAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var release = await ResolveAsync(db, ct);
        if (string.IsNullOrWhiteSpace(release.Version))
            throw AppException.Bad("No release is available yet.");
        var serverImage = ReleaseImages.Server(releases.Owner, release.Version);
        await serverUpdater.ApplyAsync(serverImage, release.Image, ct);
    }

    private async Task MaybeUpdateServerAsync(SlopDbContext db, ClientReleaseInfo release, CancellationToken ct)
    {
        var track = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == ReleaseImages.TrackKey, ct);
        var auto = track?.Value?["serverAutoUpdate"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        if (!auto || string.IsNullOrWhiteSpace(release.Version)) return;
        if (!ClientVersions.IsOutdated(ServerImageUpdater.RunningVersion, release.Version)) return;
        if (track?.Value?["serverAttempt"]?.ToString() == release.Version) return;
        if (track?.Value is JsonObject obj) obj["serverAttempt"] = release.Version;
        await db.SaveChangesAsync(ct);
        if (!ServerImageUpdater.CanUpdate)
        {
            log.LogInformation("Server {Running} can move to {Version} when the Docker socket and SLOPGARDEN_CONTAINER are set", ServerImageUpdater.RunningVersion, release.Version);
            return;
        }
        try
        {
            await serverUpdater.ApplyAsync(ReleaseImages.Server(releases.Owner, release.Version), release.Image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Server auto-update to {Version} failed", release.Version);
        }
    }
}
