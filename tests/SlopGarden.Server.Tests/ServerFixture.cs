using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SlopGarden.Contracts;
using SlopGarden.DevLlm;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Tests;

/// <summary>
/// Runs the real server against a throwaway PostgreSQL database (SLOPGARDEN_TEST_PG overrides the admin connection)
/// plus an in-process scripted OpenAI-compatible model.
/// </summary>
public sealed class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly string AdminConnection =
        Environment.GetEnvironmentVariable("SLOPGARDEN_TEST_PG") ?? "Host=localhost;Username=slopgarden;Password=slopgarden;Database=postgres";

    public string DatabaseName { get; } = $"slopgarden_test_{Guid.NewGuid():N}"[..32];
    public string LlmUrl { get; private set; } = "";
    private WebApplication? _llm;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"sg-test-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(AdminConnection))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _llm = b.Build();
        _llm.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            var req = await JsonNode.ParseAsync(http.Request.Body) as JsonObject;
            var reply = ScriptedModel.Respond(req?["messages"] as JsonArray ?? []);
            var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
            if (reply.ToolCalls.Count > 0) message["tool_calls"] = ScriptedModel.ToolCallsJson(reply.ToolCalls, false);
            return Results.Text(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString(), "application/json");
        });
        await _llm.StartAsync();
        LlmUrl = _llm.Urls.First() + "/v1";
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var csb = new NpgsqlConnectionStringBuilder(AdminConnection);
        builder.UseSetting("SlopGarden:Database:Host", csb.Host);
        builder.UseSetting("SlopGarden:Database:Port", csb.Port.ToString());
        builder.UseSetting("SlopGarden:Database:Username", csb.Username);
        builder.UseSetting("SlopGarden:Database:Password", csb.Password);
        builder.UseSetting("SlopGarden:Database:Database", DatabaseName);
        builder.UseSetting("SlopGarden:DataDirectory", _dataDir);
        builder.UseSetting("SlopGarden:DiscoveryEnabled", "false");
        builder.UseSetting("SlopGarden:SeedDemo", "true");
        builder.UseSetting("SlopGarden:InitialAdminPassword", "admin-password");
        builder.UseSetting("SlopGarden:DemoPassword", "demo-password");
        builder.UseSetting("SlopGarden:DemoAiBaseUrl", LlmUrl);
        builder.UseSetting("SlopGarden:PublicUrl", "http://localhost");
        builder.UseSetting("SkipWebBuild", "true");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_llm is not null) await _llm.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    public async Task<HttpClient> LoginAsync(string username = "admin", string password = "admin-password")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    public T WithDb<T>(Func<SlopDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<SlopDbContext>());
    }

    public HubConnection DeviceHub(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/device"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => SlopJson.Configure(o.PayloadSerializerOptions))
            .Build();
}

public static class HttpExtensions
{
    public static readonly JsonSerializerOptions Json = SlopJson.Create();

    public static async Task<JsonNode> Op(this HttpClient c, string tool, object? args = null, bool expectSuccess = true)
    {
        var res = await c.PostAsJsonAsync($"/api/ops/{tool}", args ?? new { }, Json);
        var body = await res.Content.ReadAsStringAsync();
        if (expectSuccess && !res.IsSuccessStatusCode) throw new InvalidOperationException($"{tool} failed {(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body.Length == 0 ? "null" : body) ?? JsonValue.Create(0)!;
    }

    public static async Task<T> Wait<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condition not met in time");
    }
}
