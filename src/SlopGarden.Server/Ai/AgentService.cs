using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai;

public sealed record PageContext(string? Route, string? EntityType, Guid? EntityId, string? Label);

/// <summary>Runs the assistant: builds context, calls the model, executes read tools, turns mutations into proposals, streams to the browser.</summary>
public sealed class AgentService(IServiceScopeFactory scopes, LiveNotifier live, ILogger<AgentService> log)
{
    public const int MaxToolRounds = 8;

    public async Task<ChatMessage> PostUserMessageAsync(Guid conversationId, Actor actor, string content, PageContext? page, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlopDbContext>();
        var conv = await db.ChatConversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct) ?? throw AppException.NotFound("Conversation");
        if (conv.UserId != actor.UserId) throw AppException.Forbidden("Not your conversation");
        var pageJson = page is null ? null : JsonSerializer.SerializeToNode(page, ToolRegistry.Json)!.AsObject();
        var msg = await AppendAsync(db, conv.Id, new ChatMessage { Role = "user", Content = content, PageContext = pageJson }, ct);
        if (conv.Title == "New conversation") conv.Title = content.Length > 60 ? content[..60].TrimEnd() + "…" : content;
        if (pageJson is not null) conv.LastPageContext = pageJson;
        conv.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        _ = Task.Run(() => RunSafeAsync(conv.Id, actor, AiJobs.Chat, page));
        return msg;
    }

    public async Task RunSafeAsync(Guid conversationId, Actor user, string job, PageContext? page)
    {
        try
        {
            await RunAsync(conversationId, user, job, page, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Agent run failed for conversation {Conversation}", conversationId);
        }
    }

    private static async Task<ChatMessage> AppendAsync(SlopDbContext db, Guid conversationId, ChatMessage msg, CancellationToken ct)
    {
        msg.ConversationId = conversationId;
        msg.Seq = (await db.ChatMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (int?)m.Seq, ct) ?? 0) + 1;
        db.ChatMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    private Task Push(Guid userId, object payload) => live.User(userId, LiveEvents.Chat, payload);

    public async Task<string?> RunAsync(Guid conversationId, Actor user, string job, PageContext? page, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SlopDbContext>();
        var client = sp.GetRequiredService<OpenAiClient>();
        var registry = sp.GetRequiredService<ToolRegistry>();
        var executor = sp.GetRequiredService<ToolExecutor>();
        var activities = sp.GetRequiredService<AiActivityTracker>();
        var userId = user.UserId ?? Guid.Empty;

        var endpoint = await ResolveEndpointAsync(sp, job, ct);
        if (endpoint is null)
        {
            var m = await AppendAsync(db, conversationId, new ChatMessage
            {
                Role = "assistant",
                Content = "No AI model is configured yet. An Admin can add an OpenAI-compatible provider and API key under **Settings → AI** (OpenAI, LM Studio, Ollama and others work).",
                Error = "not_configured",
            }, ct);
            await Push(userId, new { type = "message", conversationId, message = m });
            await Push(userId, new { type = "done", conversationId });
            return null;
        }

        var assignment = await db.AiModelAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Job == job, ct)
            ?? await db.AiModelAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Job == AiJobs.Chat, ct);
        var limit = Math.Clamp(assignment?.ContextMessageLimit ?? 30, 4, 200);
        var aiActor = new Actor(ActorTypes.Ai, user.UserId, "AI assistant", user.Role, Data.PatScopes.All);
        var tools = BuildToolSchemas(registry);
        var garden = await db.Gardens.AsNoTracking().Where(g => g.ArchivedAt == null).OrderBy(g => g.CreatedAt).FirstOrDefaultAsync(ct);
        var thinkingId = await activities.StartAsync(garden?.Id, page?.EntityType ?? "garden", page?.EntityId ?? garden?.Id, "thinking", "Assistant is thinking", conversationId);
        string? finalText = null;

        try
        {
            var pendingImages = new List<string>();
            for (var round = 0; round < MaxToolRounds; round++)
            {
                var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = await BuildSystemPromptAsync(sp, page, ct) } };
                foreach (var m in await HistoryAsync(db, conversationId, limit, ct)) messages.Add(m);
                if (pendingImages.Count > 0)
                {
                    var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "[App: camera image(s) you requested are attached below]" } };
                    foreach (var img in pendingImages) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = img } });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    pendingImages.Clear();
                }

                var streamId = Guid.NewGuid();
                var turn = await client.CompleteAsync(endpoint, messages, tools, delta => Push(userId, new { type = "delta", conversationId, streamId, text = delta }), ct);

                var assistant = await AppendAsync(db, conversationId, new ChatMessage
                {
                    Role = "assistant",
                    Content = turn.Content,
                    ToolCalls = turn.ToolCalls.Count == 0 ? null : JsonSerializer.SerializeToNode(turn.ToolCalls.Select(c => new { id = c.Id, name = c.Name, arguments = c.Arguments }), ToolRegistry.Json)!.AsArray(),
                    Model = endpoint.Model,
                    PromptTokens = turn.PromptTokens,
                    CompletionTokens = turn.CompletionTokens,
                }, ct);
                await Push(userId, new { type = "message", conversationId, streamId, message = assistant });

                if (turn.ToolCalls.Count == 0)
                {
                    finalText = turn.Content;
                    break;
                }

                foreach (var call in turn.ToolCalls)
                {
                    JsonObject? args;
                    try
                    {
                        args = string.IsNullOrWhiteSpace(call.Arguments) ? new JsonObject() : JsonNode.Parse(call.Arguments) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        args = null;
                    }
                    var outcome = args is null
                        ? new ToolOutcome(ToolOutcome.Failed, null, null, "Arguments were not valid JSON", [])
                        : await executor.ExecuteAsync(call.Name, args, aiActor, ToolMode.Propose, ct, conversationId, assistant.Id, job: job);
                    pendingImages.AddRange(outcome.Images);
                    var toolMsg = await AppendAsync(db, conversationId, new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = Truncate(outcome.ToModelContent(), 24_000),
                        ProposalId = outcome.ProposalId,
                        Error = outcome.Error,
                    }, ct);
                    await Push(userId, new { type = "message", conversationId, message = toolMsg });
                }
            }
            await activities.FinishAsync(thinkingId, true);
        }
        catch (Exception ex) when (ex is AiProviderException or HttpRequestException or TaskCanceledException)
        {
            await activities.FinishAsync(thinkingId, false);
            var m = await AppendAsync(db, conversationId, new ChatMessage { Role = "assistant", Content = $"⚠️ The model call failed: {ex.Message}", Error = ex.GetType().Name }, CancellationToken.None);
            await Push(userId, new { type = "message", conversationId, message = m });
        }
        finally
        {
            var conv = await db.ChatConversations.FirstOrDefaultAsync(c => c.Id == conversationId, CancellationToken.None);
            if (conv is not null)
            {
                conv.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await Push(userId, new { type = "done", conversationId });
        }
        return finalText;
    }

    public static async Task<ModelEndpoint?> ResolveEndpointAsync(IServiceProvider sp, string job, CancellationToken ct)
    {
        var db = sp.GetRequiredService<SlopDbContext>();
        var secrets = sp.GetRequiredService<SecretStore>();
        var a = await db.AiModelAssignments.AsNoTracking().Include(x => x.Provider).FirstOrDefaultAsync(x => x.Job == job, ct)
            ?? await db.AiModelAssignments.AsNoTracking().Include(x => x.Provider).FirstOrDefaultAsync(x => x.Job == AiJobs.Chat, ct);
        AiProvider? provider = a?.Provider ?? await db.AiProviders.AsNoTracking().OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        if (provider is null) return null;
        var model = !string.IsNullOrWhiteSpace(a?.Model) ? a!.Model : provider.DefaultModel;
        if (string.IsNullOrWhiteSpace(model)) return null;
        return new ModelEndpoint(provider.BaseUrl, await secrets.GetAsync(provider.ApiKeySecretId, ct), model, a?.Temperature, a?.MaxOutputTokens);
    }

    public static JsonArray BuildToolSchemas(ToolRegistry registry)
    {
        var arr = new JsonArray();
        foreach (var t in registry.All.Where(t => t.AiAllowed).OrderBy(t => t.Name))
        {
            var desc = t.Description + (t.Effect == ToolEffect.Mutate && !t.NoApprovalNeeded ? " [Creates an approval prompt; pass `reason`.]" : "");
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = desc,
                    ["parameters"] = t.InputSchema.DeepClone(),
                },
            });
        }
        return arr;
    }

    /// <summary>The last <paramref name="limit"/> messages, trimmed so the window never starts with an orphaned tool result.</summary>
    private static async Task<List<JsonObject>> HistoryAsync(SlopDbContext db, Guid conversationId, int limit, CancellationToken ct)
    {
        var rows = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Seq).Take(limit).ToListAsync(ct);
        rows.Reverse();
        while (rows.Count > 0 && rows[0].Role == "tool") rows.RemoveAt(0);

        var result = new List<JsonObject>();
        foreach (var m in rows)
        {
            switch (m.Role)
            {
                case "user":
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content });
                    break;
                case "assistant":
                {
                    var o = new JsonObject { ["role"] = "assistant", ["content"] = m.Content };
                    if (m.ToolCalls is { Count: > 0 } calls)
                    {
                        o["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c?["id"]?.ToString(),
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c?["name"]?.ToString(), ["arguments"] = c?["arguments"]?.ToString() ?? "{}" },
                        }).ToArray());
                    }
                    if (m.Error is not null && string.IsNullOrEmpty(m.Content)) continue;
                    result.Add(o);
                    break;
                }
                case "tool":
                    result.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
                    break;
                case "event":
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = $"[App event, not typed by the user] {m.Content}" });
                    break;
            }
        }
        return result;
    }

    private static async Task<string> BuildSystemPromptAsync(IServiceProvider sp, PageContext? page, CancellationToken ct)
    {
        var db = sp.GetRequiredService<SlopDbContext>();
        var overview = sp.GetRequiredService<OverviewService>();
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are the SlopGarden assistant: an AI gardener that sets up and operates an automated food garden together with its owner.
            You act only through the provided tools. Read tools run immediately. Tools that change anything create an approval prompt in the chat (what + why); the user approves or rejects it.

            Rules:
            - Always pass a short, concrete `reason` to mutating tools. Never claim a change was made until an approval event says it was applied.
            - Prefer one well-explained proposal over many small ones; don't repeat a pending proposal.
            - Hard safety limits and emergency stops are enforced by the server and the devices. Never try to work around them; you may propose changing a limit and must explain why.
            - trigger_emergency_stop is immediate and needs no approval: use it only for real danger (flooding, overheating, runaway pump).
            - Base decisions on plant type needs (crop profile + care targets), current readings, indoor/outdoor, season, daylight and location, and the user's AI-context notes.
            - Use capture_camera_image when visual inspection would help (only stills; no video).
            - Prefer IDs from the snapshot below; call tools to get anything that's missing or stale.
            - Answer in concise Markdown (short paragraphs, lists, small tables). Use metric units unless asked otherwise.
            """);
        if (page is not null)
            sb.AppendLine($"\nThe user is currently looking at: {page.Label ?? page.Route} (route {page.Route}, {page.EntityType} {page.EntityId}). Default to this context unless they say otherwise.");

        try
        {
            var o = await overview.BuildAsync(null, ct);
            sb.AppendLine("\n## Garden snapshot (live)");
            sb.AppendLine(CompactOverview(o));
            var others = await db.Gardens.AsNoTracking().Where(g => g.ArchivedAt == null && g.Id != o.Garden.Id).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
            if (others.Count > 0) sb.AppendLine("Other gardens: " + string.Join(", ", others.Select(g => $"{g.Name} ({g.Id})")));
        }
        catch (AppException)
        {
            sb.AppendLine("\nNo garden exists yet. Offer to create one (create_garden) and ask about the beds and plants.");
        }
        return sb.ToString();
    }

    public static string CompactOverview(GardenOverview o)
    {
        var sb = new StringBuilder();
        var g = o.Garden;
        var e = o.Environment;
        sb.AppendLine($"Garden '{g.Name}' id={g.Id} env={g.Environment} tz={g.TimezoneId} loc={(g.Latitude is null ? "unknown" : $"{g.Latitude:0.##},{g.Longitude:0.##} {g.LocationLabel}")} autonomy={g.AiAutonomy}");
        sb.AppendLine($"Now: {e.LocalTime} ({e.DayOfWeek}), season={e.Season}, daylight={e.DaylightHours?.ToString("0.#") ?? "?"}h, sunrise={e.Sunrise ?? "?"}, sunset={e.Sunset ?? "?"}");
        if (!string.IsNullOrWhiteSpace(g.AiContext)) sb.AppendLine($"Owner notes: {g.AiContext}");
        static string R(IEnumerable<LatestReading> rs) => string.Join("; ", rs.Select(r => $"{r.Metric}={(r.Value is { } v ? v.ToString("0.#") : "n/a")}{r.Unit} [ch {r.ChannelId}]"));
        if (o.GardenReadings.Count > 0) sb.AppendLine($"Garden-level readings: {R(o.GardenReadings)}");
        foreach (var b in o.Beds)
        {
            sb.AppendLine($"- Bed '{b.Bed.Name}' id={b.Bed.Id} kind={b.Bed.Kind} sun={b.Bed.SunExposure} irrigation={b.Bed.IrrigationType}{(b.Bed.AiContext.Length > 0 ? $" notes=\"{b.Bed.AiContext}\"" : "")}");
            if (b.Readings.Count > 0) sb.AppendLine($"  bed readings: {R(b.Readings)}");
            foreach (var p in b.Plants)
            {
                var targets = string.Join(", ", p.Targets.Select(t => $"{t.Metric} {t.Min:0.#}-{t.Max:0.#} ({t.Source})"));
                sb.AppendLine($"  • Plant '{p.Plant.Name}' id={p.Plant.Id} profile={p.Plant.ProfileName ?? "none"} x{p.Plant.Quantity} stage={p.Plant.Stage}{(p.Plant.PlantedAt is { } d ? $" planted={d:yyyy-MM-dd}" : "")}{(targets.Length > 0 ? $" targets: {targets}" : "")}{(p.Plant.AiContext.Length > 0 ? $" notes=\"{p.Plant.AiContext}\"" : "")}");
                if (p.Readings.Count > 0) sb.AppendLine($"    readings: {R(p.Readings)}");
            }
        }
        if (o.Actuators.Count > 0)
            sb.AppendLine("Actuators: " + string.Join("; ", o.Actuators.Select(a => $"'{a.Name}' id={a.Id} {a.Kind} {(a.IsOn ? "ON" : "off")} → {a.TargetType}{(a.Enabled ? "" : " (disabled)")}")));
        if (o.Cameras.Count > 0)
            sb.AppendLine("Cameras: " + string.Join("; ", o.Cameras.Select(c => $"'{c.Name}' id={c.Id} → {c.TargetType}")));
        sb.AppendLine($"Devices: {o.Devices.Online}/{o.Devices.Total} online, {o.Devices.Pending} pending approval.");
        if (o.ActiveEmergencyStops.Count > 0)
            sb.AppendLine("ACTIVE EMERGENCY STOPS: " + string.Join("; ", o.ActiveEmergencyStops.Select(s => $"{s.ScopeType} {s.ScopeId} ({s.Reason}) id={s.Id}")));
        if (o.PendingProposals > 0) sb.AppendLine($"Pending proposals awaiting the user: {o.PendingProposals}");
        return sb.ToString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…(truncated)";
}
