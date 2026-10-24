using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.DevLlm;

// A deterministic, offline, OpenAI-compatible "model" for demos and tests. It picks tools by keyword and
// reasons over tool results with simple rules. It is not an LLM; real deployments point SlopGarden at one.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5199");
var app = builder.Build();

app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = ScriptedModel.ModelId, @object = "model", owned_by = "slopgarden" } } }));

app.MapPost("/v1/chat/completions", async (HttpContext http) =>
{
    var req = await JsonNode.ParseAsync(http.Request.Body) as JsonObject ?? new JsonObject();
    var reply = ScriptedModel.Respond(req["messages"] as JsonArray ?? []);
    var stream = req["stream"]?.GetValue<bool>() == true;
    var id = $"chatcmpl-{Guid.NewGuid():N}";

    if (!stream)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
        if (reply.ToolCalls.Count > 0) message["tool_calls"] = ScriptedModel.ToolCallsJson(reply.ToolCalls, false);
        return Results.Json(new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["model"] = ScriptedModel.ModelId,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.ToolCalls.Count > 0 ? "tool_calls" : "stop" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0 },
        });
    }

    http.Response.ContentType = "text/event-stream";
    async Task Send(JsonObject delta, string? finish = null)
    {
        var chunk = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["model"] = ScriptedModel.ModelId,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };
        await http.Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n");
        await http.Response.Body.FlushAsync();
    }

    await Send(new JsonObject { ["role"] = "assistant" });
    foreach (var piece in ScriptedModel.Chunk(reply.Content))
    {
        await Send(new JsonObject { ["content"] = piece });
        await Task.Delay(18);
    }
    if (reply.ToolCalls.Count > 0) await Send(new JsonObject { ["tool_calls"] = ScriptedModel.ToolCallsJson(reply.ToolCalls, true) });
    await Send(new JsonObject(), reply.ToolCalls.Count > 0 ? "tool_calls" : "stop");
    await http.Response.WriteAsync("data: [DONE]\n\n");
    return Results.Empty;
});

app.Run();
