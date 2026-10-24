using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopGarden.Server.Ai;

/// <summary>
/// OpenAI Responses API (<c>/v1/responses</c>). Used for api.openai.com because newer OpenAI models only allow
/// function tools together with reasoning there. The agent keeps building Chat Completions–style messages;
/// they are translated here.
/// </summary>
public static class ResponsesApi
{
    public static JsonArray ToInput(JsonArray messages)
    {
        var input = new JsonArray();
        foreach (var m in messages)
        {
            var role = m?["role"]?.ToString();
            switch (role)
            {
                case "system":
                    input.Add(new JsonObject { ["role"] = "developer", ["content"] = Text(m!["content"]) });
                    break;
                case "user":
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = UserContent(m!["content"]) });
                    break;
                case "assistant":
                    var text = Text(m!["content"]);
                    if (text.Length > 0) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                    foreach (var call in m["tool_calls"] as JsonArray ?? [])
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call?["id"]?.ToString(),
                            ["name"] = call?["function"]?["name"]?.ToString(),
                            ["arguments"] = call?["function"]?["arguments"]?.ToString() ?? "{}",
                        });
                    }
                    break;
                case "tool":
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = m!["tool_call_id"]?.ToString(),
                        ["output"] = Text(m["content"]),
                    });
                    break;
            }
        }
        return input;
    }

    private static JsonNode UserContent(JsonNode? content)
    {
        if (content is not JsonArray parts) return Text(content);
        var converted = new JsonArray();
        foreach (var p in parts)
        {
            switch (p?["type"]?.ToString())
            {
                case "text":
                    converted.Add(new JsonObject { ["type"] = "input_text", ["text"] = p["text"]?.ToString() ?? "" });
                    break;
                case "image_url":
                    converted.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = p["image_url"]?["url"]?.ToString() ?? p["image_url"]?.ToString() });
                    break;
            }
        }
        return converted;
    }

    private static string Text(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray parts => string.Join("\n", parts.Select(p => p?["text"]?.ToString()).Where(t => t is not null)),
        _ => "",
    };

    /// <summary>Chat Completions tools (<c>{type, function:{…}}</c>) → Responses tools (flat).</summary>
    public static JsonArray ToTools(JsonArray tools) => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["name"] = t?["function"]?["name"]?.ToString(),
        ["description"] = t?["function"]?["description"]?.ToString(),
        ["parameters"] = t?["function"]?["parameters"]?.DeepClone(),
        ["strict"] = false,
    }).ToArray());

    public static JsonObject Body(ModelEndpoint ep, JsonArray messages, JsonArray? tools, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = ep.Model,
            ["input"] = ToInput(messages),
            ["stream"] = stream,
            ["store"] = false,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = ToTools(tools);
            body["tool_choice"] = "auto";
        }
        if (ep.Temperature is { } t) body["temperature"] = t;
        if (ep.MaxOutputTokens is { } m) body["max_output_tokens"] = m;
        return body;
    }

    public static AssistantTurn ParseFull(JsonNode response)
    {
        if (response["error"] is JsonObject err && err.Count > 0) throw new AiProviderException(err["message"]?.ToString() ?? err.ToJsonString());
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var item in response["output"] as JsonArray ?? [])
        {
            switch (item?["type"]?.ToString())
            {
                case "message":
                    foreach (var part in item["content"] as JsonArray ?? [])
                        if (part?["type"]?.ToString() == "output_text") text.Append(part["text"]?.ToString());
                    break;
                case "function_call":
                    calls.Add(new ToolCall(item["call_id"]?.ToString() ?? $"call_{Guid.NewGuid():N}", item["name"]?.ToString() ?? "", item["arguments"]?.ToString() ?? "{}"));
                    break;
            }
        }
        return new AssistantTurn(text.ToString(), calls, response["usage"]?["input_tokens"]?.GetValue<int>(), response["usage"]?["output_tokens"]?.GetValue<int>(), response["status"]?.ToString());
    }

    /// <summary>Parses the Responses SSE stream: text deltas are forwarded, function calls assembled per output item.</summary>
    public static async Task<AssistantTurn> ParseStreamAsync(Stream stream, Func<string, Task> onDelta, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string CallId, string Name, StringBuilder Args)>();
        int? inputTokens = null, outputTokens = null;
        string? status = null;

        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonNode? evt;
            try
            {
                evt = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }
            var index = evt?["output_index"]?.GetValue<int>() ?? 0;
            switch (evt?["type"]?.ToString())
            {
                case "response.output_text.delta":
                    var delta = evt["delta"]?.ToString() ?? "";
                    if (delta.Length > 0)
                    {
                        text.Append(delta);
                        await onDelta(delta);
                    }
                    break;
                case "response.output_item.added" when (evt["item"]?["type"]?.ToString() == "function_call"):
                    calls[index] = (evt["item"]!["call_id"]?.ToString() ?? "", evt["item"]!["name"]?.ToString() ?? "", new StringBuilder());
                    break;
                case "response.function_call_arguments.delta":
                    if (calls.TryGetValue(index, out var acc)) acc.Args.Append(evt["delta"]?.ToString());
                    break;
                case "response.output_item.done" when (evt["item"]?["type"]?.ToString() == "function_call"):
                    var item = evt["item"]!;
                    calls[index] = (item["call_id"]?.ToString() ?? "", item["name"]?.ToString() ?? "", new StringBuilder(item["arguments"]?.ToString() ?? "{}"));
                    break;
                case "response.completed":
                case "response.incomplete":
                    status = evt["response"]?["status"]?.ToString();
                    inputTokens = evt["response"]?["usage"]?["input_tokens"]?.GetValue<int>();
                    outputTokens = evt["response"]?["usage"]?["output_tokens"]?.GetValue<int>();
                    break;
                case "response.failed":
                    throw new AiProviderException(evt["response"]?["error"]?["message"]?.ToString() ?? "The model response failed");
                case "error":
                    throw new AiProviderException(evt["message"]?.ToString() ?? evt["error"]?["message"]?.ToString() ?? data);
            }
        }

        var toolCalls = calls.Values.Where(c => c.Name.Length > 0)
            .Select(c => new ToolCall(c.CallId.Length > 0 ? c.CallId : $"call_{Guid.NewGuid():N}", c.Name, c.Args.Length > 0 ? c.Args.ToString() : "{}")).ToList();
        return new AssistantTurn(text.ToString(), toolCalls, inputTokens, outputTokens, status);
    }

    public static HttpRequestMessage Request(ModelEndpoint ep, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, ep.BaseUrl.Trim().TrimEnd('/') + "/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(ep.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        return req;
    }
}
