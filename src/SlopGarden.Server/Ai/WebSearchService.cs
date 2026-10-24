using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Ai;

public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>Result of the web_search tool. <c>Mode</c> is <c>openai_native</c> or the custom provider id.</summary>
public sealed record WebSearchResponse(
    string Mode,
    string? Model,
    string? Summary,
    IReadOnlyList<WebSearchResult> Results,
    string? FallbackReason);

/// <param name="Mode"><c>auto</c>: OpenAI's built-in search when the model is on OpenAI and supports it, otherwise the custom provider.
/// <c>custom</c>: always the custom provider.</param>
public sealed record WebSearchSettings(string Provider = "none", string? BaseUrl = null, Guid? ApiKeySecretId = null, string? Mode = "auto")
{
    public static readonly string[] Providers = ["none", "searxng", "tavily", "brave"];
    public static readonly string[] Modes = ["auto", "custom"];

    public bool PreferCustom => Mode == "custom";
    public bool HasCustomProvider => Provider is not ("none" or "");
}

public static class SearchModes
{
    public const string OpenAiNative = "openai_native";
    public const string Custom = "custom";
    public const string None = "none";
}

/// <summary>Which backend a search for a given job will use, and why.</summary>
public sealed record SearchPlan(string Mode, ModelEndpoint? Native, WebSearchSettings Custom, string Reason)
{
    public bool CanFallBack => Custom.HasCustomProvider;
}

/// <summary>Detects when OpenAI's own web search tool can be used instead of a custom search provider:
/// any model served by api.openai.com, so new OpenAI models work without code changes.</summary>
public static class NativeSearchSupport
{
    public static bool IsOfficialOpenAi(string? baseUrl) =>
        Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    public static SearchPlan Plan(ModelEndpoint? endpoint, WebSearchSettings settings)
    {
        string? whyNotNative = null;
        if (settings.PreferCustom) whyNotNative = "Custom provider selected in Settings → AI";
        else if (endpoint is null) whyNotNative = "No AI model is configured for this job";
        else if (!IsOfficialOpenAi(endpoint.BaseUrl)) whyNotNative = "The model endpoint is not api.openai.com (bring-your-own model)";
        else if (string.IsNullOrWhiteSpace(endpoint.ApiKey)) whyNotNative = "No OpenAI API key is stored for this provider";

        if (whyNotNative is null)
            return new SearchPlan(SearchModes.OpenAiNative, endpoint, settings, $"OpenAI built-in web search with {endpoint!.Model}");
        return settings.HasCustomProvider
            ? new SearchPlan(SearchModes.Custom, null, settings, whyNotNative)
            : new SearchPlan(SearchModes.None, null, settings, whyNotNative + "; no custom provider is configured");
    }
}

/// <summary>Runs a search according to a <see cref="SearchPlan"/>, falling back from OpenAI to the custom provider on failure.</summary>
public sealed class WebSearchExecutor(IHttpClientFactory http, ILogger<WebSearchExecutor> log)
{
    private static readonly string[] NativeToolTypes = ["web_search", "web_search_preview"];

    public async Task<WebSearchResponse> ExecuteAsync(SearchPlan plan, string? customApiKey, string query, int max, CancellationToken ct)
    {
        if (plan.Mode == SearchModes.OpenAiNative && plan.Native is { } ep)
        {
            try
            {
                return await NativeAsync(ep, query, max, ct);
            }
            catch (Exception ex) when (ex is AiProviderException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "OpenAI web search failed for {Model}", ep.Model);
                if (!plan.CanFallBack) throw AppException.Bad($"OpenAI web search failed: {ex.Message}");
                var fallback = await CustomAsync(plan.Custom, customApiKey, query, max, ct);
                return fallback with { FallbackReason = $"OpenAI web search failed ({Short(ex.Message)}); used {plan.Custom.Provider} instead" };
            }
        }
        if (plan.Mode == SearchModes.None)
            throw AppException.Bad($"Web search is not available: {plan.Reason}. An Admin can choose a provider under Settings → AI.");
        return await CustomAsync(plan.Custom, customApiKey, query, max, ct);
    }

    private async Task<WebSearchResponse> NativeAsync(ModelEndpoint ep, string query, int max, CancellationToken ct)
    {
        var client = http.CreateClient("ai");
        AiProviderException? last = null;
        foreach (var toolType in NativeToolTypes)
        {
            var body = new JsonObject
            {
                ["model"] = ep.Model,
                ["tools"] = new JsonArray(new JsonObject { ["type"] = toolType }),
                ["instructions"] = "Search the web and answer concisely for a gardener. Cite your sources.",
                ["input"] = query,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, ep.BaseUrl.TrimEnd('/') + "/responses")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return ParseResponses(JsonNode.Parse(text)!, ep.Model, max);

            last = new AiProviderException($"HTTP {(int)res.StatusCode}: {Short(text)}");
            var unsupportedTool = (int)res.StatusCode == 400 && text.Contains(toolType, StringComparison.OrdinalIgnoreCase);
            if (!unsupportedTool) break;
        }
        throw last ?? new AiProviderException("OpenAI web search returned no response");
    }

    /// <summary>Extracts the answer text and its url_citation annotations from a Responses API result.</summary>
    public static WebSearchResponse ParseResponses(JsonNode response, string model, int max)
    {
        var summary = new StringBuilder();
        var results = new List<WebSearchResult>();
        foreach (var item in response["output"] as JsonArray ?? [])
        {
            if (item?["type"]?.ToString() != "message") continue;
            foreach (var part in item["content"] as JsonArray ?? [])
            {
                if (part?["type"]?.ToString() != "output_text") continue;
                var text = part["text"]?.ToString() ?? "";
                summary.Append(text);
                foreach (var a in part["annotations"] as JsonArray ?? [])
                {
                    if (a?["type"]?.ToString() != "url_citation") continue;
                    var url = a["url"]?.ToString();
                    if (string.IsNullOrEmpty(url) || results.Any(r => r.Url == url)) continue;
                    results.Add(new WebSearchResult(a["title"]?.ToString() ?? url, url, Snippet(text, a["start_index"]?.GetValue<int>(), a["end_index"]?.GetValue<int>())));
                }
            }
        }
        if (summary.Length == 0 && results.Count == 0) throw new AiProviderException("OpenAI web search returned no text");
        return new WebSearchResponse(SearchModes.OpenAiNative, model, summary.ToString(), results.Take(max).ToList(), null);
    }

    /// <summary>The sentence the citation is attached to (citations follow the sentence they support).</summary>
    public static string Snippet(string text, int? start, int? end)
    {
        if (start is not { } s || end is not { } e || s < 0 || e > text.Length || s >= e) return "";
        var before = text[..s].TrimEnd(' ', '\n', '(', '*', '_');
        var cut = before.Length > 1 ? before.LastIndexOfAny(['.', '!', '?', '\n'], before.Length - 2) : -1;
        return before[(cut + 1)..].Replace("**", "").Trim();
    }

    private async Task<WebSearchResponse> CustomAsync(WebSearchSettings cfg, string? apiKey, string query, int max, CancellationToken ct)
    {
        var client = http.CreateClient("search");
        List<WebSearchResult> results;
        switch (cfg.Provider)
        {
            case "searxng":
            {
                var baseUrl = (cfg.BaseUrl ?? throw AppException.Bad("SearXNG base URL is not configured")).TrimEnd('/');
                var json = await client.GetFromJsonAsync<JsonObject>($"{baseUrl}/search?format=json&q={Uri.EscapeDataString(query)}", ct);
                results = Map(json?["results"], "title", "url", "content", max);
                break;
            }
            case "tavily":
            {
                var key = apiKey ?? throw AppException.Bad("Tavily API key is not configured");
                using var res = await client.PostAsJsonAsync("https://api.tavily.com/search", new { api_key = key, query, max_results = max }, ct);
                res.EnsureSuccessStatusCode();
                results = Map((await res.Content.ReadFromJsonAsync<JsonObject>(ct))?["results"], "title", "url", "content", max);
                break;
            }
            case "brave":
            {
                var key = apiKey ?? throw AppException.Bad("Brave Search API key is not configured");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?count={max}&q={Uri.EscapeDataString(query)}");
                req.Headers.Add("X-Subscription-Token", key);
                using var res = await client.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                results = Map((await res.Content.ReadFromJsonAsync<JsonObject>(ct))?["web"]?["results"], "title", "url", "description", max);
                break;
            }
            default:
                throw AppException.Bad("No custom web search provider is configured. An Admin can choose one under Settings → AI.");
        }
        return new WebSearchResponse(cfg.Provider, null, null, results, null);
    }

    private static List<WebSearchResult> Map(JsonNode? arr, string title, string url, string snippet, int max) =>
        (arr as JsonArray ?? []).Take(max)
            .Select(x => new WebSearchResult(x?[title]?.ToString() ?? "", x?[url]?.ToString() ?? "", x?[snippet]?.ToString() ?? "")).ToList();

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

public sealed record JobSearchStatus(string Job, string Mode, string? Model, string Reason, bool FallbackAvailable);

/// <summary>The web_search tool: picks OpenAI's built-in search or the custom provider per AI job.</summary>
public sealed class WebSearchService(SettingsService settings, SecretStore secrets, IServiceProvider services, WebSearchExecutor executor)
{
    public const string SettingsKey = "web_search";

    public async Task<WebSearchSettings> SettingsAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync<WebSearchSettings>(SettingsKey, ct) ?? new WebSearchSettings();
        return s.Mode is null || !WebSearchSettings.Modes.Contains(s.Mode) ? s with { Mode = "auto" } : s;
    }

    public async Task<SearchPlan> PlanAsync(string job, CancellationToken ct) =>
        NativeSearchSupport.Plan(await AgentService.ResolveEndpointAsync(services, job, ct), await SettingsAsync(ct));

    public async Task<WebSearchResponse> SearchAsync(string query, int max, string? job, CancellationToken ct)
    {
        var plan = await PlanAsync(job ?? AiJobs.Chat, ct);
        return await executor.ExecuteAsync(plan, await secrets.GetAsync(plan.Custom.ApiKeySecretId, ct), query, max, ct);
    }

    public async Task<List<JobSearchStatus>> StatusAsync(CancellationToken ct)
    {
        var list = new List<JobSearchStatus>();
        foreach (var job in AiJobs.All)
        {
            var plan = await PlanAsync(job, ct);
            list.Add(new JobSearchStatus(job, plan.Mode, plan.Native?.Model, plan.Reason, plan.CanFallBack));
        }
        return list;
    }
}

public sealed class SettingsService(SlopDbContext db)
{
    private static readonly JsonSerializerOptions Json = Contracts.SlopJson.Create();

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        var s = await db.ServerSettings.FindAsync([key], ct);
        return s?.Value is null ? default : s.Value.Deserialize<T>(Json);
    }

    public async Task SetAsync<T>(string key, T value, Guid? by, CancellationToken ct)
    {
        var s = await db.ServerSettings.FindAsync([key], ct);
        if (s is null)
        {
            s = new ServerSetting { Key = key };
            db.ServerSettings.Add(s);
        }
        s.Value = JsonSerializer.SerializeToNode(value, Json);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
    }
}
