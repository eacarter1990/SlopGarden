using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Tests;

public class NativeSearchDetectionTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://API.OPENAI.COM/v1/", true)]
    [InlineData(" https://api.openai.com/v1 ", true)]
    [InlineData("http://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com.evil.example/v1", false)]
    [InlineData("https://proxy.example/api.openai.com/v1", false)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    [InlineData("https://myco.openai.azure.com/openai", false)]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_the_official_openai_endpoint_counts(string? url, bool expected) =>
        Assert.Equal(expected, NativeSearchSupport.IsOfficialOpenAi(url));

    [Theory]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-4.1-mini")]
    [InlineData("gpt-4.1-nano")]
    [InlineData("o3")]
    [InlineData("some-future-model")]
    public void Any_model_on_openai_uses_native_search(string model) =>
        Assert.Equal(SearchModes.OpenAiNative, NativeSearchSupport.Plan(OpenAi(model), new WebSearchSettings()).Mode);

    private static ModelEndpoint OpenAi(string model = "gpt-4.1-mini", string? key = "sk-test") => new("https://api.openai.com/v1", key, model, null, null);
    private static readonly WebSearchSettings Searx = new("searxng", "http://searx.local", null, "auto");

    [Fact]
    public void OpenAi_with_supported_model_uses_native_search()
    {
        var plan = NativeSearchSupport.Plan(OpenAi(), new WebSearchSettings());
        Assert.Equal(SearchModes.OpenAiNative, plan.Mode);
        Assert.False(plan.CanFallBack);
        Assert.True(NativeSearchSupport.Plan(OpenAi(), Searx).CanFallBack);
    }

    [Fact]
    public void Byom_endpoints_use_the_custom_provider_or_nothing()
    {
        var lmStudio = new ModelEndpoint("http://localhost:1234/v1", null, "qwen2.5-7b", null, null);
        Assert.Equal(SearchModes.Custom, NativeSearchSupport.Plan(lmStudio, Searx).Mode);
        var none = NativeSearchSupport.Plan(lmStudio, new WebSearchSettings());
        Assert.Equal(SearchModes.None, none.Mode);
        Assert.Contains("bring-your-own", none.Reason);
        Assert.Equal(SearchModes.None, NativeSearchSupport.Plan(null, new WebSearchSettings()).Mode);
    }

    [Fact]
    public void Missing_key_and_override_fall_back_to_custom()
    {
        Assert.Contains("API key", NativeSearchSupport.Plan(OpenAi(key: null), Searx).Reason);
        Assert.Equal(SearchModes.Custom, NativeSearchSupport.Plan(OpenAi(key: null), Searx).Mode);
        var overridden = NativeSearchSupport.Plan(OpenAi("gpt-6-luna"), Searx with { Mode = "custom" });
        Assert.Equal(SearchModes.Custom, overridden.Mode);
        Assert.Contains("Custom provider selected", overridden.Reason);
    }
}

public class WebSearchExecutorTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string NativeOk = """
        {"output":[{"type":"web_search_call","status":"completed"},{"type":"message","content":[{"type":"output_text",
         "text":"Plant garlic in autumn. Cloves need a cold period.",
         "annotations":[{"type":"url_citation","url":"https://garden.example/garlic","title":"Growing garlic","start_index":22,"end_index":50},
                        {"type":"url_citation","url":"https://garden.example/garlic","title":"dup","start_index":0,"end_index":5}]}]}]}
        """;

    private const string SearxOk = """{"results":[{"title":"SearX hit","url":"https://searx.example/1","content":"from searx"}]}""";

    private static SearchPlan NativePlan(string provider = "none", string model = "gpt-4.1-mini") =>
        new(SearchModes.OpenAiNative, new ModelEndpoint("https://api.openai.com/v1", "sk-test", model, null, null),
            new WebSearchSettings(provider, provider == "searxng" ? "http://searx.local" : null), "test");

    private static WebSearchExecutor Executor(FakeHandler h) => new(new Factory(h), NullLogger<WebSearchExecutor>.Instance);

    [Fact]
    public async Task Native_search_calls_the_responses_api_and_returns_cited_results()
    {
        var h = new FakeHandler((_, _) => Json(HttpStatusCode.OK, NativeOk));
        var r = await Executor(h).ExecuteAsync(NativePlan(), null, "when to plant garlic", 5, CancellationToken.None);

        Assert.Equal(SearchModes.OpenAiNative, r.Mode);
        Assert.Equal("gpt-4.1-mini", r.Model);
        Assert.StartsWith("Plant garlic", r.Summary);
        var hit = Assert.Single(r.Results);
        Assert.Equal("https://garden.example/garlic", hit.Url);
        Assert.Null(r.FallbackReason);

        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://api.openai.com/v1/responses", req.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("gpt-4.1-mini", json["model"]!.ToString());
        Assert.Equal("web_search", json["tools"]![0]!["type"]!.ToString());
        Assert.Equal("when to plant garlic", json["input"]!.ToString());
    }

    [Fact]
    public void Snippet_is_the_cited_sentence_without_markdown()
    {
        const string text = "Plant garlic mid-October through November. **Now is a good time to plant.** ([groei.nl](https://groei.nl/x))\n\nMore.";
        var start = text.IndexOf("([groei", StringComparison.Ordinal);
        Assert.Equal("Now is a good time to plant.", WebSearchExecutor.Snippet(text, start, start + 30));
        Assert.Equal("Plant garlic in autumn", WebSearchExecutor.Snippet("Plant garlic in autumn. Cloves need a cold period.", 22, 50));
    }

    [Fact]
    public async Task Retries_with_the_preview_tool_name_when_web_search_is_unknown()
    {
        var h = new FakeHandler((_, body) => body.Contains("\"web_search_preview\"")
            ? Json(HttpStatusCode.OK, NativeOk)
            : Json(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid value: 'web_search'. Supported values are: 'web_search_preview'."}}"""));
        var r = await Executor(h).ExecuteAsync(NativePlan(), null, "q", 5, CancellationToken.None);
        Assert.Equal(SearchModes.OpenAiNative, r.Mode);
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task Falls_back_to_the_custom_provider_when_openai_fails()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.Host == "api.openai.com" ? Json(HttpStatusCode.InternalServerError, "{}") : Json(HttpStatusCode.OK, SearxOk));
        var r = await Executor(h).ExecuteAsync(NativePlan("searxng"), null, "q", 5, CancellationToken.None);
        Assert.Equal("searxng", r.Mode);
        Assert.Equal("SearX hit", Assert.Single(r.Results).Title);
        Assert.Contains("OpenAI web search failed", r.FallbackReason);
        Assert.Contains(h.Calls, c => c.Request.RequestUri!.ToString().StartsWith("http://searx.local/search"));
    }

    [Fact]
    public async Task A_model_error_from_openai_falls_back_for_that_call_but_native_stays_planned()
    {
        const string model = "gpt-6-luna";
        var h = new FakeHandler((req, _) => req.RequestUri!.Host == "api.openai.com"
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Hosted tools are not supported with this model."}}""")
            : Json(HttpStatusCode.OK, SearxOk));
        var r = await Executor(h).ExecuteAsync(NativePlan("searxng", model), null, "q", 5, CancellationToken.None);
        Assert.Equal("searxng", r.Mode);
        Assert.Contains("OpenAI web search failed", r.FallbackReason);
        var next = NativeSearchSupport.Plan(new ModelEndpoint("https://api.openai.com/v1", "sk", model, null, null), new WebSearchSettings("searxng", "http://searx.local"));
        Assert.Equal(SearchModes.OpenAiNative, next.Mode);
    }

    [Fact]
    public async Task Without_a_fallback_the_openai_error_is_reported()
    {
        var h = new FakeHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key"}}"""));
        var ex = await Assert.ThrowsAsync<AppException>(() => Executor(h).ExecuteAsync(NativePlan(), null, "q", 5, CancellationToken.None));
        Assert.Contains("OpenAI web search failed", ex.Message);
    }

    [Fact]
    public async Task No_search_mode_explains_how_to_enable_it()
    {
        var plan = NativeSearchSupport.Plan(new ModelEndpoint("http://localhost:11434/v1", null, "llama3.1", null, null), new WebSearchSettings());
        var ex = await Assert.ThrowsAsync<AppException>(() => Executor(new FakeHandler((_, _) => Json(HttpStatusCode.OK, "{}"))).ExecuteAsync(plan, null, "q", 5, CancellationToken.None));
        Assert.Contains("Settings → AI", ex.Message);
    }
}

public class WebSearchSettingsTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    private static async Task<JsonNode> Status(HttpClient admin, string job = "chat") =>
        (await admin.GetFromJsonAsync<JsonObject>("/api/settings/ai"))!["webSearch"]!["status"]!.AsArray().First(s => s!["job"]!.ToString() == job)!;

    [Fact]
    public async Task Settings_show_the_active_search_mode_per_job()
    {
        var admin = await f.LoginAsync();

        var byom = await Status(admin);
        Assert.Equal("none", byom["mode"]!.ToString());
        Assert.Contains("bring-your-own", byom["reason"]!.ToString());

        (await admin.PutAsJsonAsync("/api/settings/ai/web-search", new { provider = "searxng", baseUrl = "http://searx.local" })).EnsureSuccessStatusCode();
        Assert.Equal("custom", (await Status(admin))["mode"]!.ToString());

        var created = await (await admin.PostAsJsonAsync("/api/settings/ai/providers", new { name = "OpenAI", baseUrl = "https://api.openai.com/v1", apiKey = "sk-test", defaultModel = "gpt-4.1-mini" })).Content.ReadFromJsonAsync<JsonObject>();
        var providerId = created!["id"]!.ToString();
        (await admin.PutAsJsonAsync("/api/settings/ai/assignments/chat", new { providerId, model = "gpt-4.1-mini" })).EnsureSuccessStatusCode();

        var native = await Status(admin);
        Assert.Equal("openai_native", native["mode"]!.ToString());
        Assert.Equal("gpt-4.1-mini", native["model"]!.ToString());
        Assert.True(native["fallbackAvailable"]!.GetValue<bool>());
        Assert.Equal("custom", (await Status(admin, "reports"))["mode"]!.ToString());

        (await admin.PutAsJsonAsync("/api/settings/ai/assignments/chat", new { providerId, model = "gpt-6-luna" })).EnsureSuccessStatusCode();
        Assert.Equal("openai_native", (await Status(admin))["mode"]!.ToString());

        (await admin.PutAsJsonAsync("/api/settings/ai/assignments/chat", new { providerId, model = "gpt-4.1-mini" })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/settings/ai/web-search", new { provider = "searxng", baseUrl = "http://searx.local", mode = "custom" })).EnsureSuccessStatusCode();
        var overridden = await Status(admin);
        Assert.Equal("custom", overridden["mode"]!.ToString());
        Assert.Contains("Custom provider selected", overridden["reason"]!.ToString());

        var settings = (await admin.GetFromJsonAsync<JsonObject>("/api/settings/ai"))!;
        Assert.Equal("custom", settings["webSearch"]!["mode"]!.ToString());
        Assert.DoesNotContain("sk-test", settings.ToJsonString());
    }

    [Fact]
    public async Task Web_search_tool_documents_the_search_response_type()
    {
        var admin = await f.LoginAsync();
        var schema = (await admin.GetFromJsonAsync<JsonArray>("/api/tools"))!.First(t => t!["name"]!.ToString() == "web_search")!;
        Assert.Equal("WebSearchResponse", schema["outputType"]!.ToString());
    }
}
