using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Ai;

public sealed record WebSearchResult(string Title, string Url, string Snippet);

public sealed record WebSearchSettings(string Provider = "none", string? BaseUrl = null, Guid? ApiKeySecretId = null)
{
    public static readonly string[] Providers = ["none", "searxng", "tavily", "brave"];
}

/// <summary>User-selectable web search provider for the AI's research.</summary>
public sealed class WebSearchService(SettingsService settings, SecretStore secrets, IHttpClientFactory http)
{
    public const string SettingsKey = "web_search";

    public async Task<List<WebSearchResult>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var cfg = await settings.GetAsync<WebSearchSettings>(SettingsKey, ct) ?? new WebSearchSettings();
        var client = http.CreateClient("search");
        client.Timeout = TimeSpan.FromSeconds(15);
        switch (cfg.Provider)
        {
            case "searxng":
            {
                var baseUrl = (cfg.BaseUrl ?? throw AppException.Bad("SearXNG base URL is not configured")).TrimEnd('/');
                var json = await client.GetFromJsonAsync<JsonObject>($"{baseUrl}/search?format=json&q={Uri.EscapeDataString(query)}", ct);
                return (json?["results"] as JsonArray ?? []).Take(max)
                    .Select(x => new WebSearchResult(x?["title"]?.ToString() ?? "", x?["url"]?.ToString() ?? "", x?["content"]?.ToString() ?? "")).ToList();
            }
            case "tavily":
            {
                var key = await secrets.GetAsync(cfg.ApiKeySecretId, ct) ?? throw AppException.Bad("Tavily API key is not configured");
                using var res = await client.PostAsJsonAsync("https://api.tavily.com/search", new { api_key = key, query, max_results = max }, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
                return (json?["results"] as JsonArray ?? []).Take(max)
                    .Select(x => new WebSearchResult(x?["title"]?.ToString() ?? "", x?["url"]?.ToString() ?? "", x?["content"]?.ToString() ?? "")).ToList();
            }
            case "brave":
            {
                var key = await secrets.GetAsync(cfg.ApiKeySecretId, ct) ?? throw AppException.Bad("Brave Search API key is not configured");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?count={max}&q={Uri.EscapeDataString(query)}");
                req.Headers.Add("X-Subscription-Token", key);
                using var res = await client.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
                return (json?["web"]?["results"] as JsonArray ?? []).Take(max)
                    .Select(x => new WebSearchResult(x?["title"]?.ToString() ?? "", x?["url"]?.ToString() ?? "", x?["description"]?.ToString() ?? "")).ToList();
            }
            default:
                throw AppException.Bad("No web search provider is configured. An Admin can choose one under Settings → AI.");
        }
    }
}

public sealed class SettingsService(Data.SlopDbContext db)
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
            s = new Data.ServerSetting { Key = key };
            db.ServerSettings.Add(s);
        }
        s.Value = JsonSerializer.SerializeToNode(value, Json);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
    }
}
