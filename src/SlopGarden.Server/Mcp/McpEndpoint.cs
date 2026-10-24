using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Mcp;

/// <summary>
/// MCP server over Streamable HTTP (JSON responses): initialize, tools/list, tools/call, ping.
/// Authenticated with a personal access token. Scopes: read (read tools only), propose (mutations become
/// proposals the user approves in the web app), write (mutations execute directly, within the user's role).
/// </summary>
public static class McpEndpoint
{
    public const string ProtocolVersion = "2025-06-18";

    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp", HandleAsync).RequireAuthorization(Policies.ReadOnly).WithTags("MCP").ExcludeFromDescription();
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed)).ExcludeFromDescription();
        app.MapDelete("/mcp", () => Results.Ok()).ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(HttpContext http, ToolRegistry registry, ToolExecutor executor, CancellationToken ct)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.Json(Error(null, -32700, "Parse error"));
        }

        var actor = http.Actor();
        if (body is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                if (item is JsonObject o && await DispatchAsync(o, actor, registry, executor, http, ct) is { } r) responses.Add(r);
            }
            return responses.Count == 0 ? Results.Accepted() : Results.Json(responses);
        }
        if (body is not JsonObject req) return Results.Json(Error(null, -32600, "Invalid request"));
        var res = await DispatchAsync(req, actor, registry, executor, http, ct);
        return res is null ? Results.Accepted() : Results.Json(res);
    }

    private static async Task<JsonObject?> DispatchAsync(JsonObject req, Actor actor, ToolRegistry registry, ToolExecutor executor, HttpContext http, CancellationToken ct)
    {
        var id = req["id"]?.DeepClone();
        var method = req["method"]?.ToString();
        if (id is null) return null;

        switch (method)
        {
            case "initialize":
                http.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = req["params"]?["protocolVersion"]?.ToString() ?? ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "slopgarden", ["title"] = "SlopGarden", ["version"] = typeof(McpEndpoint).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" },
                    ["instructions"] = "Operate a SlopGarden automated garden. Start with get_garden_overview. Mutating tools need a `reason`; with a propose-scoped token they create approval prompts in the SlopGarden web app.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = ListTools(registry, actor) });
            case "tools/call":
            {
                var name = req["params"]?["name"]?.ToString() ?? "";
                var args = req["params"]?["arguments"] as JsonObject;
                var def = registry.Find(name);
                if (def is null) return Error(id, -32602, $"Unknown tool: {name}");
                if (def.Effect == ToolEffect.Mutate && !actor.Scopes.Contains(Data.PatScopes.Write) && !actor.Scopes.Contains(Data.PatScopes.Propose))
                    return Result(id, ToolResult($"This token only has the read scope; '{name}' changes data.", true));
                var mode = actor.Type == Data.ActorTypes.Pat && !actor.Scopes.Contains(Data.PatScopes.Write) ? ToolMode.Propose : ToolMode.Direct;
                var outcome = await executor.ExecuteAsync(name, args, actor, mode, ct, origin: "mcp");
                var result = ToolResult(outcome.ToModelContent(), outcome.Status == ToolOutcome.Failed);
                if (outcome.Result is not null && outcome.Status == ToolOutcome.Ok)
                    result["structuredContent"] = outcome.Result is JsonObject obj ? obj.DeepClone() : new JsonObject { ["result"] = outcome.Result.DeepClone() };
                foreach (var img in outcome.Images)
                {
                    var comma = img.IndexOf(',');
                    var mime = img[5..img.IndexOf(';')];
                    ((JsonArray)result["content"]!).Add(new JsonObject { ["type"] = "image", ["data"] = img[(comma + 1)..], ["mimeType"] = mime });
                }
                return Result(id, result);
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    public static JsonArray ListTools(ToolRegistry registry, Actor actor)
    {
        var arr = new JsonArray();
        foreach (var t in registry.All.OrderBy(t => t.Name))
        {
            if (!Data.Roles.AtLeast(actor.Role, t.MinRole)) continue;
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["title"] = t.Name.Replace('_', ' '),
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = t.Effect == ToolEffect.Read,
                    ["destructiveHint"] = t.Name.StartsWith("delete_") || t.Name.StartsWith("archive_") || t.Name.StartsWith("remove_") || t.Name.StartsWith("revoke_"),
                },
            });
        }
        return arr;
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
