using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopGarden.Server.Ai;

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record AssistantTurn(string Content, IReadOnlyList<ToolCall> ToolCalls, int? PromptTokens, int? CompletionTokens, string? FinishReason);

public sealed record ModelEndpoint(string BaseUrl, string? ApiKey, string Model, double? Temperature, int? MaxOutputTokens);

public sealed class AiProviderException(string message) : Exception(message);

/// <summary>OpenAI-compatible model client with streaming and tool calls: the Responses API for api.openai.com, Chat Completions for everything else (LM Studio, Ollama, vLLM, OpenRouter…).</summary>
public sealed class OpenAiClient(IHttpClientFactory httpFactory, ILogger<OpenAiClient> log)
{
    public static string ChatUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
    }

    public async Task<AssistantTurn> CompleteAsync(ModelEndpoint ep, JsonArray messages, JsonArray? tools, Func<string, Task>? onDelta, CancellationToken ct)
    {
        if (NativeSearchSupport.IsOfficialOpenAi(ep.BaseUrl)) return await CompleteWithResponsesAsync(ep, messages, tools, onDelta, ct);

        var body = new JsonObject
        {
            ["model"] = ep.Model,
            ["messages"] = messages.DeepClone(),
            ["stream"] = onDelta is not null,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (ep.Temperature is { } t) body["temperature"] = t;
        if (ep.MaxOutputTokens is { } m) body["max_tokens"] = m;

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl(ep.BaseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(ep.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);

        var http = httpFactory.CreateClient("ai");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new AiProviderException($"Model endpoint returned HTTP {(int)res.StatusCode}: {Truncate(err, 600)}");
        }

        var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
        if (onDelta is null || !mediaType.Contains("event-stream"))
        {
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject() ?? throw new AiProviderException("Empty response from model");
            var turn = ParseFull(json);
            if (onDelta is not null && turn.Content.Length > 0) await onDelta(turn.Content);
            return turn;
        }
        return await ParseStreamAsync(res, onDelta, ct);
    }

    private async Task<AssistantTurn> CompleteWithResponsesAsync(ModelEndpoint ep, JsonArray messages, JsonArray? tools, Func<string, Task>? onDelta, CancellationToken ct)
    {
        using var req = ResponsesApi.Request(ep, ResponsesApi.Body(ep, messages, tools, onDelta is not null));
        using var res = await httpFactory.CreateClient("ai").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new AiProviderException($"OpenAI returned HTTP {(int)res.StatusCode}: {Truncate(err, 600)}");
        }
        if (onDelta is not null && (res.Content.Headers.ContentType?.MediaType ?? "").Contains("event-stream"))
            return await ResponsesApi.ParseStreamAsync(await res.Content.ReadAsStreamAsync(ct), onDelta, ct);

        var turn = ResponsesApi.ParseFull(JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? throw new AiProviderException("Empty response from OpenAI"));
        if (onDelta is not null && turn.Content.Length > 0) await onDelta(turn.Content);
        return turn;
    }

    private static AssistantTurn ParseFull(JsonObject json)
    {
        var msg = json["choices"]?[0]?["message"];
        var calls = new List<ToolCall>();
        if (msg?["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                var args = c?["function"]?["arguments"];
                calls.Add(new ToolCall(c?["id"]?.ToString() ?? $"call_{Guid.NewGuid():N}", c?["function"]?["name"]?.ToString() ?? "",
                    args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}"));
            }
        }
        return new AssistantTurn(msg?["content"]?.ToString() ?? "", calls,
            json["usage"]?["prompt_tokens"]?.GetValue<int>(), json["usage"]?["completion_tokens"]?.GetValue<int>(), json["choices"]?[0]?["finish_reason"]?.ToString());
    }

    private async Task<AssistantTurn> ParseStreamAsync(HttpResponseMessage res, Func<string, Task> onDelta, CancellationToken ct)
    {
        var content = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        int? promptTokens = null, completionTokens = null;
        string? finish = null;

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                log.LogDebug("Skipping unparsable stream chunk");
                continue;
            }
            if (chunk?["error"] is { } error) throw new AiProviderException(error["message"]?.ToString() ?? error.ToJsonString());
            if (chunk?["usage"] is JsonObject usage)
            {
                promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                completionTokens = usage["completion_tokens"]?.GetValue<int>();
            }
            var choice = chunk?["choices"]?[0];
            if (choice is null) continue;
            finish = choice["finish_reason"]?.ToString() ?? finish;
            var delta = choice["delta"];
            if (delta?["content"] is JsonValue cv && cv.TryGetValue<string>(out var text) && text.Length > 0)
            {
                content.Append(text);
                await onDelta(text);
            }
            if (delta?["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var index = tc?["index"]?.GetValue<int>() ?? calls.Count;
                    if (!calls.TryGetValue(index, out var acc)) acc = ("", "", new StringBuilder());
                    var id = tc?["id"]?.ToString();
                    var name = tc?["function"]?["name"]?.ToString();
                    var args = tc?["function"]?["arguments"];
                    if (args is JsonValue av && av.TryGetValue<string>(out var argText)) acc.Args.Append(argText);
                    else if (args is JsonObject ao) acc.Args.Append(ao.ToJsonString());
                    calls[index] = (string.IsNullOrEmpty(id) ? acc.Id : id, acc.Name.Length > 0 ? acc.Name : name ?? "", acc.Args);
                }
            }
        }

        var toolCalls = calls.Values.Where(c => c.Name.Length > 0)
            .Select(c => new ToolCall(c.Id.Length > 0 ? c.Id : $"call_{Guid.NewGuid():N}", c.Name, c.Args.Length > 0 ? c.Args.ToString() : "{}")).ToList();
        return new AssistantTurn(content.ToString(), toolCalls, promptTokens, completionTokens, finish);
    }

    public async Task<List<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var res = await httpFactory.CreateClient("ai").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new AiProviderException($"HTTP {(int)res.StatusCode}: {Truncate(await res.Content.ReadAsStringAsync(ct), 300)}");
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return (json?["data"] as JsonArray ?? []).Select(m => m?["id"]?.ToString() ?? "").Where(s => s.Length > 0).OrderBy(s => s).ToList();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
