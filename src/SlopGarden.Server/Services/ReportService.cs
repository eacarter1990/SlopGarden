using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record GrowthReportDto(
    Guid Id, Guid GardenId, string Kind, string Title, string Body, JsonObject Conditions, JsonArray Predictions,
    JsonArray ImageIds, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, DateTimeOffset CreatedAt);

/// <summary>
/// Builds a growth report from current readings, camera frames and harvest dates.
/// A configured reports (or chat) model writes the narrative; otherwise a structured template is stored.
/// </summary>
public sealed class ReportService(
    SlopDbContext db,
    CameraFrameStore frames,
    NotificationService notifications,
    ActivityLog activity,
    OpenAiClient ai,
    IServiceProvider services,
    ILogger<ReportService> log)
{
    public async Task<GrowthReportDto> GenerateAsync(Guid gardenId, string kind, Guid? scheduleId, CancellationToken ct)
    {
        if (!ReportCadence.Scheduled.Contains(kind) && kind != ReportCadence.OnDemand)
            throw Infrastructure.AppException.Bad("kind must be daily, weekly, monthly or on_demand");
        var garden = await db.Gardens.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gardenId, ct)
            ?? throw Infrastructure.AppException.NotFound("Garden");
        var now = DateTimeOffset.UtcNow;
        var period = kind switch
        {
            ReportCadence.Weekly => TimeSpan.FromDays(7),
            ReportCadence.Monthly => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1),
        };

        var beds = await db.Beds.AsNoTracking().Where(b => b.GardenId == gardenId && b.ArchivedAt == null).OrderBy(b => b.Name).ToListAsync(ct);
        var plants = await db.Plants.AsNoTracking().Include(p => p.PlantProfile).Include(p => p.Bed)
            .Where(p => p.Bed!.GardenId == gardenId && p.RemovedAt == null).OrderBy(p => p.Name).ToListAsync(ct);
        var channels = await db.SensorChannels.AsNoTracking().Include(c => c.Sensor)
            .Where(c => c.Sensor!.GardenId == gardenId && c.LastValue != null).ToListAsync(ct);
        var cameras = await db.Cameras.AsNoTracking().Where(c => c.GardenId == gardenId && c.Enabled).OrderBy(c => c.Name).ToListAsync(ct);

        var conditions = new JsonObject
        {
            ["garden"] = garden.Name,
            ["timezone"] = garden.TimezoneId,
            ["generatedAt"] = now.ToString("O"),
            ["beds"] = new JsonArray(beds.Select(b => (JsonNode)new JsonObject
            {
                ["name"] = b.Name,
                ["medium"] = b.GrowingMedium ?? garden.DefaultGrowingMedium,
                ["hydroSystem"] = b.HydroSystem,
                ["readings"] = new JsonArray(channels.Where(c => c.Sensor!.BedId == b.Id && c.Sensor.PlantId == null).Select(ReadingNode).ToArray()),
            }).ToArray()),
        };

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var predictions = new JsonArray();
        foreach (var p in plants)
        {
            var start = p.ExpectedHarvestStart;
            int? days = start is { } s ? s.DayNumber - today.DayNumber : null;
            var summary = days switch
            {
                null => "No harvest date is set yet.",
                < 0 when p.ExpectedHarvestEnd is { } end && end < today => "The expected harvest window has passed.",
                < 0 => $"Harvest window opened {-days} days ago{(p.ExpectedHarvestEnd is { } end ? $", through {end:yyyy-MM-dd}" : "")}.",
                0 => "Harvest window opens today.",
                _ => $"Harvest window opens in {days} days ({start:yyyy-MM-dd}).",
            };
            predictions.Add(new JsonObject
            {
                ["plantId"] = p.Id.ToString(),
                ["plant"] = p.Name,
                ["bed"] = p.Bed?.Name,
                ["stage"] = p.Stage,
                ["variety"] = p.Variety,
                ["expectedStart"] = start?.ToString("yyyy-MM-dd"),
                ["expectedEnd"] = p.ExpectedHarvestEnd?.ToString("yyyy-MM-dd"),
                ["summary"] = summary,
            });
        }

        var imageIds = new JsonArray();
        foreach (var cam in cameras.Take(12))
        {
            var live = frames.Latest(cam.Id);
            Media? media = null;
            if (live is not null)
            {
                media = new Media
                {
                    Kind = "growth_report", CameraId = cam.Id, GardenId = cam.GardenId, BedId = cam.BedId, PlantId = cam.PlantId,
                    ContentType = live.ContentType, Width = live.Width, Height = live.Height, SizeBytes = live.Data.Length,
                    Data = live.Data, CapturedAt = live.CapturedAt,
                };
                db.Media.Add(media);
            }
            else
            {
                media = await db.Media.AsNoTracking().Where(m => m.CameraId == cam.Id).OrderByDescending(m => m.CapturedAt).FirstOrDefaultAsync(ct);
            }
            if (media is null) continue;
            imageIds.Add(new JsonObject { ["id"] = media.Id.ToString(), ["camera"] = cam.Name, ["role"] = cam.TargetType, ["capturedAt"] = (live?.CapturedAt ?? media.CapturedAt).ToString("O") });
        }
        await db.SaveChangesAsync(ct);

        var facts = Facts(garden.Name, kind, conditions, predictions, imageIds);
        var narrative = await NarrateAsync(facts, ct);
        var body = string.IsNullOrWhiteSpace(narrative) ? Template(garden.Name, kind, conditions, predictions, imageIds) : narrative.Trim();

        var report = new GrowthReport
        {
            GardenId = gardenId,
            Kind = kind,
            Title = $"{ReportCadence.Title(kind)} — {garden.Name}",
            Body = body,
            Conditions = conditions,
            Predictions = predictions,
            ImageIds = imageIds,
            PeriodStart = now - period,
            PeriodEnd = now,
            ScheduleId = scheduleId,
        };
        db.GrowthReports.Add(report);
        await db.SaveChangesAsync(ct);
        activity.Garden(gardenId, $"Growth report ({kind}): {report.Title}");
        await notifications.CreateAsync(new NotificationRequest(gardenId, Severities.Info, "report", report.Title, "A growth report is ready.", "garden", gardenId, $"report:{gardenId}:{kind}:{now:yyyyMMddHH}"), ct);
        return ToDto(report);
    }

    public async Task EnsureScheduleAsync(Guid gardenId, string cadence, bool enabled, Guid? userId, CancellationToken ct)
    {
        if (!ReportCadence.Scheduled.Contains(cadence)) throw Infrastructure.AppException.Bad("cadence must be daily, weekly or monthly");
        var garden = await db.Gardens.FirstOrDefaultAsync(g => g.Id == gardenId, ct) ?? throw Infrastructure.AppException.NotFound("Garden");
        var name = ReportCadence.Title(cadence);
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.GardenId == gardenId && s.Kind == ScheduleKinds.Report && s.Name == name, ct);
        if (schedule is null)
        {
            schedule = new Schedule
            {
                GardenId = gardenId, Name = name, Kind = ScheduleKinds.Report, Cron = ReportCadence.Cron(cadence),
                TimezoneId = garden.TimezoneId, Enabled = enabled, CreatedById = userId,
                Payload = new JsonObject { ["cadence"] = cadence },
            };
            db.Schedules.Add(schedule);
        }
        else
        {
            schedule.Enabled = enabled;
            schedule.Cron = ReportCadence.Cron(cadence);
            schedule.Payload["cadence"] = cadence;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> NarrateAsync(string facts, CancellationToken ct)
    {
        try
        {
            var endpoint = await AgentService.ResolveEndpointAsync(services, AiJobs.Reports, ct);
            if (endpoint is null) return null;
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You write a short garden growth report from the facts provided. Cover current conditions and harvest predictions. Do not propose actuator commands, doses, schedules, or setting changes. Do not invent readings." },
                new JsonObject { ["role"] = "user", ["content"] = facts },
            };
            var turn = await ai.CompleteAsync(endpoint, messages, null, null, ct);
            if (turn.ToolCalls.Count > 0 || string.IsNullOrWhiteSpace(turn.Content)) return null;
            return turn.Content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Growth report narrative failed; storing the structured template");
            return null;
        }
    }

    private static string Facts(string garden, string kind, JsonObject conditions, JsonArray predictions, JsonArray images)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Garden: {garden}");
        sb.AppendLine($"Report: {kind}");
        sb.AppendLine("Conditions:");
        sb.AppendLine(conditions.ToJsonString());
        sb.AppendLine("Harvest predictions:");
        sb.AppendLine(predictions.ToJsonString());
        sb.AppendLine($"Camera frames attached: {images.Count}");
        return sb.ToString();
    }

    public static string Template(string garden, string kind, JsonObject conditions, JsonArray predictions, JsonArray images)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ReportCadence.Title(kind)}");
        sb.AppendLine();
        sb.AppendLine($"**{garden}** — generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC.");
        sb.AppendLine();
        sb.AppendLine("## Current conditions");
        if (conditions["beds"] is JsonArray beds)
        {
            foreach (var bed in beds)
            {
                sb.AppendLine();
                sb.AppendLine($"### {bed?["name"]}");
                var medium = bed?["medium"]?.ToString();
                var hydro = bed?["hydroSystem"]?.ToString();
                sb.AppendLine(string.IsNullOrEmpty(hydro) ? $"Medium: {medium}." : $"Medium: {medium} ({hydro}).");
                if (bed?["readings"] is JsonArray readings && readings.Count > 0)
                {
                    foreach (var r in readings)
                        sb.AppendLine($"- {r?["name"]}: {r?["value"]} {r?["unit"]}");
                }
                else sb.AppendLine("No fresh readings for this bed.");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Harvest predictions");
        if (predictions.Count == 0) sb.AppendLine("No plants with a harvest window.");
        foreach (var p in predictions)
            sb.AppendLine($"- **{p?["plant"]}** ({p?["bed"]}, {p?["stage"]}): {p?["summary"]}");
        sb.AppendLine();
        sb.AppendLine(images.Count == 0 ? "No camera frames were available." : $"{images.Count} camera frame(s) are attached to this report.");
        return sb.ToString();
    }

    public static GrowthReportDto ToDto(GrowthReport r) => new(r.Id, r.GardenId, r.Kind, r.Title, r.Body, r.Conditions, r.Predictions, r.ImageIds, r.PeriodStart, r.PeriodEnd, r.CreatedAt);

    private static JsonObject ReadingNode(SensorChannel c) => new()
    {
        ["name"] = string.IsNullOrWhiteSpace(c.DisplayName) ? c.Metric : c.DisplayName,
        ["metric"] = c.Metric,
        ["value"] = c.LastValue,
        ["unit"] = c.Unit,
        ["at"] = c.LastValueAt?.ToString("O"),
    };
}
