using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SlopGarden.Contracts;

namespace SlopGarden.Server.Tests;

public class MachineSetupTests
{
    [Fact]
    public void Client_install_script_starts_only_the_client()
    {
        var script = MachineSetup.ClientDockerScript("http://192.168.1.20:5080", "Greenhouse Pi", null);
        Assert.Contains("docker run", script);
        Assert.Contains("--network host", script);
        Assert.Contains("http://192.168.1.20:5080", script);
        Assert.Contains("Greenhouse Pi", script);
        Assert.Contains("SLOPGARDEN_CONTAINER=slopgarden-client", script);
        Assert.DoesNotContain("postgres", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slopgarden-server", script);
    }

    [Fact]
    public void Server_compose_has_no_client_and_client_compose_has_no_database()
    {
        var server = MachineSetup.ServerCompose("s3cret", "http://192.168.1.20:5080");
        Assert.Contains("postgres:16", server);
        Assert.Contains("slopgarden-server", server);
        Assert.DoesNotContain("slopgarden-client", server);

        var client = MachineSetup.ClientCompose("http://192.168.1.20:5080", "Greenhouse Pi", null);
        Assert.Contains("slopgarden-client", client);
        Assert.Contains("network_mode: host", client);
        Assert.DoesNotContain("postgres", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slopgarden-server", client);
    }

    [Fact]
    public void Host_key_fingerprint_is_sha256()
    {
        var fp = MachineSetup.FingerprintSha256([1, 2, 3, 4]);
        Assert.StartsWith("SHA256:", fp);
        Assert.DoesNotContain("=", fp);
    }

    [Fact]
    public void A_blank_server_address_leaves_discovery_on()
    {
        var script = MachineSetup.ClientDockerScript(null, "Greenhouse Pi", null);
        Assert.DoesNotContain("ServerUrl", script);
        Assert.Contains("mDNS", script);
        var compose = MachineSetup.ClientCompose("  ", "Greenhouse Pi", null);
        Assert.DoesNotContain("ServerUrl", compose);
        Assert.Contains("network_mode: host", compose);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://files.local")]
    public void Server_url_must_be_http(string url) =>
        Assert.Throws<ArgumentException>(() => MachineSetup.RequireServerUrl(url));
}

public class RemoteSetupApiTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Remote_setup_is_an_admin_action_and_not_an_ai_tool()
    {
        var anon = f.CreateClient();
        var denied = await anon.PostAsJsonAsync("/api/devices/remote-setup/probe", new { host = "127.0.0.1", port = 1, username = "pi", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var admin = await f.LoginAsync();
        var tools = await admin.GetFromJsonAsync<JsonArray>("/api/tools", HttpExtensions.Json);
        Assert.DoesNotContain(tools!, t => (t?["name"]?.ToString() ?? "").Contains("remote", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools!, t => (t?["description"]?.ToString() ?? "").Contains("ssh", StringComparison.OrdinalIgnoreCase));

        var probe = await admin.PostAsJsonAsync("/api/devices/remote-setup/probe", new { host = "127.0.0.1", port = 1, username = "pi", password = "nope" }, HttpExtensions.Json);
        var body = await probe.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, probe.StatusCode);
        Assert.DoesNotContain("nope", body);
    }

    [Fact]
    public async Task Install_without_a_confirmed_check_does_nothing()
    {
        var admin = await f.LoginAsync();
        var res = await admin.PostAsJsonAsync("/api/devices/remote-setup/missing/install", new { fingerprint = "SHA256:abc", name = "Pi" }, HttpExtensions.Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("expired", body, StringComparison.OrdinalIgnoreCase);
    }
}
