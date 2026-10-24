using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlopGarden.Contracts;
using SlopGarden.DevLlm;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Background;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Tests;

public class HydroponicTests(ServerFixture f) : IClassFixture<ServerFixture>
{
    private sealed record Rig(FakeDevice Device, Guid ReservoirId, Guid BedId, Guid PumpId, Guid FlowChannelId, Guid PhChannelId, Guid DoserA, Guid DoserB, Guid PhDown);

    private static async Task<Rig> BuildRigAsync(ServerFixture f, HttpClient admin, string hw)
    {
        var dev = await DeviceHarness.OnboardAsync(f, admin, hw);
        var gardenId = (await admin.Op("list_gardens"))[0]!["id"]!.ToString();
        var res = await admin.Op("create_reservoir", new { name = $"Tank {hw}", gardenId, volumeLiters = 40 });
        var reservoirId = Guid.Parse(res["id"]!.ToString());
        var bed = await admin.Op("create_bed", new { gardenId, name = $"NFT {hw}", growingMedium = "hydroponic", hydroSystem = "nft", reservoirId });
        var bedId = Guid.Parse(bed["id"]!.ToString());
        await admin.Op("create_plant", new { bedId, name = "Lettuce", profile = "lettuce", quantity = 10 });

        async Task<Guid> Sensor(string name, string metric) =>
            Guid.Parse((await admin.Op("create_sensor", new { deviceId = dev.DeviceId, name, pluginId = "emulator-sensor", channels = new[] { new { key = "value", metric } }, targetType = "reservoir", reservoirId }))["channels"]![0]!["id"]!.ToString());
        var flow = await Sensor("Flow", Metrics.FlowLitersPerMin);
        var ph = await Sensor("pH", Metrics.Ph);
        async Task<Guid> Actuator(string name, string kind, string role, object? extra = null)
        {
            var created = await admin.Op("create_actuator", new { deviceId = dev.DeviceId, name, kind, role, pluginId = "emulator-actuator", targetType = "reservoir", reservoirId, flowRateMlPerMin = kind == "fertilizer_doser" ? 60 : (double?)null });
            var id = Guid.Parse(created["id"]!.ToString());
            if (extra is not null)
            {
                var args = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject();
                args["actuatorId"] = id.ToString();
                await admin.Op("update_actuator", args);
            }
            return id;
        }
        var pump = await Actuator("NFT pump", "pump", "circulation_pump", new { flowChannelId = flow, flowMinWhenOn = 0.5, flowGraceSec = 10, flowMaxWhenOff = 0.3 });
        var a = await Actuator("Nutrient A", "fertilizer_doser", "dose_nutrient_a");
        var b = await Actuator("Nutrient B", "fertilizer_doser", "dose_nutrient_b");
        var down = await Actuator("pH Down", "fertilizer_doser", "dose_ph_down");
        return new Rig(dev, reservoirId, bedId, pump, flow, ph, a, b, down);
    }

    private static Task Report(FakeDevice d, Guid channel, double value) =>
        d.Hub.InvokeAsync<ReadingBatchAck>(DeviceHubMethods.ReportReadings, new ReadingBatch(Guid.NewGuid(), [new ReadingSample(channel, DateTimeOffset.UtcNow, value, null, 0)]));

    private static Task State(FakeDevice d, Guid actuator, bool on, DateTimeOffset? since = null) =>
        d.Hub.InvokeAsync(DeviceHubMethods.ReportActuatorState, new ActuatorStateReport(actuator, on, null, since ?? DateTimeOffset.UtcNow, null, "test"));

    private static Task Run(FakeDevice d, Guid actuator, double seconds, double ml) =>
        d.Hub.InvokeAsync(DeviceHubMethods.ReportActuatorRun, new ActuatorRunReport(Guid.NewGuid(), actuator, null, null, DateTimeOffset.UtcNow.AddSeconds(-seconds), DateTimeOffset.UtcNow, seconds, ml, "command", "completed"));

    [Fact]
    public async Task Beds_inherit_the_garden_default_medium_and_hydro_beds_drop_soil_targets()
    {
        var admin = await f.LoginAsync();
        var gardenId = (await admin.Op("list_gardens"))[0]!["id"]!.ToString();
        var bed = await admin.Op("create_bed", new { gardenId, name = "Inherits" });
        await admin.Op("create_plant", new { bedId = bed["id"]!.ToString(), name = "Inherited lettuce", profile = "lettuce" });
        JsonNode Bed(JsonNode o) => o["beds"]!.AsArray().First(b => b!["bed"]!["id"]!.ToString() == bed["id"]!.ToString())!;

        var soil = Bed(await admin.Op("get_garden_overview"));
        Assert.Equal("soil", soil["medium"]!.ToString());
        Assert.Contains(soil["plants"]![0]!["targets"]!.AsArray(), t => t!["metric"]!.ToString() == Metrics.SoilMoisturePct);

        await admin.Op("update_garden", new { gardenId, defaultGrowingMedium = "hydroponic" });
        var hydro = Bed(await admin.Op("get_garden_overview"));
        Assert.Equal("hydroponic", hydro["medium"]!.ToString());
        Assert.DoesNotContain(hydro["plants"]![0]!["targets"]!.AsArray(), t => t!["metric"]!.ToString() == Metrics.SoilMoisturePct);
        await admin.Op("update_garden", new { gardenId, defaultGrowingMedium = "soil" });

        var bad = await admin.PostAsJsonAsync("/api/ops/update_bed", new { bedId = bed["id"]!.ToString(), hydroSystem = "fish_tank" });
        Assert.Contains("hydroSystem must be one of", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Device_config_marks_pumps_continuous_and_gives_dosers_reservoir_limits()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-config");
        await using var _ = rig.Device;
        var config = await HttpExtensions.Wait(() => rig.Device.Configs.LastOrDefault(c => c.Actuators.Count == 4 && c.Actuators.Any(a => a.FlowWatch is not null)));

        var pump = config.Actuators.Single(a => a.Id == rig.PumpId);
        Assert.True(pump.ContinuousRun);
        Assert.Equal(ActuatorRoles.CirculationPump, pump.Role);
        Assert.Null(pump.Safety.MaxRunSeconds);
        Assert.Null(pump.Safety.MaxDailyRunSeconds);
        Assert.Equal(rig.ReservoirId, pump.ReservoirId);
        Assert.Equal(rig.FlowChannelId, pump.FlowWatch!.ChannelId);
        Assert.Equal(10, pump.FlowWatch.GraceSec);
        Assert.Equal(0.3, pump.FlowWatch.MaxFlowWhenOff);

        var doser = config.Actuators.Single(a => a.Id == rig.DoserA);
        Assert.Equal(rig.PumpId, doser.Safety.RequiresActuatorOn);
        Assert.Equal(600, doser.Safety.MinSecondsBetweenReservoirDoses);
        Assert.Equal(30, doser.Safety.MaxHourlyAmountMl);
        Assert.False(doser.ContinuousRun);
    }

    [Fact]
    public async Task Dosing_needs_the_circulation_pump_and_respects_the_mixing_wait()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-dose");
        await using var _ = rig.Device;

        var off = await admin.Op("run_actuator", new { actuatorId = rig.DoserA, action = "dose", amountMl = 5 }, expectSuccess: false);
        Assert.Contains("must be running", off["error"]!.ToString());

        await State(rig.Device, rig.PumpId, true);
        Assert.Equal(CommandStatuses.Sent, (await admin.Op("run_actuator", new { actuatorId = rig.DoserA, action = "dose", amountMl = 5 }))["status"]!.ToString());
        await Run(rig.Device, rig.DoserA, 5, 5);

        Assert.Equal(CommandStatuses.Sent, (await admin.Op("run_actuator", new { actuatorId = rig.DoserB, action = "dose", amountMl = 5 }))["status"]!.ToString());
        var again = await admin.Op("run_actuator", new { actuatorId = rig.DoserA, action = "dose", amountMl = 5 }, expectSuccess: false);
        Assert.Matches("Mixing wait|between runs", again["error"]!.ToString());
        var ph = await admin.Op("run_actuator", new { actuatorId = rig.PhDown, action = "dose", amountMl = 2 }, expectSuccess: false);
        Assert.Contains("Mixing wait", ph["error"]!.ToString());

        await f.WithDbAsync(async db => await db.ActuatorRuns.Where(r => r.ActuatorId == rig.DoserA).ExecuteUpdateAsync(s => s.SetProperty(r => r.EndedAt, DateTimeOffset.UtcNow.AddMinutes(-11))));
        await Run(rig.Device, rig.DoserA, 25, 25);
        await f.WithDbAsync(async db => await db.ActuatorRuns.Where(r => r.ActuatorId == rig.DoserA).ExecuteUpdateAsync(s => s.SetProperty(r => r.EndedAt, DateTimeOffset.UtcNow.AddMinutes(-11))));
        var hourly = await admin.Op("run_actuator", new { actuatorId = rig.DoserA, action = "dose", amountMl = 5 }, expectSuccess: false);
        Assert.Contains("Hourly dose limit", hourly["error"]!.ToString());
    }

    [Fact]
    public async Task Ai_dose_proposals_never_offer_always_allow()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-ai");
        await using var _ = rig.Device;
        await State(rig.Device, rig.PumpId, true);

        using var scope = f.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<ToolExecutor>();
        var userId = f.WithDb(db => db.Users.First(u => u.Username == "admin").Id);
        var ai = new Actor(ActorTypes.Ai, userId, "AI assistant", Roles.Admin, PatScopes.All);
        var outcome = await exec.ExecuteAsync("run_actuator", new JsonObject { ["actuatorId"] = rig.PhDown.ToString(), ["action"] = "dose", ["amountMl"] = 2, ["reason"] = "pH high" }, ai, ToolMode.Propose, CancellationToken.None);
        Assert.Equal(ToolOutcome.Proposed, outcome.Status);
        var proposal = f.WithDb(db => db.AiProposals.Single(p => p.Id == outcome.ProposalId));
        Assert.False(proposal.AllowStanding);
        Assert.Contains("Dose 2 ml ph down", proposal.Title);

        var res = await admin.PostAsJsonAsync($"/api/proposals/{proposal.Id}/approve", new { allowAlways = true });
        res.EnsureSuccessStatusCode();
        Assert.False(f.WithDb(db => db.AiStandingApprovals.Any(s => s.CreatedFromProposalId == proposal.Id)));

        // Even a standing approval created by hand for this doser doesn't let the AI dose without asking.
        await f.WithDbAsync(async db =>
        {
            db.AiStandingApprovals.Add(new AiStandingApproval { ToolName = "run_actuator", TargetType = "actuator", TargetId = rig.PhDown, ApprovedBy = userId });
            await db.SaveChangesAsync();
        });
        using var scope2 = f.Services.CreateScope();
        var second = await scope2.ServiceProvider.GetRequiredService<ToolExecutor>().ExecuteAsync("run_actuator",
            new JsonObject { ["actuatorId"] = rig.PhDown.ToString(), ["action"] = "dose", ["amountMl"] = 2, ["reason"] = "pH high" }, ai, ToolMode.Propose, CancellationToken.None);
        Assert.Equal(ToolOutcome.Proposed, second.Status);
    }

    [Fact]
    public async Task Watchdog_raises_flow_stall_unexpected_flow_and_pump_not_running()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-watch");
        await using var _ = rig.Device;
        var watchdog = f.Services.GetRequiredService<HydroWatchdog>();
        async Task Check()
        {
            using var scope = f.Services.CreateScope();
            await watchdog.CheckAsync(scope.ServiceProvider, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await State(rig.Device, rig.PumpId, true, DateTimeOffset.UtcNow.AddSeconds(-30));
        await Report(rig.Device, rig.FlowChannelId, 0.05);
        await Check();
        var stall = f.WithDb(db => db.Notifications.SingleOrDefault(n => n.DedupeKey == HydroWatchdog.DedupeKey(SafetyEventCodes.FlowStall, rig.PumpId)));
        Assert.NotNull(stall);
        Assert.Equal(Severities.Critical, stall!.Severity);
        await Check();
        Assert.Equal(1, f.WithDb(db => db.Notifications.Count(n => n.DedupeKey == HydroWatchdog.DedupeKey(SafetyEventCodes.FlowStall, rig.PumpId))));

        await State(rig.Device, rig.PumpId, false, DateTimeOffset.UtcNow.AddMinutes(-10));
        await Report(rig.Device, rig.FlowChannelId, 1.4);
        await Check();
        Assert.True(f.WithDb(db => db.Notifications.Any(n => n.DedupeKey == HydroWatchdog.DedupeKey(SafetyEventCodes.UnexpectedFlow, rig.PumpId))));
        Assert.True(f.WithDb(db => db.Notifications.Any(n => n.DedupeKey == HydroWatchdog.DedupeKey(SafetyEventCodes.PumpNotRunning, rig.PumpId) && n.Severity == Severities.Critical)));

        await rig.Device.Hub.InvokeAsync(DeviceHubMethods.ReportEvent, new DeviceEventReport(DateTimeOffset.UtcNow, Severities.Critical, "safety", "pump stalled", null, rig.PumpId, null, SafetyEventCodes.FlowStall));
        Assert.Equal(1, f.WithDb(db => db.Notifications.Count(n => n.DedupeKey == HydroWatchdog.DedupeKey(SafetyEventCodes.FlowStall, rig.PumpId))));
    }

    [Fact]
    public async Task Reservoir_status_tracks_targets_alerts_changes_and_top_ups()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-status");
        await using var _ = rig.Device;
        await Report(rig.Device, rig.PhChannelId, 7.1);

        var status = await admin.Op("get_reservoir", new { reservoirId = rig.ReservoirId });
        var ec = status["targets"]!.AsArray().Single(t => t!["metric"]!.ToString() == Metrics.EcMsCm)!;
        Assert.Equal(0.8, ec["min"]!.GetValue<double>());
        Assert.Equal("profile", ec["source"]!.ToString());
        Assert.Contains(status["alerts"]!.AsArray(), a => a!.ToString().Contains("ph 7.1 above 6.5"));
        Assert.Contains(status["alerts"]!.AsArray(), a => a!.ToString().Contains("should run continuously"));

        await admin.Op("set_care_target", new { scopeType = "reservoir", reservoirId = rig.ReservoirId, metric = Metrics.Ph, minValue = 5.8, maxValue = 7.2 });
        status = await admin.Op("get_reservoir", new { reservoirId = rig.ReservoirId });
        Assert.DoesNotContain(status["alerts"]!.AsArray(), a => a!.ToString().StartsWith("ph"));

        await admin.Op("log_reservoir_event", new { reservoirId = rig.ReservoirId, kind = "top_up", liters = 4 });
        await admin.Op("log_reservoir_event", new { reservoirId = rig.ReservoirId, kind = "top_up", liters = 2.5 });
        Assert.Equal(6.5, (await admin.Op("get_reservoir", new { reservoirId = rig.ReservoirId }))["toppedUpLitersSinceChange"]!.GetValue<double>());
        await admin.Op("log_reservoir_event", new { reservoirId = rig.ReservoirId, kind = "change", liters = 45 });
        var changed = await admin.Op("get_reservoir", new { reservoirId = rig.ReservoirId });
        Assert.Equal(0, changed["toppedUpLitersSinceChange"]!.GetValue<double>());
        Assert.Equal(45, changed["volumeLiters"]!.GetValue<double>());
        Assert.True(changed["daysSinceChange"]!.GetValue<double>() < 0.01);
        Assert.Equal(3, (await admin.Op("list_journal", new { reservoirId = rig.ReservoirId })).AsArray().Count);

        var overview = await admin.Op("get_garden_overview");
        Assert.Contains(overview["reservoirs"]!.AsArray(), r => r!["id"]!.ToString() == rig.ReservoirId.ToString());
        Assert.DoesNotContain(overview["gardenReadings"]!.AsArray(), r => r!["channelId"]!.ToString() == rig.PhChannelId.ToString());
    }

    [Fact]
    public async Task Emergency_stop_can_target_a_reservoir()
    {
        var admin = await f.LoginAsync();
        var rig = await BuildRigAsync(f, admin, "hydro-estop");
        await using var _ = rig.Device;
        var stop = await admin.Op("trigger_emergency_stop", new { scopeType = "reservoir", scopeId = rig.ReservoirId, reason = "reservoir leak" });
        Assert.Equal(4, stop["affectedActuators"]!.GetValue<int>());
        await admin.Op("release_emergency_stop", new { stopId = stop["stop"]!["id"]!.ToString() });
    }
}

public class ScriptedReservoirTests
{
    private static JsonObject Overview(double ph, double ec) => new()
    {
        ["beds"] = new JsonArray(),
        ["actuators"] = new JsonArray(),
        ["reservoirs"] = new JsonArray(new JsonObject
        {
            ["name"] = "Rack", ["volumeLiters"] = 60, ["alerts"] = new JsonArray(),
            ["readings"] = new JsonArray(new JsonObject { ["metric"] = "ph", ["value"] = ph }, new JsonObject { ["metric"] = "ec_ms_cm", ["value"] = ec }),
            ["targets"] = new JsonArray(new JsonObject { ["metric"] = "ph", ["min"] = 5.8, ["max"] = 6.2 }, new JsonObject { ["metric"] = "ec_ms_cm", ["min"] = 1.0, ["max"] = 1.6 }),
            ["actuators"] = new JsonArray(
                new JsonObject { ["id"] = "down", ["role"] = "dose_ph_down" },
                new JsonObject { ["id"] = "a", ["role"] = "dose_nutrient_a" },
                new JsonObject { ["id"] = "b", ["role"] = "dose_nutrient_b" }),
        }),
    };

    private static ScriptedReply Ask(string text, JsonObject overview)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = text } };
        var first = ScriptedModel.Respond(messages);
        messages.Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = ScriptedModel.ToolCallsJson(first.ToolCalls, false) });
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = messages[1]!["tool_calls"]![0]!["id"]!.ToString(), ["content"] = overview.ToJsonString() });
        return ScriptedModel.Respond(messages);
    }

    [Fact]
    public void High_ph_proposes_a_small_ph_down_dose()
    {
        var call = Assert.Single(Ask("How is the reservoir?", Overview(6.9, 1.3)).ToolCalls);
        Assert.Equal("run_actuator", call.Name);
        Assert.Equal("down", call.Arguments["actuatorId"]!.ToString());
        Assert.Equal("dose", call.Arguments["action"]!.ToString());
        Assert.InRange(call.Arguments["amountMl"]!.GetValue<double>(), 1, 8);
    }

    [Fact]
    public void Low_ec_proposes_both_nutrient_parts_and_high_ec_suggests_water()
    {
        var calls = Ask("Check the nutrients", Overview(6.0, 0.7)).ToolCalls;
        Assert.Equal(["a", "b"], calls.Select(c => c.Arguments["actuatorId"]!.ToString()));
        var high = Ask("Check the nutrients", Overview(6.0, 2.2));
        Assert.Empty(high.ToolCalls);
        Assert.Contains("plain water", high.Content);
    }
}
