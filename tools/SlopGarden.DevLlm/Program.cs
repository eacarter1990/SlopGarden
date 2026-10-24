using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopGarden.DevLlm;

// A deterministic, offline, OpenAI-compatible "model" for demos and tests. It picks tools by keyword and
// reasons over tool results with simple rules. It is not an LLM; real deployments point SlopGarden at one.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5199");
var app = builder.Build();

app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = ScriptedModel.ModelId, @object = "model", owned_by = "slopgarden" } } }));

app.MapPost("/v1/chat/completions", async (HttpContext http) =>
{
    var req = await JsonNode.ParseAsync(http.Request.Body) as JsonObject ?? new JsonObject();
    var reply = ScriptedModel.Respond(req["messages"] as JsonArray ?? []);
    var stream = req["stream"]?.GetValue<bool>() == true;
    var id = $"chatcmpl-{Guid.NewGuid():N}";

    if (!stream)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
        if (reply.ToolCalls.Count > 0) message["tool_calls"] = ScriptedModel.ToolCallsJson(reply.ToolCalls, false);
        return Results.Json(new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["model"] = ScriptedModel.ModelId,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.ToolCalls.Count > 0 ? "tool_calls" : "stop" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0 },
        });
    }

    http.Response.ContentType = "text/event-stream";
    async Task Send(JsonObject delta, string? finish = null)
    {
        var chunk = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["model"] = ScriptedModel.ModelId,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };
        await http.Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n");
        await http.Response.Body.FlushAsync();
    }

    await Send(new JsonObject { ["role"] = "assistant" });
    foreach (var piece in ScriptedModel.Chunk(reply.Content))
    {
        await Send(new JsonObject { ["content"] = piece });
        await Task.Delay(18);
    }
    if (reply.ToolCalls.Count > 0) await Send(new JsonObject { ["tool_calls"] = ScriptedModel.ToolCallsJson(reply.ToolCalls, true) });
    await Send(new JsonObject(), reply.ToolCalls.Count > 0 ? "tool_calls" : "stop");
    await http.Response.WriteAsync("data: [DONE]\n\n");
    return Results.Empty;
});

app.Run();

namespace SlopGarden.DevLlm
{
    public sealed record ScriptedCall(string Name, JsonObject Arguments);

    public sealed record ScriptedReply(string Content, List<ScriptedCall> ToolCalls);

    public static class ScriptedModel
    {
        public const string ModelId = "slopgarden-demo";
        private const string Footer = "\n\n---\n*Scripted demo model — connect a real model under Settings → AI for open-ended conversation.*";

        public static IEnumerable<string> Chunk(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                sb.Append(ch);
                if (ch == ' ' && sb.Length > 12)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        public static JsonArray ToolCallsJson(List<ScriptedCall> calls, bool withIndex)
        {
            var arr = new JsonArray();
            for (var i = 0; i < calls.Count; i++)
            {
                var o = new JsonObject
                {
                    ["id"] = $"call_{Guid.NewGuid():N}"[..24],
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = calls[i].Name, ["arguments"] = calls[i].Arguments.ToJsonString() },
                };
                if (withIndex) o["index"] = i;
                arr.Add(o);
            }
            return arr;
        }

        public static ScriptedReply Respond(JsonArray messages)
        {
            var lastUserIndex = -1;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var content = Text(messages[i]?["content"]);
                if (messages[i]?["role"]?.ToString() == "user" && !content.StartsWith("[App")) { lastUserIndex = i; break; }
            }
            var userText = lastUserIndex >= 0 ? Text(messages[lastUserIndex]!["content"]) : "";
            var lower = userText.ToLowerInvariant();

            // Tool results produced since the last user message, keyed by tool name.
            var results = new Dictionary<string, JsonNode?>();
            var callNames = new Dictionary<string, string>();
            for (var i = Math.Max(lastUserIndex, 0); i < messages.Count; i++)
            {
                var m = messages[i];
                if (m?["role"]?.ToString() == "assistant" && m["tool_calls"] is JsonArray calls)
                    foreach (var c in calls) callNames[c?["id"]?.ToString() ?? ""] = c?["function"]?["name"]?.ToString() ?? "";
                if (m?["role"]?.ToString() == "tool" && callNames.TryGetValue(m["tool_call_id"]?.ToString() ?? "", out var name))
                {
                    try { results[name] = JsonNode.Parse(Text(m["content"])); }
                    catch (JsonException) { results[name] = null; }
                }
            }

            var lastEvent = messages.LastOrDefault(m => m?["role"]?.ToString() == "user" && Text(m["content"]).StartsWith("[App event"));
            if (lastUserIndex < 0 && lastEvent is not null) return new ScriptedReply("Noted." + Footer, []);

            if (Has(lower, "emergency", "stop everything", "e-stop", "estop"))
                return results.ContainsKey("trigger_emergency_stop")
                    ? new ScriptedReply("🛑 **Emergency stop is active.** Every actuator is switched off and locked until someone releases the stop on the **Safety** page." + Footer, [])
                    : Call("trigger_emergency_stop", new JsonObject { ["scopeType"] = "global", ["reason"] = "User asked for an emergency stop from chat" }, "Stopping everything now.");

            if (!results.TryGetValue("get_garden_overview", out var overview))
                return Call("get_garden_overview", new JsonObject(), "Let me look at the garden first.");
            if (overview is null) return new ScriptedReply("I couldn't read the garden overview." + Footer, []);

            if (Has(lower, "photo", "picture", "camera", "look at", "see the", "image"))
            {
                var cam = (overview["cameras"] as JsonArray ?? []).FirstOrDefault();
                if (cam is null) return new ScriptedReply("There are no cameras configured yet. Adopt one from **Devices → discovered hardware**." + Footer, []);
                if (!results.TryGetValue("capture_camera_image", out var cap))
                    return Call("capture_camera_image", new JsonObject { ["cameraId"] = cam["id"]?.ToString(), ["width"] = 800 }, $"Capturing a still from **{cam["name"]}**.");
                return new ScriptedReply($"Here's a fresh still from **{cam["name"]}** ({cap?["width"]}×{cap?["height"]}). [Open image]({cap?["imageUrl"]})\n\nA vision-capable model would describe leaf colour, wilting and fruit set here; this scripted demo model can't see images." + Footer, []);
            }

            if (Has(lower, "schedule", "every morning", "daily", "automate"))
            {
                var (plant, bed, actuator, _, _) = Driest(overview);
                if (actuator is null) return new ScriptedReply("I couldn't find a valve or pump to schedule." + Footer, []);
                if (results.TryGetValue("create_schedule", out var sched)) return Proposed(sched, $"a daily 07:00 watering of **{actuator["name"]}**");
                return Call("create_schedule", new JsonObject
                {
                    ["name"] = $"Morning watering – {bed?["bed"]?["name"] ?? actuator["name"]}",
                    ["kind"] = "actuator",
                    ["cron"] = "0 7 * * *",
                    ["actuatorId"] = actuator["id"]?.ToString(),
                    ["durationSec"] = 90,
                    ["reason"] = $"{plant?["plant"]?["name"] ?? "These plants"} dry out by the afternoon; a 90 s morning drip keeps soil moisture in range before the heat. Runs on the device, so it keeps working offline, and stays within the daily safety limit.",
                }, "I'll propose a morning watering schedule.");
            }

            if (Has(lower, "target", "range", "threshold"))
            {
                var (plant, _, _, _, _) = Driest(overview);
                var p = plant ?? (overview["beds"] as JsonArray ?? []).SelectMany(b => b?["plants"] as JsonArray ?? []).FirstOrDefault();
                if (p is null) return new ScriptedReply("There are no plants yet." + Footer, []);
                if (results.TryGetValue("set_care_target", out var t)) return Proposed(t, $"a soil-moisture target of 50–70 % for **{p["plant"]?["name"]}**");
                return Call("set_care_target", new JsonObject
                {
                    ["scopeType"] = "plant", ["plantId"] = p["plant"]?["id"]?.ToString(), ["metric"] = "soil_moisture_pct", ["minValue"] = 50, ["maxValue"] = 70,
                    ["rationale"] = "Fruiting stage: steady moisture reduces blossom-end rot and splitting.",
                    ["reason"] = "Fruiting tomatoes need steadier moisture than the generic profile range; tightening to 50–70 % reduces blossom-end rot and fruit splitting.",
                }, "I'll propose a tighter moisture range.");
            }

            if (Has(lower, "water", "dry", "thirsty", "moisture", "irrigat"))
            {
                var (plant, bed, actuator, value, min) = Driest(overview);
                if (plant is null)
                    return new ScriptedReply("All soil-moisture readings are within their targets right now, so no watering is needed. 💧" + Summary(overview) + Footer, []);
                if (actuator is null)
                    return new ScriptedReply($"**{plant["plant"]?["name"]}** is dry ({value:0.#} % vs. target ≥ {min:0} %), but there's no valve or pump serving it yet." + Footer, []);
                if (results.TryGetValue("run_actuator", out var run))
                {
                    if (run?["status"]?.ToString() == "error") return new ScriptedReply($"I couldn't water: {run["error"]}" + Footer, []);
                    if (run?["status"]?.ToString() == "pending_approval")
                        return new ScriptedReply($"**{plant["plant"]?["name"]}** in *{bed?["bed"]?["name"]}* is at **{value:0.#} %** soil moisture, below its **{min:0} %** minimum.\n\nI've proposed running **{actuator["name"]}** for **60 s** — approve it above. Tick *always allow* if I may repeat this kind of top-up on my own (always within the safety limits)." + Footer, []);
                    return new ScriptedReply($"Watering **{plant["plant"]?["name"]}** now with **{actuator["name"]}** for 60 s (already approved routine). I'll check the moisture again afterwards." + Footer, []);
                }
                return Call("run_actuator", new JsonObject
                {
                    ["actuatorId"] = actuator["id"]?.ToString(),
                    ["action"] = "pulse",
                    ["durationSec"] = 60,
                    ["reason"] = $"{plant["plant"]?["name"]} soil moisture is {value:0.#} %, below the {min:0} % minimum for its stage; a 60 s drip brings it back into range.",
                }, "");
            }

            if (Has(lower, "hello", "hi ", "hey", "help", "what can you"))
                return new ScriptedReply("Hi! I'm the SlopGarden assistant. I can check on your plants, water them, set care targets, create schedules, take a camera snapshot, or hit the emergency stop.\n\nTry: *\"How is the garden doing?\"*, *\"Is anything too dry?\"*, *\"Water the tomatoes every morning\"*, or *\"Show me the tomato camera\"*." + Footer, []);

            return new ScriptedReply("Here's how the garden looks right now:" + Summary(overview) + Footer, []);
        }

        private static ScriptedReply Call(string name, JsonObject args, string text) => new(text, [new ScriptedCall(name, args)]);

        private static ScriptedReply Proposed(JsonNode? result, string what) =>
            result?["status"]?.ToString() == "error"
                ? new ScriptedReply($"That didn't work: {result["error"]}" + Footer, [])
                : new ScriptedReply($"I've proposed {what}. Review the change above and approve or reject it." + Footer, []);

        private static bool Has(string text, params string[] words) => words.Any(text.Contains);

        private static string Text(JsonNode? content) => content switch
        {
            JsonValue v => v.ToString(),
            JsonArray parts => string.Join(" ", parts.Select(p => p?["text"]?.ToString() ?? "")),
            _ => "",
        };

        private static double? MoistureOf(JsonNode? readings) =>
            (readings as JsonArray ?? []).Where(r => r?["metric"]?.ToString() == "soil_moisture_pct" && r["value"] is not null).Select(r => (double?)r!["value"]!.GetValue<double>()).FirstOrDefault();

        /// <summary>The plant furthest below its soil-moisture minimum, its bed, and the valve/pump serving it.</summary>
        public static (JsonNode? Plant, JsonNode? Bed, JsonNode? Actuator, double Value, double Min) Driest(JsonNode overview)
        {
            var actuators = overview["actuators"] as JsonArray ?? [];
            (JsonNode? p, JsonNode? b, double deficit, double v, double min) best = (null, null, 0, 0, 0);
            JsonNode? anyBed = null, anyActuator = null;
            foreach (var bed in overview["beds"] as JsonArray ?? [])
            {
                anyBed ??= bed;
                var bedMoisture = MoistureOf(bed?["readings"]);
                foreach (var plant in bed?["plants"] as JsonArray ?? [])
                {
                    var value = MoistureOf(plant?["readings"]) ?? bedMoisture;
                    var min = (plant?["targets"] as JsonArray ?? []).Where(t => t?["metric"]?.ToString() == "soil_moisture_pct").Select(t => t?["min"]?.GetValue<double>()).FirstOrDefault();
                    if (value is null || min is null) continue;
                    var deficit = min.Value - value.Value;
                    if (deficit > best.deficit) best = (plant, bed, deficit, value.Value, min.Value);
                }
            }
            JsonNode? Serving(JsonNode? plant, JsonNode? bed) => actuators.FirstOrDefault(a =>
                a?["kind"]?.ToString() is "valve" or "pump" or "mister" &&
                ((plant is not null && a["plantId"]?.ToString() == plant["plant"]?["id"]?.ToString()) || (bed is not null && a["bedId"]?.ToString() == bed["bed"]?["id"]?.ToString())));
            if (best.p is null)
            {
                anyActuator = actuators.FirstOrDefault(a => a?["kind"]?.ToString() is "valve" or "pump");
                return (null, anyBed, anyActuator, 0, 0);
            }
            return (best.p, best.b, Serving(best.p, best.b), best.v, best.min);
        }

        private static string Summary(JsonNode overview)
        {
            var sb = new StringBuilder("\n\n| Bed | Plant | Stage | Soil moisture | Target |\n|---|---|---|---|---|\n");
            foreach (var bed in overview["beds"] as JsonArray ?? [])
            {
                var bedMoisture = MoistureOf(bed?["readings"]);
                foreach (var plant in bed?["plants"] as JsonArray ?? [])
                {
                    var v = MoistureOf(plant?["readings"]) ?? bedMoisture;
                    var t = (plant?["targets"] as JsonArray ?? []).FirstOrDefault(x => x?["metric"]?.ToString() == "soil_moisture_pct");
                    var status = v is null || t is null ? "" : v < t["min"]?.GetValue<double>() ? " 🔴" : v > t["max"]?.GetValue<double>() ? " 🔵" : " 🟢";
                    sb.Append($"| {bed?["bed"]?["name"]} | {plant?["plant"]?["name"]} | {plant?["plant"]?["stage"]} | {(v is null ? "–" : $"{v:0.#} %")}{status} | {(t is null ? "–" : $"{t["min"]}–{t["max"]} %")} |\n");
                }
            }
            var env = overview["environment"];
            var on = (overview["actuators"] as JsonArray ?? []).Where(a => a?["isOn"]?.GetValue<bool>() == true).Select(a => a?["name"]?.ToString()).ToList();
            sb.Append($"\n**{env?["season"]}**, {env?["localTime"]}, {env?["daylightHours"]} h of daylight. ");
            sb.Append(on.Count == 0 ? "All actuators are off." : $"Running: {string.Join(", ", on)}.");
            var stops = overview["activeEmergencyStops"] as JsonArray ?? [];
            if (stops.Count > 0) sb.Append($"\n\n🛑 {stops.Count} emergency stop(s) active.");
            return sb.ToString();
        }
    }
}
