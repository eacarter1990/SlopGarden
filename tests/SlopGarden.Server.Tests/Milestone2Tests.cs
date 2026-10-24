using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Tests;

public class Milestone2UnitTests
{
    [Theory]
    [InlineData("0.2.0", "0.3.0", true)]
    [InlineData("0.3.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "", false)]
    [InlineData("v0.1.0", "0.2.0", true)]
    public void Outdated_means_older_than_the_published_release(string running, string latest, bool outdated) =>
        Assert.Equal(outdated, ClientVersions.IsOutdated(running, latest));

    [Fact]
    public void Mdns_client_announcement_round_trips()
    {
        var packet = MdnsCodec.AnnounceClient("pi-1", 8787, System.Net.IPAddress.Parse("10.1.2.3"), "hw-1", "0.2.0", "Greenhouse");
        Assert.True(MdnsCodec.TryRead(packet, out var message));
        var svc = Assert.Single(message.Services);
        Assert.Equal("hw-1", svc.HardwareId);
        Assert.Equal("0.2.0", svc.Version);
        Assert.Equal(8787, svc.Port);
        Assert.Equal("10.1.2.3", svc.Address!.ToString());
        Assert.Contains("slopgarden", svc.ServiceType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mdns_server_announcement_carries_the_url_and_queries_match()
    {
        var packet = MdnsCodec.AnnounceServer("slopgarden", 5080, System.Net.IPAddress.Loopback, "http://10.0.0.8:5080");
        Assert.True(MdnsCodec.TryRead(packet, out var message));
        Assert.Equal("http://10.0.0.8:5080", Assert.Single(message.Services).Url);
        Assert.True(MdnsCodec.IsQueryFor(MdnsCodec.Query(Discovery.MdnsClientService), Discovery.MdnsClientService));
        Assert.False(MdnsCodec.IsQueryFor(packet, Discovery.MdnsClientService));
    }

    [Fact]
    public void Report_template_includes_conditions_and_harvest_predictions()
    {
        var conditions = new JsonObject { ["beds"] = new JsonArray(new JsonObject { ["name"] = "Tomato bed", ["medium"] = "soil", ["readings"] = new JsonArray(new JsonObject { ["name"] = "Moisture", ["value"] = 44, ["unit"] = "%" }) }) };
        var predictions = new JsonArray(new JsonObject { ["plant"] = "San Marzano", ["bed"] = "Tomato bed", ["stage"] = "fruiting", ["summary"] = "Harvest window opens in 6 days." });
        var body = ReportService.Template("Backyard", ReportCadence.OnDemand, conditions, predictions, []);
        Assert.Contains("Tomato bed", body);
        Assert.Contains("44", body);
        Assert.Contains("Harvest", body);
        Assert.Contains("San Marzano", body);
    }

    [Fact]
    public async Task Mjpeg_frame_has_the_boundary_and_the_bytes()
    {
        var jpeg = new byte[] { 1, 2, 3, 4 };
        using var ms = new MemoryStream();
        await Mjpeg.WriteFrameAsync(ms, jpeg, CancellationToken.None);
        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("--frame", text);
        Assert.Contains("Content-Length: 4", text);
        Assert.Equal(jpeg, ms.ToArray()[^6..^2]);
    }
}

public class Milestone2ApiTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task On_demand_report_stores_predictions_and_a_garden_log()
    {
        var admin = await f.LoginAsync();
        var gardens = await admin.Op("list_gardens");
        var gardenId = gardens[0]!["id"]!.ToString();
        var res = await admin.PostAsJsonAsync($"/api/gardens/{gardenId}/reports", new { kind = "on_demand" }, HttpExtensions.Json);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        var report = JsonNode.Parse(body)!;
        Assert.True(report["predictions"]!.AsArray().Count > 0);
        Assert.NotNull(report["conditions"]);
        Assert.Contains("Harvest", report["body"]!.ToString());

        var log = await admin.GetFromJsonAsync<JsonObject>($"/api/logs?scope=garden&id={gardenId}", HttpExtensions.Json);
        Assert.Contains("Growth report", log!["text"]!.ToString());
    }

    [Fact]
    public async Task Camera_stream_is_multipart_and_repeats_the_latest_frame()
    {
        var admin = await f.LoginAsync();
        var id = Guid.NewGuid();
        var store = f.Services.GetRequiredService<CameraFrameStore>();
        store.Put(id, [0xFF, 0xD8, 0x00, 0xFF, 0xD9], "image/jpeg", null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var res = await admin.GetAsync($"/api/cameras/{id}/stream?width=320", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("multipart/x-mixed-replace", res.Content.Headers.ContentType!.MediaType);
        var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buf = new byte[256];
        var read = await stream.ReadAsync(buf, cts.Token);
        Assert.Contains("--frame", Encoding.ASCII.GetString(buf, 0, read));
    }

    [Fact]
    public async Task Setup_database_is_refused_once_an_account_exists()
    {
        var anon = f.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/auth/setup-database", new { host = "127.0.0.1", port = 1, database = "slopgarden", username = "slopgarden", password = "nope" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Device_list_shows_an_outdated_client_and_auto_update_is_opt_in()
    {
        var admin = await f.LoginAsync();
        await using var dev = await DeviceHarness.OnboardAsync(f, admin, "hw-update-" + Guid.NewGuid().ToString("N")[..8]);
        var saved = await admin.PutAsJsonAsync("/api/releases", new { mode = "pinned", version = "9.9.9", serverAutoUpdate = false }, HttpExtensions.Json);
        saved.EnsureSuccessStatusCode();
        var list = await admin.Op("list_devices");
        var row = list.AsArray().First(d => d!["id"]!.ToString() == dev.DeviceId.ToString())!;
        Assert.True(row["updateAvailable"]!.GetValue<bool>());
        Assert.False(row["autoUpdate"]!.GetValue<bool>());

        var toggled = await admin.PutAsJsonAsync($"/api/devices/{dev.DeviceId}/auto-update", new { enabled = true }, HttpExtensions.Json);
        toggled.EnsureSuccessStatusCode();
        var pushed = await admin.PostAsync($"/api/devices/{dev.DeviceId}/update", new StringContent(""));
        var text = await pushed.Content.ReadAsStringAsync();
        Assert.True(pushed.IsSuccessStatusCode, text);
        Assert.Contains("Update sent", text);
    }

    [Fact]
    public async Task Daily_weekly_and_monthly_report_schedules_can_be_enabled()
    {
        var admin = await f.LoginAsync();
        var gardens = await admin.Op("list_gardens");
        var gardenId = gardens[1]!["id"]!.ToString();
        foreach (var cadence in new[] { "daily", "weekly", "monthly" })
        {
            var res = await admin.PostAsJsonAsync($"/api/gardens/{gardenId}/reports/schedule", new { cadence, enabled = true }, HttpExtensions.Json);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }
        var schedules = await admin.GetFromJsonAsync<JsonArray>($"/api/gardens/{gardenId}/reports/schedules", HttpExtensions.Json);
        Assert.Equal(3, schedules!.Count);
        Assert.Contains(schedules, s => s!["cadence"]!.ToString() == "weekly" && s["enabled"]!.GetValue<bool>());
    }
}
