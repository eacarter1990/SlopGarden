using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopGarden.DevLlm;

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

        if (Has(lower, "reservoir", "nutrient", "hydro", "nft", " ph", "ph ", " ec", "ec ", "dose", "pump", "flow rate", "no flow", "flow stall"))
            return Reservoir(overview, results);

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

    private static double? ReadingOf(JsonNode? reservoir, string metric) =>
        (reservoir?["readings"] as JsonArray ?? []).Where(r => r?["metric"]?.ToString() == metric && r["value"] is not null).Select(r => (double?)r!["value"]!.GetValue<double>()).FirstOrDefault();

    private static (double? Min, double? Max) TargetOf(JsonNode? reservoir, string metric)
    {
        var t = (reservoir?["targets"] as JsonArray ?? []).FirstOrDefault(x => x?["metric"]?.ToString() == metric);
        return (t?["min"]?.GetValue<double?>(), t?["max"]?.GetValue<double?>());
    }

    private static JsonNode? Doser(JsonNode reservoir, string role) =>
        (reservoir["actuators"] as JsonArray ?? []).FirstOrDefault(a => a?["role"]?.ToString() == role);

    /// <summary>Hydroponic reservoir check: flow problems first, then pH, then EC. Doses are always proposals.</summary>
    private static ScriptedReply Reservoir(JsonNode overview, Dictionary<string, JsonNode?> results)
    {
        var reservoirs = overview["reservoirs"] as JsonArray ?? [];
        if (reservoirs.Count == 0) return new ScriptedReply("There's no hydroponic reservoir set up yet. Create one in **Garden → Reservoirs** and point a bed's growing medium to *Hydroponic*." + Footer, []);
        var res = reservoirs.FirstOrDefault(r => (r?["alerts"] as JsonArray)?.Count > 0) ?? reservoirs[0]!;
        var name = res!["name"]?.ToString();
        var volume = res["volumeLiters"]?.GetValue<double?>() ?? 50;
        var alerts = (res["alerts"] as JsonArray ?? []).Select(a => a?.ToString() ?? "").ToList();

        var proposed = results.Where(r => r.Key == "run_actuator").Select(r => r.Value).ToList();
        if (proposed.Count > 0)
        {
            var failed = proposed.FirstOrDefault(r => r?["status"]?.ToString() == "error");
            if (failed is not null) return new ScriptedReply($"I couldn't propose that dose: {failed["error"]}" + Footer, []);
            return new ScriptedReply($"I've proposed the dose above for **{name}**. Dosing always needs your approval. After it runs, give the reservoir about 10 minutes to mix before I check again." + Footer, []);
        }

        var flowAlert = alerts.FirstOrDefault(a => a.Contains("flow stall") || a.Contains("should run continuously"));
        if (flowAlert is not null)
            return new ScriptedReply($"🚨 **{name}: {flowAlert}.** In NFT and aeroponic systems roots can dry out quickly. Check the pump, the intake filter and the reservoir level now. Use the emergency stop if water is leaking." + Footer, []);

        var ph = ReadingOf(res, "ph");
        var (phMin, phMax) = TargetOf(res, "ph");
        if (ph is { } p && phMax is { } hi && p > hi && Doser(res, "dose_ph_down") is { } down)
        {
            var ml = Math.Clamp(Math.Round((p - ((phMin ?? hi) + hi) / 2) / 0.4 * volume / 10, MidpointRounding.AwayFromZero), 1, 8);
            return new ScriptedReply($"pH in **{name}** is **{p:0.00}**, above the {phMin}–{hi} target.", [new ScriptedCall("run_actuator", new JsonObject
            {
                ["actuatorId"] = down["id"]?.ToString(), ["action"] = "dose", ["amountMl"] = ml,
                ["reason"] = $"pH {p:0.00} is above the {hi} maximum; high pH locks out iron and manganese. {ml} ml pH Down in {volume:0} L should bring it back toward {((phMin ?? hi) + hi) / 2:0.0}. Re-check after mixing.",
            })]);
        }
        if (ph is { } p2 && phMin is { } lo && p2 < lo && Doser(res, "dose_ph_up") is { } up)
        {
            var ml = Math.Clamp(Math.Round((((phMax ?? lo) + lo) / 2 - p2) / 0.4 * volume / 10, MidpointRounding.AwayFromZero), 1, 8);
            return new ScriptedReply($"pH in **{name}** is **{p2:0.00}**, below the {lo}–{phMax} target.", [new ScriptedCall("run_actuator", new JsonObject
            {
                ["actuatorId"] = up["id"]?.ToString(), ["action"] = "dose", ["amountMl"] = ml,
                ["reason"] = $"pH {p2:0.00} is below the {lo} minimum. {ml} ml pH Up in {volume:0} L should lift it toward the middle of the range. Re-check after mixing.",
            })]);
        }

        var ec = ReadingOf(res, "ec_ms_cm");
        var (ecMin, ecMax) = TargetOf(res, "ec_ms_cm");
        if (ec is { } e && ecMin is { } emin && e < emin && Doser(res, "dose_nutrient_a") is { } a && Doser(res, "dose_nutrient_b") is { } b)
        {
            var ml = Math.Clamp(Math.Round((emin + 0.1 - e) / 0.1 * 0.15 * volume * 2, MidpointRounding.AwayFromZero) / 2, 2, 10);
            var why = $"EC {e:0.00} mS/cm is below the {emin} minimum, so the plants are underfed. {ml} ml each of part A and B in {volume:0} L raises EC by roughly {ml / volume / 0.15 * 0.1:0.0#}.";
            return new ScriptedReply($"EC in **{name}** is **{e:0.00} mS/cm**, below the {emin}–{ecMax} target.", [
                new ScriptedCall("run_actuator", new JsonObject { ["actuatorId"] = a["id"]?.ToString(), ["action"] = "dose", ["amountMl"] = ml, ["reason"] = why }),
                new ScriptedCall("run_actuator", new JsonObject { ["actuatorId"] = b["id"]?.ToString(), ["action"] = "dose", ["amountMl"] = ml, ["reason"] = why + " Part B follows part A in the same mixing window." }),
            ]);
        }
        if (ec is { } e2 && ecMax is { } emax && e2 > emax)
        {
            var liters = Math.Round(volume * (e2 - emax) / e2, 0);
            return new ScriptedReply($"EC in **{name}** is **{e2:0.00} mS/cm**, above the {ecMin}–{emax} target. That usually means the plants drank more water than nutrients. I don't dose for this: top up with about **{liters:0} L of plain water** (pH-adjusted), then log it with *Log top-up* on the reservoir page." + Footer, []);
        }

        var temp = ReadingOf(res, "water_temperature_c");
        var level = ReadingOf(res, "water_level_pct");
        return new ScriptedReply($"**{name}** looks good: pH {ph:0.00}, EC {ec:0.00} mS/cm, water {temp:0.#} °C, level {level:0} %. Solution changed {res["daysSinceChange"]} days ago (every {res["changeIntervalDays"]} days)." + Footer, []);
    }

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
