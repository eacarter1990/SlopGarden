using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Ai;

public sealed record ToolOutcome(string Status, JsonNode? Result, Guid? ProposalId, string? Error, IReadOnlyList<string> Images)
{
    public const string Ok = "ok";
    public const string Proposed = "pending_approval";
    public const string Failed = "error";

    /// <summary>Text handed back to the model as the tool result.</summary>
    public string ToModelContent() => Status switch
    {
        Ok => Result?.ToJsonString() ?? "null",
        Proposed => new JsonObject
        {
            ["status"] = "pending_approval",
            ["proposalId"] = ProposalId?.ToString(),
            ["message"] = "The change was NOT applied yet. The user sees an approval prompt in the chat. Tell them briefly what you proposed and why; do not repeat the same proposal.",
        }.ToJsonString(),
        _ => new JsonObject { ["status"] = "error", ["error"] = Error }.ToJsonString(),
    };
}

public sealed class ToolExecutor(
    ToolRegistry registry,
    IServiceProvider services,
    SlopDbContext db,
    LiveNotifier live,
    AiActivityTracker activities,
    ILogger<ToolExecutor> log)
{
    public async Task<ToolOutcome> ExecuteAsync(string toolName, JsonObject? args, Actor actor, ToolMode mode, CancellationToken ct,
        Guid? conversationId = null, Guid? messageId = null, string origin = "chat", string? job = null)
    {
        var def = registry.Find(toolName);
        if (def is null) return Fail($"Unknown tool '{toolName}'");
        args ??= new JsonObject();
        var reason = args["reason"]?.GetValue<string>();

        try
        {
            if (!Roles.AtLeast(actor.Role, def.MinRole))
                return Fail($"'{toolName}' requires the {def.MinRole} role");
            if (actor.Type == ActorTypes.Ai && !def.AiAllowed)
                return Fail($"'{toolName}' is restricted to humans in the web app");
            if (def.Effect == ToolEffect.Mutate && mode == ToolMode.Direct && actor.Type == ActorTypes.Pat && !actor.Scopes.Contains(PatScopes.Write))
                mode = actor.Scopes.Contains(PatScopes.Propose) ? ToolMode.Propose : throw AppException.Forbidden("Token lacks the write or propose scope");

            var input = ToolRegistry.Deserialize(def, args);

            if (def.Effect == ToolEffect.Mutate && mode == ToolMode.Propose && !def.NoApprovalNeeded)
            {
                var ctx = new ToolContext(services, db, actor, mode, ct) { ConversationId = conversationId, Reason = reason, Job = job };
                var preview = await def.Preview!(ctx, input);
                if (def.Routine && await MatchingStandingApprovalAsync(def, preview, args, ct) is { } standing)
                {
                    log.LogInformation("Standing approval {Id} covers {Tool}", standing.Id, def.Name);
                    return await RunAsync(def, input, actor, ToolMode.Approved, conversationId, null, reason, preview, ct, job);
                }
                if (actor.Type == ActorTypes.Ai && string.IsNullOrWhiteSpace(reason))
                    return Fail("Mutating tools require a 'reason' argument explaining why the change is needed.");
                var proposal = await CreateProposalAsync(def, args, preview, reason ?? "", actor, conversationId, messageId, origin, ct);
                return new ToolOutcome(ToolOutcome.Proposed, JsonSerializer.SerializeToNode(new { proposalId = proposal.Id, proposal.Title }), proposal.Id, null, []);
            }

            ProposalPreview? pv = null;
            if (actor.Type == ActorTypes.Ai && def.Effect == ToolEffect.Mutate && def.Preview is not null)
                pv = await def.Preview(new ToolContext(services, db, actor, mode, ct), input);
            return await RunAsync(def, input, actor, mode, conversationId, null, reason, pv, ct, job);
        }
        catch (AppException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Tool {Tool} failed", toolName);
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<ToolOutcome> RunAsync(ToolDefinition def, object input, Actor actor, ToolMode mode, Guid? conversationId, Guid? proposalId,
        string? reason, ProposalPreview? preview, CancellationToken ct, string? job = null)
    {
        Guid? activityId = null;
        if (preview is not null && (actor.Type == ActorTypes.Ai || mode == ToolMode.Approved))
            activityId = await activities.StartAsync(preview.GardenId, preview.TargetType, preview.TargetId, "tool", preview.Title, conversationId, proposalId);

        var ctx = new ToolContext(services, db, actor, mode, ct) { ConversationId = conversationId, ProposalId = proposalId, Reason = reason, ActivityId = activityId, Job = job };
        try
        {
            var result = await def.Handler(ctx, input);
            var node = JsonSerializer.SerializeToNode(result, ToolRegistry.Json);
            if (activityId is { } id && !ctx.ActivityHandedOff) await activities.FinishAsync(id, true);
            return new ToolOutcome(ToolOutcome.Ok, node, null, null, ctx.Images);
        }
        catch
        {
            if (activityId is { } id) await activities.FinishAsync(id, false);
            throw;
        }
    }

    private async Task<AiStandingApproval?> MatchingStandingApprovalAsync(ToolDefinition def, ProposalPreview preview, JsonObject args, CancellationToken ct)
    {
        var candidates = await db.AiStandingApprovals.AsNoTracking()
            .Where(s => s.RevokedAt == null && s.ToolName == def.Name && s.TargetType == preview.TargetType && (s.TargetId == null || s.TargetId == preview.TargetId))
            .ToListAsync(ct);
        return candidates.FirstOrDefault(s => WithinConstraints(s.Constraints, args));
    }

    /// <summary>Constraints are "max{Arg}" (numeric upper bound) or "{arg}" (exact value) entries.</summary>
    public static bool WithinConstraints(JsonObject constraints, JsonObject args)
    {
        foreach (var (key, value) in constraints)
        {
            if (key.StartsWith("max", StringComparison.Ordinal) && key.Length > 3)
            {
                var arg = char.ToLowerInvariant(key[3]) + key[4..];
                var actual = args[arg];
                if (actual is null) continue;
                if (!double.TryParse(actual.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var a)) return false;
                if (value is null || !double.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var max)) return false;
                if (a > max) return false;
            }
            else if (args[key]?.ToJsonString() != value?.ToJsonString())
            {
                return false;
            }
        }
        return true;
    }

    private async Task<AiProposal> CreateProposalAsync(ToolDefinition def, JsonObject args, ProposalPreview preview, string reason, Actor actor,
        Guid? conversationId, Guid? messageId, string origin, CancellationToken ct)
    {
        var clean = args.DeepClone().AsObject();
        clean.Remove("reason");
        var p = new AiProposal
        {
            ConversationId = conversationId,
            MessageId = messageId,
            Origin = origin,
            RequestedByType = actor.Type,
            RequestedById = actor.UserId,
            ToolName = def.Name,
            Arguments = clean,
            Title = preview.Title,
            Rationale = reason,
            Changes = preview.Changes,
            TargetType = preview.TargetType,
            TargetId = preview.TargetId,
            GardenId = preview.GardenId,
            Risk = preview.Risk,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.AiProposals.Add(p);
        db.Audit(actor, "ai.proposal.created", def.Name, p.Id, new() { ["title"] = p.Title });
        await db.SaveChangesAsync(ct);
        await live.All(LiveEvents.Proposal, new { proposal = p });
        return p;
    }

    public async Task<AiProposal> DecideAsync(Guid proposalId, bool approve, string? note, bool allowAlways, Actor approver, CancellationToken ct)
    {
        var p = await db.AiProposals.FirstOrDefaultAsync(x => x.Id == proposalId, ct) ?? throw AppException.NotFound("Proposal");
        if (p.Status != ProposalStatuses.Pending) throw AppException.Conflict($"Proposal is already {p.Status}");
        if (!approver.CanWrite) throw AppException.Forbidden("ReadWrite or Admin role required to decide on proposals");
        var def = registry.Find(p.ToolName) ?? throw AppException.Bad($"Tool {p.ToolName} no longer exists");
        if (!Roles.AtLeast(approver.Role, def.MinRole)) throw AppException.Forbidden($"Approving this requires the {def.MinRole} role");

        p.DecidedBy = approver.UserId;
        p.DecidedAt = DateTimeOffset.UtcNow;
        p.DecisionNote = note;

        if (!approve)
        {
            p.Status = ProposalStatuses.Rejected;
            db.Audit(approver, "ai.proposal.rejected", p.ToolName, p.Id, new() { ["note"] = note });
            await db.SaveChangesAsync(ct);
            await AppendEventAsync(p, $"**Rejected** by {approver.Label}: {p.Title}" + (string.IsNullOrWhiteSpace(note) ? "" : $"\n\n> {note}"), ct);
            await live.All(LiveEvents.Proposal, new { proposal = p });
            return p;
        }

        p.Status = ProposalStatuses.Approved;
        db.Audit(approver, "ai.proposal.approved", p.ToolName, p.Id, new() { ["note"] = note, ["allowAlways"] = allowAlways });
        await db.SaveChangesAsync(ct);

        var preview = new ProposalPreview(p.Title, p.TargetType, p.TargetId, p.GardenId, p.Risk, p.Changes);
        try
        {
            var input = ToolRegistry.Deserialize(def, p.Arguments);
            var outcome = await RunAsync(def, input, approver, ToolMode.Approved, p.ConversationId, p.Id, p.Rationale, preview, ct);
            p.Status = ProposalStatuses.Executed;
            p.ExecutedAt = DateTimeOffset.UtcNow;
            p.Result = outcome.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            p.Status = ProposalStatuses.Failed;
            p.Result = new JsonObject { ["error"] = ex.Message };
        }

        if (allowAlways && def.Routine && p.Status == ProposalStatuses.Executed)
        {
            var constraints = new JsonObject();
            foreach (var (k, v) in p.Arguments)
            {
                if (v is JsonValue jv && jv.TryGetValue<double>(out var num)) constraints["max" + char.ToUpperInvariant(k[0]) + k[1..]] = num;
                else if (k is "action") constraints[k] = v?.DeepClone();
            }
            db.AiStandingApprovals.Add(new AiStandingApproval
            {
                ToolName = def.Name,
                TargetType = p.TargetType,
                TargetId = p.TargetId,
                Constraints = constraints,
                CreatedFromProposalId = p.Id,
                ApprovedBy = approver.UserId ?? Guid.Empty,
            });
        }

        await db.SaveChangesAsync(ct);
        var resultText = p.Status == ProposalStatuses.Executed
            ? $"**Approved** by {approver.Label} and applied: {p.Title}" + (allowAlways && def.Routine ? "\n\n_The AI may repeat this routine action within the same limits without asking._" : "")
            : $"**Approved** by {approver.Label}, but applying it failed: {p.Result?["error"]}";
        await AppendEventAsync(p, resultText, ct);
        await live.All(LiveEvents.Proposal, new { proposal = p });
        await live.Changed("all");
        return p;
    }

    private async Task AppendEventAsync(AiProposal p, string content, CancellationToken ct)
    {
        if (p.ConversationId is not { } cid) return;
        var seq = (await db.ChatMessages.Where(m => m.ConversationId == cid).MaxAsync(m => (int?)m.Seq, ct) ?? 0) + 1;
        var msg = new ChatMessage { ConversationId = cid, Seq = seq, Role = "event", Content = content, ProposalId = p.Id };
        db.ChatMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        var conv = await db.ChatConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
        if (conv is not null) await live.User(conv.UserId, LiveEvents.Chat, new { type = "message", conversationId = cid, message = msg });
    }

    private static ToolOutcome Fail(string error) => new(ToolOutcome.Failed, null, null, error, []);
}
