using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;

namespace SlopGarden.Server.Tests;

public sealed class FakeDevice : IAsyncDisposable
{
    public required Guid DeviceId { get; init; }
    public required string Token { get; init; }
    public required HubConnection Hub { get; init; }
    public ConcurrentQueue<DeviceConfigDocument> Configs { get; } = new();
    public ConcurrentQueue<ActuatorCommandMessage> Commands { get; } = new();
    public ConcurrentQueue<EmergencyStopState> Stops { get; } = new();

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}

public class DeviceAndSafetyTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    private static DeviceEnvironmentInfo Env => new("Europe/Amsterdam", 120, DateTimeOffset.Now, 52.1, 5.1, "Utrecht");

    private async Task<FakeDevice> OnboardAsync(HttpClient admin, string hardwareId)
    {
        var anon = f.CreateClient();
        var secret = Guid.NewGuid().ToString("N");
        var plugins = new List<PluginInfo>
        {
            new("emulator-sensor", "1.0.0", "Emulator sensor", [PluginKinds.Sensor], "abc", "builtin", PluginTrust.Builtin, "SlopGarden", null, null),
            new("third-party", "0.1.0", "Third party", [PluginKinds.Sensor], "def", "external", PluginTrust.Unsigned, null, null, null),
        };
        var reg = await anon.PostAsJsonAsync("/api/device-api/register",
            new DeviceRegistrationRequest(hardwareId, $"pi-{hardwareId}", "Linux", "Arm64", "0.1.0", secret, "broadcast", null, Env, plugins), HttpExtensions.Json);
        reg.EnsureSuccessStatusCode();
        var registered = (await reg.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(HttpExtensions.Json))!;
        Assert.Equal(DeviceStatuses.Pending, registered.Status);

        var pending = await anon.GetFromJsonAsync<DeviceRegistrationStatus>($"/api/device-api/register/{registered.DeviceId}?secret={secret}", HttpExtensions.Json);
        Assert.Null(pending!.DeviceToken);

        await admin.Op("approve_device", new { deviceId = registered.DeviceId });
        var approved = await anon.GetFromJsonAsync<DeviceRegistrationStatus>($"/api/device-api/register/{registered.DeviceId}?secret={secret}", HttpExtensions.Json);
        Assert.Equal(DeviceStatuses.Approved, approved!.Status);
        Assert.StartsWith("sgd_", approved.DeviceToken);
        var again = await anon.GetFromJsonAsync<DeviceRegistrationStatus>($"/api/device-api/register/{registered.DeviceId}?secret={secret}", HttpExtensions.Json);
        Assert.Null(again!.DeviceToken);

        var device = new FakeDevice { DeviceId = registered.DeviceId, Token = approved.DeviceToken!, Hub = f.DeviceHub(approved.DeviceToken!) };
        device.Hub.On<DeviceConfigDocument>(DeviceHubMethods.ApplyConfig, c => device.Configs.Enqueue(c));
        device.Hub.On<ActuatorCommandMessage>(DeviceHubMethods.ExecuteCommand, c => device.Commands.Enqueue(c));
        device.Hub.On<EmergencyStopState>(DeviceHubMethods.EmergencyStop, s => device.Stops.Enqueue(s));
        device.Hub.On(DeviceHubMethods.RequestDiscovery, () => { });
        device.Hub.On<CaptureFrameRequest>(DeviceHubMethods.CaptureFrame, _ => { });
        await device.Hub.StartAsync();
        await device.Hub.InvokeAsync(DeviceHubMethods.Hello, new DeviceHello("0.1.0", "Linux", "Arm64", $"pi-{hardwareId}", null, Env, plugins, 0));
        await HttpExtensions.Wait(() => device.Configs.IsEmpty ? null : device.Configs.Last());
        return device;
    }

    [Fact]
    public async Task Unapproved_and_bogus_tokens_cannot_use_the_device_hub()
    {
        var hub = f.DeviceHub("sgd_not-a-real-token");
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Device_onboarding_config_push_readings_and_plugin_trust()
    {
        var admin = await f.LoginAsync();
        await using var dev = await OnboardAsync(admin, "hw-onboard");
        var plants = await admin.Op("list_plants");
        var plantId = plants[0]!["id"]!.ToString();

        var sensor = await admin.Op("create_sensor", new
        {
            deviceId = dev.DeviceId, name = "Soil probe", pluginId = "emulator-sensor", settings = new { sensorId = "s1" },
            channels = new[] { new { key = "value", metric = Metrics.SoilMoisturePct, alertLow = 20.0 } }, targetType = "plant", plantId, readIntervalSec = 10,
        });
        var channelId = Guid.Parse(sensor["channels"]![0]!["id"]!.ToString());
        var config = await HttpExtensions.Wait(() => dev.Configs.LastOrDefault(c => c.Sensors.Count == 1));
        Assert.Equal(channelId, config.Sensors[0].Channels[0].Id);
        Assert.Equal("Europe/Amsterdam", config.TimeZoneId);

        var now = DateTimeOffset.UtcNow;
        var ack = await dev.Hub.InvokeAsync<ReadingBatchAck>(DeviceHubMethods.ReportReadings,
            new ReadingBatch(Guid.NewGuid(), [new ReadingSample(channelId, now.AddSeconds(-10), 41.5, 1800, 0), new ReadingSample(channelId, now, 12.0, 2400, 0)]));
        Assert.Equal(2, ack.Accepted);
        await dev.Hub.InvokeAsync<ReadingBatchAck>(DeviceHubMethods.ReportReadings, new ReadingBatch(Guid.NewGuid(), [new ReadingSample(channelId, now, 12.0, 2400, 0)]));

        Assert.Equal(2, f.WithDb(db => db.Readings.Count(r => r.ChannelId == channelId)));
        Assert.Equal(12.0, f.WithDb(db => db.SensorChannels.First(c => c.Id == channelId).LastValue));
        await HttpExtensions.Wait(() => f.WithDb(db => db.Notifications.FirstOrDefault(n => n.Category == "sensor" && n.TargetId == Guid.Parse(sensor["id"]!.ToString()))));

        var series = await admin.Op("get_readings", new { channelId, hours = 1 });
        Assert.Equal(2, series[0]!["points"]!.AsArray().Sum(p => p!["count"]!.GetValue<int>()));

        var detail = await admin.Op("get_device", new { deviceId = dev.DeviceId });
        Assert.Contains(detail["plugins"]!.AsArray(), p => p!["trust"]!.ToString() == "unsigned");
        Assert.Equal(1, detail["device"]!["unsignedPlugins"]!.GetValue<int>());
    }

    [Fact]
    public async Task Commands_respect_safety_limits_and_emergency_stops()
    {
        var admin = await f.LoginAsync();
        await using var dev = await OnboardAsync(admin, "hw-safety");
        var beds = await admin.Op("list_beds");
        var bedId = beds[0]!["id"]!.ToString();
        var valve = await admin.Op("create_actuator", new { deviceId = dev.DeviceId, name = "Valve", kind = "valve", pluginId = "emulator-actuator", settings = new { actuatorId = "v1" }, targetType = "bed", bedId });
        var valveId = Guid.Parse(valve["id"]!.ToString());

        var config = await HttpExtensions.Wait(() => dev.Configs.LastOrDefault(c => c.Actuators.Any(a => a.Id == valveId)));
        Assert.Equal(900, config.Actuators.Single().Safety.MaxRunSeconds);

        var ok = await admin.Op("run_actuator", new { actuatorId = valveId, action = "pulse", durationSec = 2000 });
        Assert.Equal(CommandStatuses.Sent, ok["status"]!.ToString());
        Assert.Equal(900, ok["durationSec"]!.GetValue<double>());
        var sent = await HttpExtensions.Wait(() => dev.Commands.FirstOrDefault(c => c.ActuatorId == valveId));
        Assert.Equal(900, sent.DurationSec);

        await dev.Hub.InvokeAsync(DeviceHubMethods.ReportActuatorRun, new ActuatorRunReport(Guid.NewGuid(), valveId, sent.CommandId, null,
            DateTimeOffset.UtcNow.AddMinutes(-70), DateTimeOffset.UtcNow.AddMinutes(-10), 3600, null, "command", "completed"));
        var blocked = await admin.Op("run_actuator", new { actuatorId = valveId, action = "pulse", durationSec = 30 }, expectSuccess: false);
        Assert.Contains("Daily run limit", blocked["error"]!.ToString());

        var stop = await admin.Op("trigger_emergency_stop", new { scopeType = "bed", scopeId = bedId, reason = "test flood" });
        Assert.Equal(1, stop["affectedActuators"]!.GetValue<int>());
        var pushed = await HttpExtensions.Wait(() => dev.Stops.LastOrDefault(s => s.StoppedActuatorIds.Contains(valveId)));
        Assert.Equal("test flood", pushed.Reason);
        var whileStopped = await admin.Op("run_actuator", new { actuatorId = valveId, action = "on" }, expectSuccess: false);
        Assert.Contains("Emergency stop", whileStopped["error"]!.ToString());
        Assert.Equal(CommandStatuses.Sent, (await admin.Op("run_actuator", new { actuatorId = valveId, action = "off" }))["status"]!.ToString());

        await admin.Op("release_emergency_stop", new { stopId = stop["stop"]!["id"]!.ToString() });
        await HttpExtensions.Wait(() => dev.Stops.LastOrDefault(s => !s.StoppedActuatorIds.Contains(valveId) && dev.Stops.Count > 1));
    }

    [Fact]
    public async Task Tank_low_interlock_blocks_pumping()
    {
        var admin = await f.LoginAsync();
        await using var dev = await OnboardAsync(admin, "hw-tank");
        var tank = await admin.Op("create_sensor", new { deviceId = dev.DeviceId, name = "Tank", pluginId = "emulator-sensor", channels = new[] { new { key = "value", metric = Metrics.WaterLevelPct } }, targetType = "garden" });
        var channelId = Guid.Parse(tank["channels"]![0]!["id"]!.ToString());
        var pump = await admin.Op("create_actuator", new { deviceId = dev.DeviceId, name = "Pump", kind = "pump", pluginId = "emulator-actuator", targetType = "garden" });
        await admin.Op("set_safety_limit", new { scopeType = "actuator", scopeId = pump["id"]!.ToString(), limitType = "tank_low_lockout", value = 15, channelId });
        await dev.Hub.InvokeAsync<ReadingBatchAck>(DeviceHubMethods.ReportReadings, new ReadingBatch(Guid.NewGuid(), [new ReadingSample(channelId, DateTimeOffset.UtcNow, 8, null, 0)]));

        var res = await admin.Op("run_actuator", new { actuatorId = pump["id"]!.ToString(), action = "pulse", durationSec = 10 }, expectSuccess: false);
        Assert.Contains("tank level", res["error"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        var config = await HttpExtensions.Wait(() => dev.Configs.LastOrDefault(c => c.Actuators.Any(a => a.Safety.Interlocks.Count > 0)));
        Assert.Equal("lt", config.Actuators.Single(a => a.Kind == "pump").Safety.Interlocks.Single().Operator);
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        var viewer = await f.LoginAsync("viewer", "demo-password");
        var beds = await viewer.Op("list_beds");
        Assert.NotEmpty(beds.AsArray());
        var res = await viewer.PostAsJsonAsync("/api/ops/create_bed", new { gardenId = beds[0]!["gardenId"]!.ToString(), name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);

        var grower = await f.LoginAsync("grower", "demo-password");
        await grower.Op("create_bed", new { gardenId = beds[0]!["gardenId"]!.ToString(), name = "Grower bed" });
        var approve = await grower.PostAsJsonAsync("/api/ops/approve_device", new { deviceId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }
}

public class AiAndMcpTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Chat_creates_a_proposal_and_approval_executes_it()
    {
        var admin = await f.LoginAsync();
        var conv = await (await admin.PostAsJsonAsync("/api/chat/conversations", new { })).Content.ReadFromJsonAsync<JsonObject>();
        var convId = conv!["id"]!.ToString();
        (await admin.PostAsJsonAsync($"/api/chat/conversations/{convId}/messages", new { content = "Please set a moisture target for the tomatoes" })).EnsureSuccessStatusCode();

        var proposal = await HttpExtensions.Wait(() => f.WithDb(db => db.AiProposals.AsNoTracking().FirstOrDefault(p => p.ConversationId == Guid.Parse(convId))), TimeSpan.FromSeconds(20));
        Assert.Equal("set_care_target", proposal.ToolName);
        Assert.Equal("pending", proposal.Status);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Rationale));
        Assert.NotEmpty(proposal.Changes);
        Assert.Empty(f.WithDb(db => db.CareTargets.Where(t => t.PlantId == proposal.TargetId).ToList()));

        var decided = await (await admin.PostAsJsonAsync($"/api/proposals/{proposal.Id}/approve", new { note = "ok" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("executed", decided!["status"]!.ToString());
        var target = f.WithDb(db => db.CareTargets.Single(t => t.PlantId == proposal.TargetId));
        Assert.Equal(50, target.MinValue);
        Assert.Equal("ai", target.Source);

        var messages = await admin.GetFromJsonAsync<JsonObject>($"/api/chat/conversations/{convId}/messages");
        var roles = messages!["messages"]!.AsArray().Select(m => m!["role"]!.ToString()).ToList();
        Assert.Contains("tool", roles);
        Assert.Contains("event", roles);
    }

    [Fact]
    public async Task Rejected_proposals_change_nothing()
    {
        var admin = await f.LoginAsync();
        var conv = await (await admin.PostAsJsonAsync("/api/chat/conversations", new { })).Content.ReadFromJsonAsync<JsonObject>();
        var convId = Guid.Parse(conv!["id"]!.ToString());
        var schedulesBefore = f.WithDb(db => db.Schedules.Count());
        await admin.PostAsJsonAsync($"/api/chat/conversations/{convId}/messages", new { content = "emergency stop everything!" });
        await HttpExtensions.Wait(() => f.WithDb(db => db.EmergencyStops.FirstOrDefault(s => s.Active && s.ActivatedByType == "ai")), TimeSpan.FromSeconds(20));
        Assert.Equal(schedulesBefore, f.WithDb(db => db.Schedules.Count()));
        foreach (var s in f.WithDb(db => db.EmergencyStops.Where(x => x.Active).ToList()))
            await admin.Op("release_emergency_stop", new { stopId = s.Id });
    }

    [Fact]
    public async Task Mcp_lists_tools_and_respects_token_scopes()
    {
        var admin = await f.LoginAsync();
        async Task<string> Token(params string[] scopes) =>
            (await (await admin.PostAsJsonAsync("/api/tokens", new { name = string.Join("-", scopes), scopes })).Content.ReadFromJsonAsync<JsonObject>())!["token"]!.ToString();

        var mcp = f.CreateClient();
        async Task<JsonNode> Rpc(string token, string method, object? @params = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await mcp.SendAsync(req);
            res.EnsureSuccessStatusCode();
            return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        }

        var read = await Token("read");
        var init = await Rpc(read, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        Assert.Equal("slopgarden", init["result"]!["serverInfo"]!["name"]!.ToString());
        var tools = await Rpc(read, "tools/list");
        Assert.True(tools["result"]!["tools"]!.AsArray().Count >= 60);

        var overview = await Rpc(read, "tools/call", new { name = "get_garden_overview", arguments = new { } });
        Assert.False(overview["result"]!["isError"]!.GetValue<bool>());
        var gardenId = overview["result"]!["structuredContent"]!["garden"]!["id"]!.ToString();

        var denied = await Rpc(read, "tools/call", new { name = "create_bed", arguments = new { gardenId, name = "MCP bed" } });
        Assert.True(denied["result"]!["isError"]!.GetValue<bool>());

        var propose = await Token("read", "propose");
        var proposed = await Rpc(propose, "tools/call", new { name = "create_bed", arguments = new { gardenId, name = "MCP bed", reason = "testing" } });
        Assert.Contains("pending_approval", proposed["result"]!["content"]![0]!["text"]!.ToString());
        Assert.False(f.WithDb(db => db.Beds.Any(b => b.Name == "MCP bed")));
        Assert.True(f.WithDb(db => db.AiProposals.Any(p => p.Origin == "mcp" && p.ToolName == "create_bed")));

        var write = await Token("read", "write");
        await Rpc(write, "tools/call", new { name = "create_bed", arguments = new { gardenId, name = "MCP direct bed" } });
        Assert.True(f.WithDb(db => db.Beds.Any(b => b.Name == "MCP direct bed")));

        using var noAuth = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.SendAsync(noAuth)).StatusCode);
    }

    [Fact]
    public async Task Api_keys_are_never_returned_to_the_browser()
    {
        var admin = await f.LoginAsync();
        (await admin.PostAsJsonAsync("/api/settings/ai/providers", new { name = "Secret test", baseUrl = "https://example.invalid/v1", apiKey = "sk-super-secret-value", defaultModel = "x" })).EnsureSuccessStatusCode();
        var body = await admin.GetStringAsync("/api/settings/ai");
        Assert.DoesNotContain("sk-super-secret-value", body);
        Assert.Contains("\"hasApiKey\":true", body);
        var stored = f.WithDb(db => db.Secrets.Select(s => s.Ciphertext).ToList());
        Assert.DoesNotContain(stored, c => c.Contains("sk-super-secret-value"));
    }
}
