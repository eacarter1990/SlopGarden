using Cronos;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Background;

/// <summary>
/// Runs server-side schedules: <c>ai_review</c> (how often garden data is sent to the model), <c>report</c> and
/// <c>camera_snapshot</c>. Actuator schedules run on the devices, not here.
/// </summary>
public sealed class ServerScheduler(IServiceScopeFactory scopes, AgentService agent, ILogger<ServerScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduler tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var now = DateTimeOffset.UtcNow;
        var due = await db.Schedules.Where(s => s.Enabled && s.Kind != ScheduleKinds.Actuator).ToListAsync(ct);
        foreach (var s in due)
        {
            if (s.NextRunAt is null)
            {
                s.NextRunAt = Next(s, now);
                continue;
            }
            if (s.NextRunAt > now) continue;
            s.LastRunAt = now;
            s.NextRunAt = Next(s, now);
            var run = new ScheduleRun { ScheduleId = s.Id };
            db.ScheduleRuns.Add(run);
            await db.SaveChangesAsync(ct);
            try
            {
                await RunAsync(scope.ServiceProvider, db, s, ct);
                run.Status = "succeeded";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Status = "failed";
                run.Details = new() { ["error"] = ex.Message };
                log.LogWarning(ex, "Schedule {Name} failed", s.Name);
            }
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset? Next(Schedule s, DateTimeOffset from)
    {
        try
        {
            return CronExpression.Parse(s.Cron).GetNextOccurrence(from, TimeZones.Find(s.TimezoneId));
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    private async Task RunAsync(IServiceProvider sp, SlopDbContext db, Schedule s, CancellationToken ct)
    {
        switch (s.Kind)
        {
            case ScheduleKinds.CameraSnapshot:
            {
                var store = sp.GetRequiredService<CameraFrameStore>();
                var cams = await db.Cameras.Where(c => c.GardenId == s.GardenId && (s.BedId == null || c.BedId == s.BedId) && (s.PlantId == null || c.PlantId == s.PlantId)).ToListAsync(ct);
                foreach (var c in cams)
                {
                    if (store.Latest(c.Id) is not { } f) continue;
                    db.Media.Add(new Media { Kind = "camera_frame", CameraId = c.Id, GardenId = c.GardenId, BedId = c.BedId, PlantId = c.PlantId, ContentType = f.ContentType, Width = f.Width, Height = f.Height, SizeBytes = f.Data.Length, Data = f.Data, CapturedAt = f.CapturedAt });
                }
                break;
            }
            case ScheduleKinds.AiReview:
            case ScheduleKinds.Report:
            {
                var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == s.CreatedById, ct)
                    ?? await db.Users.AsNoTracking().Where(u => u.Role == Roles.Admin && !u.IsDisabled).OrderBy(u => u.CreatedAt).FirstAsync(ct);
                var title = s.Kind == ScheduleKinds.Report ? $"Reports: {s.Name}" : $"Scheduled review: {s.Name}";
                var conv = await db.ChatConversations.FirstOrDefaultAsync(c => c.UserId == owner.Id && c.Title == title && c.ArchivedAt == null, ct);
                if (conv is null)
                {
                    conv = new ChatConversation { UserId = owner.Id, Title = title, GardenId = s.GardenId };
                    db.ChatConversations.Add(conv);
                    await db.SaveChangesAsync(ct);
                }
                var prompt = s.Payload["prompt"]?.ToString() ?? (s.Kind == ScheduleKinds.Report
                    ? "Write a short garden report: current conditions per bed, growth progress, anything needing attention, and harvest predictions."
                    : "Scheduled check-in: review the current readings against each plant's needs. If something needs action, propose it (or act if already approved). Otherwise reply briefly that all is well.");
                var seq = (await db.ChatMessages.Where(m => m.ConversationId == conv.Id).MaxAsync(m => (int?)m.Seq, ct) ?? 0) + 1;
                db.ChatMessages.Add(new ChatMessage { ConversationId = conv.Id, Seq = seq, Role = "user", Content = $"[Scheduled: {s.Name}] {prompt}" });
                await db.SaveChangesAsync(ct);
                var actor = new Actor(ActorTypes.User, owner.Id, owner.DisplayName, owner.Role, PatScopes.All);
                var text = await agent.RunAsync(conv.Id, actor, s.Kind == ScheduleKinds.Report ? AiJobs.Reports : AiJobs.ScheduledReview, null, ct);
                if (s.Kind == ScheduleKinds.Report && !string.IsNullOrWhiteSpace(text))
                {
                    await sp.GetRequiredService<NotificationService>().CreateAsync(new NotificationRequest(s.GardenId, Severities.Info, "report", s.Name, text), ct);
                }
                break;
            }
        }
    }
}

/// <summary>Periodic cleanup: expire stale commands and AI activities, mark silent devices offline.</summary>
public sealed class HousekeepingService(IServiceScopeFactory scopes, AiActivityTracker activities, DeviceConnections connections, ILogger<HousekeepingService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
            await db.Devices.Where(d => d.IsOnline).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsOnline, false), stoppingToken);
            await db.AiActivities.Where(a => a.Status == "running").ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "failed"), stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
                var now = DateTimeOffset.UtcNow;
                await db.ActuatorCommands.Where(c => (c.Status == CommandStatuses.Pending || c.Status == CommandStatuses.Sent) && c.ExpiresAt < now)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatuses.Expired), stoppingToken);
                await db.AiProposals.Where(p => p.Status == ProposalStatuses.Pending && p.ExpiresAt < now)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProposalStatuses.Expired), stoppingToken);
                await activities.ExpireStaleAsync(TimeSpan.FromMinutes(30));
                var online = await db.Devices.Where(d => d.IsOnline).ToListAsync(stoppingToken);
                foreach (var d in online.Where(d => !connections.IsConnected(d.Id))) d.IsOnline = false;
                await db.SaveChangesAsync(stoppingToken);
                await db.Media.Where(m => m.RetainUntil != null && m.RetainUntil < now).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Housekeeping failed");
            }
        }
    }
}
