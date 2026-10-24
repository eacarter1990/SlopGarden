using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;

namespace SlopGarden.Server.Ai;

/// <summary>Records what the AI is doing right now so the dashboard can show an "AI acting" indicator per plant/bed/actuator.</summary>
public sealed class AiActivityTracker(IServiceScopeFactory scopes, LiveNotifier live)
{
    public async Task<Guid> StartAsync(Guid? gardenId, string targetType, Guid? targetId, string kind, string summary,
        Guid? conversationId = null, Guid? proposalId = null, Guid? commandId = null)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var a = new AiActivity
        {
            GardenId = gardenId,
            TargetType = targetType,
            TargetId = targetId,
            Kind = kind,
            Summary = summary,
            ConversationId = conversationId,
            ProposalId = proposalId,
            CommandId = commandId,
        };
        db.AiActivities.Add(a);
        await db.SaveChangesAsync();
        await live.All(LiveEvents.AiActivity, a);
        return a.Id;
    }

    public async Task FinishAsync(Guid id, bool success)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var a = await db.AiActivities.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null || a.Status != "running") return;
        a.Status = success ? "succeeded" : "failed";
        a.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await live.All(LiveEvents.AiActivity, a);
    }

    public async Task FinishForCommandAsync(Guid commandId, bool success)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var ids = await db.AiActivities.Where(x => x.CommandId == commandId && x.Status == "running").Select(x => x.Id).ToListAsync();
        foreach (var id in ids) await FinishAsync(id, success);
    }

    /// <summary>Activities left running (e.g. command never acknowledged) are closed after a timeout.</summary>
    public async Task ExpireStaleAsync(TimeSpan maxAge)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var stale = await db.AiActivities.Where(x => x.Status == "running" && x.StartedAt < cutoff).Select(x => x.Id).ToListAsync();
        foreach (var id in stale) await FinishAsync(id, false);
    }
}
