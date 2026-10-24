using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlopGarden.Contracts;
using SlopGarden.DevLlm;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Ai.Tools;
using SlopGarden.Server.Data;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Tests;

public class ToolRegistryTests
{
    private static ToolRegistry Build()
    {
        var r = new ToolRegistry();
        GardenTools.Register(r);
        DeviceTools.Register(r);
        AutomationTools.Register(r);
        MiscTools.Register(r);
        return r;
    }

    [Fact]
    public void Every_tool_has_snake_case_name_object_schema_and_description()
    {
        var r = Build();
        Assert.True(r.All.Count >= 60);
        foreach (var t in r.All)
        {
            Assert.Matches(new Regex("^[a-z][a-z0-9_]+$"), t.Name);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Equal("object", t.InputSchema["type"]?.ToString());
            Assert.NotNull(t.InputSchema["properties"]);
        }
    }

    [Fact]
    public void Mutating_tools_ask_for_a_reason_and_have_a_preview()
    {
        foreach (var t in Build().All.Where(t => t.Effect == ToolEffect.Mutate))
        {
            Assert.NotNull(t.InputSchema["properties"]?["reason"]);
            Assert.NotNull(t.Preview);
        }
    }

    [Fact]
    public void Credential_tools_are_not_offered_to_the_ai()
    {
        var r = Build();
        foreach (var name in new[] { "approve_device", "reject_device", "revoke_device", "add_device_by_ip" })
            Assert.False(r.Find(name)!.AiAllowed);
        Assert.DoesNotContain(AgentService.BuildToolSchemas(r), t => t?["function"]?["name"]?.ToString() == "approve_device");
    }

    [Fact]
    public void Emergency_stop_needs_no_approval_but_release_does()
    {
        var r = Build();
        Assert.True(r.Find("trigger_emergency_stop")!.NoApprovalNeeded);
        Assert.False(r.Find("release_emergency_stop")!.NoApprovalNeeded);
        Assert.False(r.Find("set_safety_limit")!.NoApprovalNeeded);
    }

    [Fact]
    public void Numbers_in_schemas_are_strict()
    {
        var schema = Build().Find("run_actuator")!.InputSchema;
        var t = schema["properties"]!["durationSec"]!["type"]!.ToJsonString();
        Assert.DoesNotContain("string", t);
    }
}

public class StandingApprovalTests
{
    [Fact]
    public void Max_constraints_bound_numeric_arguments()
    {
        var c = new JsonObject { ["maxDurationSec"] = 60, ["action"] = "pulse" };
        Assert.True(ToolExecutor.WithinConstraints(c, new JsonObject { ["durationSec"] = 45, ["action"] = "pulse" }));
        Assert.False(ToolExecutor.WithinConstraints(c, new JsonObject { ["durationSec"] = 61, ["action"] = "pulse" }));
        Assert.False(ToolExecutor.WithinConstraints(c, new JsonObject { ["durationSec"] = 30, ["action"] = "on" }));
    }
}

public class EnvironmentTests
{
    [Fact]
    public void Time_zone_list_uses_iana_ids_the_server_can_resolve()
    {
        Assert.Contains(TimeZones.Options, z => z.Id == "UTC" || z.Id == "Etc/UTC");
        Assert.Contains(TimeZones.Options, z => z.Id == "Europe/Amsterdam");
        Assert.All(TimeZones.Options, z => Assert.False(string.IsNullOrWhiteSpace(z.Label)));
        Assert.True(TimeZones.IsKnown("America/New_York"));
        Assert.False(TimeZones.IsKnown("Not/AZone"));
        Assert.Equal("EC", Metrics.Label(Metrics.EcMsCm));
        Assert.Equal("pH", Metrics.Label(Metrics.Ph));
        Assert.Equal("custom metric", Metrics.Label("custom_metric"));
    }

    [Theory]
    [InlineData(7, false, "summer")]
    [InlineData(7, true, "winter")]
    [InlineData(10, false, "autumn")]
    [InlineData(4, true, "autumn")]
    public void Season_depends_on_hemisphere(int month, bool south, string expected) =>
        Assert.Equal(expected, EnvironmentService.Season(month, south));

    [Fact]
    public void Daylight_is_long_in_northern_summer_and_short_in_winter()
    {
        var (summer, _) = EnvironmentService.Daylight(52.37, 4.9, 172);
        var (winter, _) = EnvironmentService.Daylight(52.37, 4.9, 355);
        Assert.InRange(summer, 16, 17.5);
        Assert.InRange(winter, 7, 8.5);
    }

    [Fact]
    public void Garden_context_reports_local_time_and_sunrise()
    {
        var g = new Garden { TimezoneId = "Europe/Amsterdam", Latitude = 52.37, Longitude = 4.9, Environment = "outdoor" };
        var ctx = new EnvironmentService().For(g, new DateTimeOffset(2026, 6, 21, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal("summer", ctx.Season);
        Assert.StartsWith("2026-06-21 12:00", ctx.LocalTime);
        Assert.NotNull(ctx.Sunrise);
        Assert.True(ctx.IsDaylight);
    }
}

public class InterlockTests
{
    [Theory]
    [InlineData("lt", 15, 10, true)]
    [InlineData("lt", 15, 20, false)]
    [InlineData("gt", 32, 35, true)]
    [InlineData("gt", 32, 30, false)]
    public void Interlock_trips_on_operator(string op, double threshold, double value, bool tripped) =>
        Assert.Equal(tripped, new SafetyInterlock("x", Guid.Empty, op, threshold, "").IsTripped(value));
}

public class ScriptedModelTests
{
    [Fact]
    public void First_reads_the_overview_then_proposes_watering_for_the_driest_plant()
    {
        var plantId = Guid.NewGuid();
        var bedId = Guid.NewGuid();
        var valveId = Guid.NewGuid();
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Is anything too dry?" } };
        var first = ScriptedModel.Respond(messages);
        Assert.Equal("get_garden_overview", Assert.Single(first.ToolCalls).Name);

        var overview = new JsonObject
        {
            ["beds"] = new JsonArray(new JsonObject
            {
                ["bed"] = new JsonObject { ["id"] = bedId.ToString(), ["name"] = "Tomato bed" },
                ["readings"] = new JsonArray(),
                ["plants"] = new JsonArray(new JsonObject
                {
                    ["plant"] = new JsonObject { ["id"] = plantId.ToString(), ["name"] = "Tomatoes", ["stage"] = "fruiting" },
                    ["readings"] = new JsonArray(new JsonObject { ["metric"] = "soil_moisture_pct", ["value"] = 28.0 }),
                    ["targets"] = new JsonArray(new JsonObject { ["metric"] = "soil_moisture_pct", ["min"] = 45.0, ["max"] = 70.0 }),
                }),
            }),
            ["actuators"] = new JsonArray(new JsonObject { ["id"] = valveId.ToString(), ["name"] = "Tomato valve", ["kind"] = "valve", ["bedId"] = bedId.ToString() }),
        };
        messages.Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = ScriptedModel.ToolCallsJson(first.ToolCalls, false) });
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = messages[1]!["tool_calls"]![0]!["id"]!.ToString(), ["content"] = overview.ToJsonString() });
        var second = ScriptedModel.Respond(messages);
        var call = Assert.Single(second.ToolCalls);
        Assert.Equal("run_actuator", call.Name);
        Assert.Equal(valveId.ToString(), call.Arguments["actuatorId"]?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(call.Arguments["reason"]?.ToString()));
    }
}
