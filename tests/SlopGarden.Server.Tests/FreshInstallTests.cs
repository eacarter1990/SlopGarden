using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Tests;

/// <summary>A fresh server: no demo seed and no initial admin password, matching the Windows MSI.</summary>
public sealed class FreshServerFixture : ServerFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("SlopGarden:SeedDemo", "false");
        builder.UseSetting("SlopGarden:InitialAdminPassword", "");
    }
}

/// <summary>PostgreSQL is not listening yet, which is the boot order of the Windows service.</summary>
public sealed class UnreachableDatabaseFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"sg-nodb-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("SlopGarden:Database:Host", "127.0.0.1");
        builder.UseSetting("SlopGarden:Database:Port", "1");
        builder.UseSetting("SlopGarden:Database:Database", "slopgarden");
        builder.UseSetting("SlopGarden:Database:Username", "slopgarden");
        builder.UseSetting("SlopGarden:Database:Password", "slopgarden");
        builder.UseSetting("SlopGarden:Database:Extra", "Timeout=2");
        builder.UseSetting("SlopGarden:DataDirectory", _dataDir);
        builder.UseSetting("SlopGarden:DiscoveryEnabled", "false");
        builder.UseSetting("SlopGarden:SeedDemo", "false");
        builder.UseSetting("SlopGarden:InitialAdminPassword", "");
        builder.UseSetting("SkipWebBuild", "true");
    }
}

public class FreshInstallTests(FreshServerFixture server, UnreachableDatabaseFixture down) : IClassFixture<FreshServerFixture>, IClassFixture<UnreachableDatabaseFixture>
{
    [Fact]
    public void Connection_refused_tells_the_operator_to_start_postgres()
    {
        var message = DatabaseStartup.Describe(new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Contains("PostgreSQL is not running", message);
        Assert.Contains("slopgarden", message);
    }

    [Fact]
    public async Task Empty_database_opens_setup_and_then_accepts_that_admin()
    {
        var anon = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var status = await GetObject(anon, "/api/auth/status");
        Assert.True(status["databaseReady"]!.GetValue<bool>());
        Assert.True(status["needsSetup"]!.GetValue<bool>());
        Assert.True(IsNull(status["user"]));

        foreach (var (username, password) in new[] { ("admin", "slopgarden"), ("grower", "slopgarden"), ("viewer", "slopgarden") })
        {
            var denied = await anon.PostAsJsonAsync("/api/auth/login", new { username, password });
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            var body = await Read(denied);
            Assert.True(body["needsSetup"]!.GetValue<bool>());
            Assert.DoesNotContain("Invalid username", body["error"]!.GetValue<string>());
        }

        var created = await anon.PostAsJsonAsync("/api/auth/setup", new { username = "eddie", password = "garden-secret", displayName = "Eddie" });
        created.EnsureSuccessStatusCode();
        var admin = await Read(created);
        Assert.Equal("eddie", admin["username"]!.GetValue<string>());
        Assert.Equal("admin", admin["role"]!.GetValue<string>());

        var duplicate = await anon.PostAsJsonAsync("/api/auth/setup", new { username = "other", password = "another-secret" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var after = await GetObject(anon, "/api/auth/status");
        Assert.False(after["needsSetup"]!.GetValue<bool>());
        Assert.Equal("eddie", after["user"]!["username"]!.GetValue<string>());

        var other = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var wrong = await other.PostAsJsonAsync("/api/auth/login", new { username = "eddie", password = "slopgarden" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var signedIn = await other.PostAsJsonAsync("/api/auth/login", new { username = "eddie", password = "garden-secret" });
        signedIn.EnsureSuccessStatusCode();
        Assert.Equal("eddie", (await Read(signedIn))["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task Database_down_keeps_setup_required_instead_of_a_login_failure()
    {
        var client = down.CreateClient();
        var health = await client.GetAsync("/api/health");
        health.EnsureSuccessStatusCode();

        var statusRes = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        var status = await Read(statusRes);
        Assert.False(status["databaseReady"]!.GetValue<bool>());
        Assert.True(status["needsSetup"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(status["error"]!.GetValue<string>()));
        Assert.Contains("PostgreSQL", status["error"]!.GetValue<string>());

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "slopgarden" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        var body = await Read(login);
        Assert.True(body["needsSetup"]!.GetValue<bool>());
        Assert.Contains("PostgreSQL", body["error"]!.GetValue<string>());
        Assert.DoesNotContain("Invalid username", body["error"]!.GetValue<string>());
    }

    private static bool IsNull(JsonNode? node) => node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null;

    private static async Task<JsonObject> GetObject(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        return await Read(res);
    }

    private static async Task<JsonObject> Read(HttpResponseMessage res) =>
        JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
}
