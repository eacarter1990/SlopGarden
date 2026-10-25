using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using SlopGarden.Contracts;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

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
    public void Windows_services_start_automatically_and_removal_deletes_them()
    {
        var server = MachineServices.Install(ServiceRole.Server, ServicePlatform.Windows,
            @"C:\Program Files\SlopGarden\Server\SlopGarden.Server.exe", null, null, "http://192.168.1.20:5080", "s3cret");
        Assert.Equal("windows-service", server.Manager);
        Assert.Equal("SlopGardenServer", server.Name);
        Assert.Contains(server.Activate, c => string.Join(' ', c.Arguments).Contains("start= auto") && c.Arguments.Contains("SlopGardenServer"));
        Assert.Contains(server.Activate, c => c.Arguments is ["sc.exe", "start", "SlopGardenServer"]);
        Assert.Contains(server.Stop, c => c.Arguments is ["sc.exe", "delete", "SlopGardenServer"]);
        Assert.Contains(server.Prepare, c => c.Arguments.Contains("SlopGardenClient") && c.IgnoreFailure);
        Assert.DoesNotContain(server.Activate.SelectMany(c => c.Arguments), a => a.Contains("s3cret"));
        Assert.Contains("s3cret", server.Files[0].Contents);
        Assert.Contains("http://192.168.1.20:5080", server.Files[0].Contents);

        var client = MachineServices.Install(ServiceRole.Client, ServicePlatform.Windows,
            @"C:\Program Files\SlopGarden\Client\slopgarden-client.exe", "http://192.168.1.20:5080", "Greenhouse Pi", null, "");
        Assert.Equal("SlopGardenClient", client.Name);
        var create = string.Join(' ', client.Activate[0].Arguments);
        Assert.Contains("start= auto", create);
        Assert.Contains("run", create);
        Assert.Contains(client.Stop, c => c.Arguments is ["sc.exe", "delete", "SlopGardenClient"]);
        Assert.Contains(client.Prepare, c => c.Arguments.Contains("SlopGardenServer"));
    }

    [Fact]
    public void Systemd_units_are_enabled_at_boot_and_can_be_stopped()
    {
        var server = MachineServices.Install(ServiceRole.Server, ServicePlatform.Linux,
            "/opt/slopgarden-server/SlopGarden.Server", null, null, "http://192.168.1.20:5080", "s3cret");
        Assert.Equal("systemd", server.Manager);
        Assert.Equal("/etc/systemd/system/slopgarden-server.service", server.UnitPath);
        Assert.Contains("WantedBy=multi-user.target", server.UnitBody);
        Assert.Contains("Type=notify", server.UnitBody);
        Assert.Contains("Restart=always", server.UnitBody);
        Assert.Contains("ExecStart=/opt/slopgarden-server/SlopGarden.Server", server.UnitBody);
        Assert.Contains(server.Activate, c => c.Arguments is ["systemctl", "enable", "slopgarden-server"]);
        Assert.Contains(server.Activate, c => c.Arguments is ["systemctl", "restart", "slopgarden-server"]);
        Assert.Contains(server.Stop, c => c.Arguments is ["systemctl", "disable", "--now", "slopgarden-server"]);
        Assert.Contains(server.Prepare, c => c.Arguments is ["systemctl", "disable", "--now", "slopgarden-client"]);
        Assert.DoesNotContain("docker", server.UnitBody, StringComparison.OrdinalIgnoreCase);

        var client = MachineServices.Install(ServiceRole.Client, ServicePlatform.Linux,
            "/opt/slopgarden-client/slopgarden-client", "http://192.168.1.20:5080", "Greenhouse Pi", null, "");
        Assert.Contains("User=slopgarden", client.UnitBody);
        Assert.Contains("ExecStart=/opt/slopgarden-client/slopgarden-client run", client.UnitBody);
        Assert.Contains("http://192.168.1.20:5080", client.Files[0].Contents);
        Assert.Contains(client.Activate, c => c.Arguments is ["systemctl", "enable", "slopgarden-client"]);
        Assert.Contains(client.Stop, c => c.Arguments is ["systemctl", "disable", "--now", "slopgarden-client"]);
        Assert.Contains(client.Prepare, c => c.Arguments.Contains("slopgarden-server"));
    }

    [Fact]
    public void Launchd_services_load_at_boot_and_removal_unloads_them()
    {
        var server = MachineServices.Install(ServiceRole.Server, ServicePlatform.MacOs,
            "/usr/local/slopgarden-server/SlopGarden.Server", null, null, null, "s3cret");
        Assert.Equal("launchd", server.Manager);
        Assert.Equal("com.slopgarden.server", server.Name);
        Assert.Contains("<key>RunAtLoad</key>", server.UnitBody);
        Assert.Contains("<key>KeepAlive</key>", server.UnitBody);
        Assert.Contains("<true/>", server.UnitBody);
        Assert.Contains("/Library/LaunchDaemons/com.slopgarden.server.plist", server.UnitPath);
        Assert.Contains(server.Activate, c => c.Arguments is ["launchctl", "bootstrap", "system", "/Library/LaunchDaemons/com.slopgarden.server.plist"]);
        Assert.Contains(server.Activate, c => c.Arguments is ["launchctl", "enable", "system/com.slopgarden.server"]);
        Assert.Contains(server.Activate, c => c.Arguments is ["launchctl", "kickstart", "-k", "system/com.slopgarden.server"]);
        Assert.Contains(server.Stop, c => c.Arguments is ["launchctl", "bootout", "system/com.slopgarden.server"]);
        Assert.Contains(server.Prepare, c => c.Arguments.Contains("system/com.slopgarden.client"));

        var client = MachineServices.Install(ServiceRole.Client, ServicePlatform.MacOs,
            "/usr/local/slopgarden-client/slopgarden-client", "http://192.168.1.20:5080", "Greenhouse Mac", null, "");
        Assert.Equal("com.slopgarden.client", client.Name);
        Assert.Contains("<string>run</string>", client.UnitBody);
        Assert.Contains("http://192.168.1.20:5080", client.Files[0].Contents);
    }

    [Fact]
    public void Systemd_units_pass_systemd_analyze_verify()
    {
        const string analyze = "/usr/bin/systemd-analyze";
        Assert.True(File.Exists(analyze), "systemd-analyze is required to check the unit files.");
        foreach (var plan in new[]
        {
            MachineServices.Install(ServiceRole.Server, ServicePlatform.Linux, "/bin/true", null, null, null, "s3cret"),
            MachineServices.Install(ServiceRole.Client, ServicePlatform.Linux, "/bin/true", "http://127.0.0.1:5080", "Test", null, ""),
        })
        {
            var path = Path.Combine(Path.GetTempPath(), plan.Name + ".service");
            File.WriteAllText(path, plan.UnitBody);
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(analyze, $"verify {path}") { RedirectStandardError = true, RedirectStandardOutput = true };
                using var process = System.Diagnostics.Process.Start(start)!;
                var error = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, error);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Server_service_hooks_load_on_linux()
    {
        var builder = WebApplication.CreateBuilder();
        MachineHost.Configure(builder.Host);
        using var host = builder.Build();
    }

    [Fact]
    public void Host_key_fingerprint_is_sha256()
    {
        var fp = MachineSetup.FingerprintSha256([1, 2, 3, 4]);
        Assert.StartsWith("SHA256:", fp);
        Assert.DoesNotContain("=", fp);
    }

    [Fact]
    public void A_release_selects_both_images_and_latest_is_the_newest()
    {
        var json = """
            [
              {"tag_name":"v0.2.1","draft":false},
              {"tag_name":"v0.2.10","draft":false},
              {"tag_name":"v0.2.3","draft":true},
              {"tag_name":"v0.2.2","draft":false}
            ]
            """;
        var releases = ReleaseImages.Parse(json, "Eacarter1990");
        Assert.Equal("0.2.10", releases[0].Version);
        Assert.Equal("ghcr.io/eacarter1990/slopgarden-server:0.2.10", releases[0].ServerImage);
        Assert.Equal("ghcr.io/eacarter1990/slopgarden-client:0.2.10", releases[0].ClientImage);
        Assert.DoesNotContain(releases, r => r.Version == "0.2.3");
        Assert.Equal("0.2.10", ReleaseImages.Choose(releases, "latest", null, "eacarter1990")!.Version);
        var pinned = ReleaseImages.Choose(releases, "pinned", "v0.2.1", "eacarter1990")!;
        Assert.Equal("ghcr.io/eacarter1990/slopgarden-client:0.2.1", pinned.ClientImage);
        Assert.Equal("ghcr.io/eacarter1990/slopgarden-server:0.2.1", pinned.ServerImage);
    }

    [Fact]
    public void Discovery_packets_yield_the_server_url()
    {
        var udp = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryAnnouncement(Discovery.ServerMessageType, "http://192.168.1.20:5080", "SlopGarden", "0.2.0"), SlopJson.Options);
        Assert.Equal("http://192.168.1.20:5080", MachineSetup.ServerUrlFromUdp(udp));
        Assert.Null(MachineSetup.ServerUrlFromUdp("not json"u8.ToArray()));

        var mdns = MdnsCodec.AnnounceServer("slopgarden", 5080, System.Net.IPAddress.Parse("10.0.0.8"), "http://10.0.0.8:5080");
        Assert.Equal("http://10.0.0.8:5080", MachineSetup.ServerUrlFromMdns(mdns));
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
