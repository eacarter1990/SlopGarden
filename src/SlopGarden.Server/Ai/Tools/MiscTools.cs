using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai.Tools;

public sealed record ListNotificationsInput(bool UnreadOnly = false, int Limit = 50, string? Category = null);
public sealed record MarkReadInput([property: Description("Notification ids; omit to mark all as read")] List<Guid>? Ids = null);

public sealed record MuteInput(
    [property: Description("plant | bed | device | garden | sensor")] string ScopeType,
    Guid ScopeId,
    [property: Description("Only mute this category (device, sensor, safety, ai, schedule, report); omit for all")] string? Category = null,
    [property: Description("Mute duration; omit to mute until unmuted")] double? Hours = null,
    string? Reason = null);

public sealed record MuteIdInput(Guid MuteId);
public sealed record ListProposalsInput([property: Description("pending | approved | rejected | executed | failed; omit for recent of any status")] string? Status = null, int Limit = 50);
public sealed record WebSearchInput(string Query, int MaxResults = 5);
public sealed record ActivitiesInput(bool RunningOnly = false, int Limit = 30);

public static class MiscTools
{
    public static void Register(ToolRegistry r)
    {
        const string notif = "Notifications";
        r.Read<ListNotificationsInput, List<Notification>>("list_notifications", notif, "Recent notifications (alerts, device events, safety blocks, AI proposals).",
            async (ctx, i) =>
            {
                var q = ctx.Db.Notifications.AsNoTracking().AsQueryable();
                if (i.UnreadOnly) q = q.Where(n => n.ReadAt == null);
                if (i.Category is { } c) q = q.Where(n => n.Category == c);
                return await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(i.Limit, 1, 500)).ToListAsync(ctx.Ct);
            });

        r.Mutate<MarkReadInput, int>("mark_notifications_read", notif, "Mark notifications as read.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Notifications.Where(n => n.ReadAt == null);
                if (i.Ids is { Count: > 0 } ids) q = q.Where(n => ids.Contains(n.Id));
                var n = await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, DateTimeOffset.UtcNow), ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("notifications");
                return n;
            },
            (ctx, i) => Task.FromResult(new ProposalPreview("Mark notifications read", "garden", null, null, "low", [])),
            minRole: Roles.ReadOnly);

        r.Read<EmptyInput, List<NotificationMute>>("list_notification_mutes", notif, "Active notification mutes per plant/bed/device.",
            async (ctx, _) => await ctx.Db.NotificationMutes.AsNoTracking().Where(m => m.MutedUntil == null || m.MutedUntil > DateTimeOffset.UtcNow).OrderByDescending(m => m.CreatedAt).ToListAsync(ctx.Ct));

        r.Mutate<MuteInput, NotificationMute>("mute_notifications", notif, "Mute notifications for a plant, bed, device, sensor or garden (optionally one category, optionally for N hours).",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.ScopeType, ["plant", "bed", "device", "garden", "sensor"], "scopeType");
                var m = new NotificationMute
                {
                    ScopeType = i.ScopeType, ScopeId = i.ScopeId, Category = i.Category, Reason = i.Reason ?? ctx.Reason,
                    MutedUntil = i.Hours is { } h ? DateTimeOffset.UtcNow.AddHours(h) : null, CreatedBy = ctx.Actor.UserId,
                };
                ctx.Db.NotificationMutes.Add(m);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("notifications");
                return m;
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Mute {i.Category ?? "all"} notifications for {i.ScopeType}" + (i.Hours is { } h ? $" for {h:0.#} h" : ""), i.ScopeType, i.ScopeId, null, "low", Patch.CreateChanges(i))));

        r.Mutate<MuteIdInput, bool>("unmute_notifications", notif, "Remove a notification mute.",
            async (ctx, i) =>
            {
                var m = Ensure.Found(await ctx.Db.NotificationMutes.FirstOrDefaultAsync(x => x.Id == i.MuteId, ctx.Ct), "Mute");
                ctx.Db.NotificationMutes.Remove(m);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("notifications");
                return true;
            },
            (ctx, i) => Task.FromResult(new ProposalPreview("Unmute notifications", "garden", null, null, "low", [])));

        const string ai = "AI";
        r.Read<ListProposalsInput, List<AiProposal>>("list_proposals", ai, "AI change proposals and their approval status.",
            async (ctx, i) =>
            {
                var q = ctx.Db.AiProposals.AsNoTracking().AsQueryable();
                if (i.Status is { } s) q = q.Where(p => p.Status == s);
                return await q.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(i.Limit, 1, 500)).ToListAsync(ctx.Ct);
            });

        r.Read<ActivitiesInput, List<AiActivity>>("list_ai_activities", ai, "What the AI is doing or recently did (drives the dashboard indicator).",
            async (ctx, i) =>
            {
                var q = ctx.Db.AiActivities.AsNoTracking().AsQueryable();
                if (i.RunningOnly) q = q.Where(a => a.Status == "running");
                return await q.OrderByDescending(a => a.StartedAt).Take(Math.Clamp(i.Limit, 1, 200)).ToListAsync(ctx.Ct);
            });

        r.Read<EmptyInput, List<AiStandingApproval>>("list_standing_approvals", ai, "Routine actions the user allowed the AI to repeat without asking.",
            async (ctx, _) => await ctx.Db.AiStandingApprovals.AsNoTracking().Where(s => s.RevokedAt == null).OrderByDescending(s => s.CreatedAt).ToListAsync(ctx.Ct));

        r.Read<GardenIdInput, EnvironmentContext>("get_environment_context", ai,
            "Current local date/time, season, hemisphere, daylight hours, sunrise/sunset, indoor/outdoor and location of a garden (inherited from the device unless overridden).",
            async (ctx, i) => ctx.Get<EnvironmentService>().For(await GardenTools.ResolveGardenAsync(ctx.Db, i.GardenId, ctx.Ct)));

        r.Read<WebSearchInput, List<WebSearchResult>>("web_search", ai, "Search the web with the configured provider (for crop research, pests, regional planting dates).",
            (ctx, i) => ctx.Get<WebSearchService>().SearchAsync(i.Query, Math.Clamp(i.MaxResults, 1, 10), ctx.Ct));

        const string catalog = "Catalogs";
        r.Read<EmptyInput, List<HardwareCatalogEntry>>("list_hardware_catalog", catalog, "Curated compatible sensors/actuators/cameras, which plugin drives them and whether SlopGarden recommends them.",
            async (ctx, _) => await ctx.Db.HardwareCatalog.AsNoTracking().OrderByDescending(h => h.Recommended).ThenBy(h => h.Kind).ThenBy(h => h.Model).ToListAsync(ctx.Ct));

        r.Read<EmptyInput, List<PluginCatalogEntry>>("list_plugin_catalog", catalog, "Approved/signed plugin builds and their signing publishers.",
            async (ctx, _) => await ctx.Db.PluginCatalog.AsNoTracking().Include(p => p.SigningKey).OrderBy(p => p.PluginId).ToListAsync(ctx.Ct));
    }
}
