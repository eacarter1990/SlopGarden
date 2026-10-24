using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Ai;

public enum ToolEffect
{
    Read,
    Mutate,
}

/// <summary>How a tool call reached the server; decides whether mutations run or become proposals.</summary>
public enum ToolMode
{
    /// <summary>A human acting in the web UI (or a PAT with the write scope): execute immediately.</summary>
    Direct,
    /// <summary>The AI agent (built-in chat, scheduled review) or an MCP client with the propose scope: mutations become proposals.</summary>
    Propose,
    /// <summary>Executing an approved proposal on behalf of the approver.</summary>
    Approved,
}

public sealed class ToolContext(IServiceProvider services, SlopDbContext db, Actor actor, ToolMode mode, CancellationToken ct)
{
    public IServiceProvider Services { get; } = services;
    public SlopDbContext Db { get; } = db;
    public Actor Actor { get; } = actor;
    public ToolMode Mode { get; } = mode;
    public CancellationToken Ct { get; } = ct;
    public Guid? ConversationId { get; init; }
    public Guid? ProposalId { get; init; }
    public string? Reason { get; init; }

    /// <summary>The AI job (chat, scheduled_review…) this call belongs to; decides which model's capabilities apply.</summary>
    public string? Job { get; init; }

    /// <summary>The AI activity (dashboard indicator) wrapping this call, if any.</summary>
    public Guid? ActivityId { get; init; }

    /// <summary>Set when the tool passed the activity on (e.g. to a running actuator command) so it is not closed on return.</summary>
    public bool ActivityHandedOff { get; set; }

    /// <summary>The actor recorded on changes: approved proposals are attributed to the AI, approved by the user.</summary>
    public string ChangeActorType => Mode == ToolMode.Approved ? ActorTypes.Ai : Actor.Type;

    /// <summary>Images the tool wants to show the model (vision), as data URLs.</summary>
    public List<string> Images { get; } = [];

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}

/// <param name="AllowStanding">False when "always allow" must not be offered (e.g. nutrient and pH dosing).</param>
public sealed record ProposalPreview(string Title, string TargetType, Guid? TargetId, Guid? GardenId, string Risk, JsonArray Changes, bool AllowStanding = true);

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required ToolEffect Effect { get; init; }
    public required string MinRole { get; init; }
    public required Type InputType { get; init; }
    public required Type OutputType { get; init; }
    public required JsonObject InputSchema { get; init; }
    public required JsonObject OutputSchema { get; init; }
    /// <summary>False for credential/administration tools the assistant must never call (device approval, users…).</summary>
    public bool AiAllowed { get; init; } = true;
    /// <summary>Mutations the AI may perform without approval because they only make things safer (emergency stop).</summary>
    public bool NoApprovalNeeded { get; init; }
    /// <summary>Routine actions that can be covered by a standing approval ("always allow").</summary>
    public bool Routine { get; init; }
    public required Func<ToolContext, object, Task<object?>> Handler { get; init; }
    public Func<ToolContext, object, Task<ProposalPreview>>? Preview { get; init; }

    public string Signature => $"{Name}({string.Join(", ", (InputSchema["properties"] as JsonObject ?? []).Select(p => $"{p.Key}: {TypeName(p.Value)}"))}) -> {OutputType.Name}";

    private static string TypeName(JsonNode? schema)
    {
        var t = schema?["type"];
        return t switch
        {
            JsonArray arr => string.Join("|", arr.Select(x => x?.ToString())),
            null => "any",
            _ => t.ToString(),
        };
    }
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal);
    public static readonly JsonSerializerOptions Json = SlopJson.Create();

    public IReadOnlyCollection<ToolDefinition> All => _tools.Values;

    public ToolDefinition? Find(string name) => _tools.GetValueOrDefault(name);

    public void Read<TIn, TOut>(string name, string category, string description, Func<ToolContext, TIn, Task<TOut>> handler, string minRole = Roles.ReadOnly)
        where TIn : notnull
    {
        Add(new ToolDefinition
        {
            Name = name,
            Category = category,
            Description = description,
            Effect = ToolEffect.Read,
            MinRole = minRole,
            InputType = typeof(TIn),
            OutputType = typeof(TOut),
            InputSchema = SchemaFor(typeof(TIn), false),
            OutputSchema = SchemaFor(typeof(TOut), false),
            Handler = async (ctx, input) => await handler(ctx, (TIn)input),
        });
    }

    public void Mutate<TIn, TOut>(string name, string category, string description,
        Func<ToolContext, TIn, Task<TOut>> handler,
        Func<ToolContext, TIn, Task<ProposalPreview>> preview,
        string minRole = Roles.ReadWrite, bool aiAllowed = true, bool noApprovalNeeded = false, bool routine = false)
        where TIn : notnull
    {
        Add(new ToolDefinition
        {
            Name = name,
            Category = category,
            Description = description,
            Effect = ToolEffect.Mutate,
            MinRole = minRole,
            InputType = typeof(TIn),
            OutputType = typeof(TOut),
            InputSchema = SchemaFor(typeof(TIn), true),
            OutputSchema = SchemaFor(typeof(TOut), false),
            AiAllowed = aiAllowed,
            NoApprovalNeeded = noApprovalNeeded,
            Routine = routine,
            Handler = async (ctx, input) => await handler(ctx, (TIn)input),
            Preview = async (ctx, input) => await preview(ctx, (TIn)input),
        });
    }

    private void Add(ToolDefinition def)
    {
        if (!_tools.TryAdd(def.Name, def)) throw new InvalidOperationException($"Duplicate tool {def.Name}");
    }

    public static object Deserialize(ToolDefinition def, JsonObject? args)
    {
        args ??= new JsonObject();
        try
        {
            return args.Deserialize(def.InputType, Json) ?? throw AppException.Bad("Arguments are required");
        }
        catch (JsonException ex)
        {
            throw AppException.Bad($"Invalid arguments for {def.Name}: {ex.Message}");
        }
    }

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (ctx, node) =>
        {
            if (node is not JsonObject obj) return node;
            var desc = ctx.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), true).OfType<DescriptionAttribute>().FirstOrDefault()?.Description
                ?? ctx.PropertyInfo?.AssociatedParameter?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), true).OfType<DescriptionAttribute>().FirstOrDefault()?.Description
                ?? (ctx.PropertyInfo is null ? ctx.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>()?.Description : null);
            if (desc is not null) obj.Insert(0, "description", desc);
            return obj;
        },
    };

    private static readonly JsonSerializerOptions SchemaJson = new(Json) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    public static JsonObject SchemaFor(Type t, bool addReason)
    {
        var node = SchemaJson.GetJsonSchemaAsNode(t, ExporterOptions);
        var obj = node as JsonObject ?? new JsonObject { ["type"] = "object" };
        if (obj["type"] is JsonValue v && v.ToString() == "object")
        {
            obj["properties"] ??= new JsonObject();
            if (addReason)
            {
                obj["properties"]!["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Why this change is needed. Shown to the user in the approval prompt; required when called by an AI.",
                };
            }
        }
        return obj;
    }
}
