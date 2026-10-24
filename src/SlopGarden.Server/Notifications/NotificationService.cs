using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed record NotificationRequest(
    Guid? GardenId,
    string Severity,
    string Category,
    string Title,
    string Body,
    string? TargetType = null,
    Guid? TargetId = null,
    string? DedupeKey = null);

/// <summary>A delivery channel. Add new channel kinds (email, Telegram, push…) by implementing this.</summary>
public interface INotificationSender
{
    string Kind { get; }
    Task SendAsync(NotificationChannel channel, Notification notification, CancellationToken ct);
}

public sealed class BrowserNotificationSender(LiveNotifier live) : INotificationSender
{
    public string Kind => "browser";

    public Task SendAsync(NotificationChannel channel, Notification n, CancellationToken ct) =>
        live.All(LiveEvents.Notification, new { n.Id, n.Severity, n.Category, n.Title, n.Body, n.TargetType, n.TargetId, n.CreatedAt, browser = true });
}

public sealed class DiscordNotificationSender(IHttpClientFactory http, SecretStore secrets) : INotificationSender
{
    public string Kind => "discord";

    public async Task SendAsync(NotificationChannel channel, Notification n, CancellationToken ct)
    {
        var url = await secrets.GetAsync(channel.SecretId, ct) ?? throw new InvalidOperationException("Discord webhook URL not set");
        var color = n.Severity switch { Severities.Critical => 0xD64545, Severities.Warning => 0xE0A526, _ => 0x3FA34D };
        var payload = new
        {
            username = "SlopGarden",
            embeds = new[]
            {
                new { title = n.Title, description = n.Body.Length > 3900 ? n.Body[..3900] : n.Body, color, timestamp = n.CreatedAt.ToString("O"),
                    footer = new { text = $"{n.Severity} · {n.Category}" } },
            },
        };
        using var res = await http.CreateClient("discord").PostAsJsonAsync(url, payload, ct);
        res.EnsureSuccessStatusCode();
    }
}

public sealed class NotificationService(
    SlopDbContext db,
    IEnumerable<INotificationSender> senders,
    LiveNotifier live,
    ActivityLog activity,
    ILogger<NotificationService> log)
{
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(30);

    public async Task<bool> IsMutedAsync(NotificationRequest r, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var scopes = new List<(string Type, Guid Id)>();
        if (r.TargetType is not null && r.TargetId is { } tid) scopes.Add((r.TargetType, tid));
        if (r.GardenId is { } gid) scopes.Add(("garden", gid));

        if (r.TargetType is "sensor" && r.TargetId is { } sid)
        {
            var s = await db.Sensors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid, ct);
            if (s is not null) AddComponentScopes(scopes, s.DeviceId, s.BedId, s.PlantId);
        }
        else if (r.TargetType is "actuator" && r.TargetId is { } aid)
        {
            var a = await db.Actuators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == aid, ct);
            if (a is not null) AddComponentScopes(scopes, a.DeviceId, a.BedId, a.PlantId);
        }
        else if (r.TargetType is "plant" && r.TargetId is { } pid)
        {
            var bed = await db.Plants.Where(p => p.Id == pid).Select(p => (Guid?)p.BedId).FirstOrDefaultAsync(ct);
            if (bed is { } b) scopes.Add(("bed", b));
        }

        var ids = scopes.Select(s => s.Id).ToList();
        var mutes = await db.NotificationMutes.AsNoTracking()
            .Where(m => ids.Contains(m.ScopeId) && (m.MutedUntil == null || m.MutedUntil > now))
            .ToListAsync(ct);
        return mutes.Any(m => scopes.Contains((m.ScopeType, m.ScopeId)) && (m.Category is null || m.Category == r.Category));
    }

    private static void AddComponentScopes(List<(string, Guid)> scopes, Guid deviceId, Guid? bedId, Guid? plantId)
    {
        scopes.Add(("device", deviceId));
        if (bedId is { } b) scopes.Add(("bed", b));
        if (plantId is { } p) scopes.Add(("plant", p));
    }

    public async Task<Notification?> CreateAsync(NotificationRequest r, CancellationToken ct)
    {
        try
        {
            if (r.DedupeKey is { } key)
            {
                var since = DateTimeOffset.UtcNow - DedupeWindow;
                if (await db.Notifications.AnyAsync(n => n.DedupeKey == key && n.CreatedAt > since, ct)) return null;
            }

            var muted = await IsMutedAsync(r, ct);
            var n = new Notification
            {
                GardenId = r.GardenId,
                Severity = r.Severity,
                Category = r.Category,
                Title = r.Title,
                Body = r.Body,
                TargetType = r.TargetType,
                TargetId = r.TargetId,
                DedupeKey = r.DedupeKey,
            };
            db.Notifications.Add(n);
            await db.SaveChangesAsync(ct);
            activity.Note(r.GardenId, r.TargetType, r.TargetId, $"[{r.Severity}] {r.Title} — {r.Body}");

            var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
            foreach (var ch in channels)
            {
                var delivery = new NotificationDelivery { NotificationId = n.Id, ChannelId = ch.Id };
                if (muted || Severities.Rank(n.Severity) < Severities.Rank(ch.MinSeverity) || (ch.Categories.Count > 0 && !ch.Categories.Contains(n.Category)))
                {
                    delivery.Status = "suppressed";
                }
                else if (senders.FirstOrDefault(s => s.Kind == ch.Kind) is { } sender)
                {
                    try
                    {
                        await sender.SendAsync(ch, n, ct);
                    }
                    catch (Exception ex)
                    {
                        delivery.Status = "failed";
                        delivery.Error = ex.Message;
                        log.LogWarning(ex, "Notification delivery via {Channel} failed", ch.Name);
                    }
                }
                db.NotificationDeliveries.Add(delivery);
            }
            await db.SaveChangesAsync(ct);
            if (!muted) await live.All(LiveEvents.Notification, new { n.Id, n.Severity, n.Category, n.Title, n.Body, n.TargetType, n.TargetId, n.CreatedAt, browser = false });
            return n;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to create notification {Title}", r.Title);
            return null;
        }
    }
}
