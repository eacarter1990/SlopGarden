using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Endpoints;

public static class MilestoneEndpoints
{
    public static void Map(WebApplication app)
    {
        MapStream(app);
        MapReports(app);
        MapLogs(app);
        MapUpdates(app);
    }

    private static void MapStream(WebApplication app)
    {
        app.MapGet("/api/cameras/{id:guid}/stream", async (Guid id, int? width, CameraFrameStore store, HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = Mjpeg.ContentType;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            var lastSent = DateTimeOffset.MinValue;
            byte[]? last = null;
            while (!ct.IsCancellationRequested)
            {
                var frame = store.Scaled(id, width);
                var changed = frame is not null && (last is null || !frame.Data.AsSpan().SequenceEqual(last));
                var repeat = frame is not null && DateTimeOffset.UtcNow - lastSent >= TimeSpan.FromSeconds(1);
                if (frame is not null && (changed || repeat))
                {
                    await Mjpeg.WriteFrameAsync(http.Response.Body, frame.Data, ct);
                    last = frame.Data;
                    lastSent = DateTimeOffset.UtcNow;
                }
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Cameras");
    }

    private static void MapReports(WebApplication app)
    {
        var g = app.MapGroup("/api/gardens/{gardenId:guid}/reports").WithTags("Reports").RequireAuthorization(Policies.ReadOnly);

        g.MapGet("/", async (Guid gardenId, SlopDbContext db) =>
            (await db.GrowthReports.AsNoTracking().Where(r => r.GardenId == gardenId).OrderByDescending(r => r.CreatedAt).Take(60).ToListAsync())
                .Select(ReportService.ToDto));

        g.MapGet("/schedules", async (Guid gardenId, SlopDbContext db) =>
        {
            var rows = await db.Schedules.AsNoTracking().Where(s => s.GardenId == gardenId && s.Kind == ScheduleKinds.Report).ToListAsync();
            return rows.Select(s => new
            {
                s.Id, s.Name, s.Cron, s.Enabled,
                cadence = s.Payload["cadence"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var c) ? c : null,
            });
        });

        g.MapPost("/", async (Guid gardenId, ReportRequest req, ReportService reports, CancellationToken ct) =>
        {
            var kind = string.IsNullOrWhiteSpace(req.Kind) ? ReportCadence.OnDemand : req.Kind;
            return Results.Ok(await reports.GenerateAsync(gardenId, kind, null, ct));
        }).RequireAuthorization(Policies.ReadWrite);

        g.MapPost("/schedule", async (Guid gardenId, ReportScheduleRequest req, ReportService reports, HttpContext http, CancellationToken ct) =>
        {
            await reports.EnsureScheduleAsync(gardenId, req.Cadence, req.Enabled, http.Actor().UserId, ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.ReadWrite);
    }

    private static void MapLogs(WebApplication app)
    {
        app.MapGet("/api/logs", async (string scope, string? id, int? lines, ActivityLog logs, SlopDbContext db) =>
        {
            if (scope is not ("server" or "garden" or "bed" or "plant" or "client"))
                return Results.BadRequest(new { error = "scope must be server, garden, bed, plant or client" });
            var text = logs.Read(scope, id, lines ?? 200);
            string? title = scope switch
            {
                "garden" when Guid.TryParse(id, out var g) => await db.Gardens.Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(),
                "bed" when Guid.TryParse(id, out var b) => await db.Beds.Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(),
                "plant" when Guid.TryParse(id, out var p) => await db.Plants.Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(),
                "client" when Guid.TryParse(id, out var d) => await db.Devices.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(),
                "server" => "Server",
                _ => null,
            };
            return Results.Ok(new { scope, id, title, text });
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Logs");

        app.MapGet("/api/logs/scopes", async (SlopDbContext db) =>
        {
            var gardens = await db.Gardens.AsNoTracking().Where(g => g.ArchivedAt == null).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync();
            var beds = await db.Beds.AsNoTracking().Where(b => b.ArchivedAt == null).OrderBy(b => b.Name).Select(b => new { b.Id, b.Name, b.GardenId }).ToListAsync();
            var plants = await db.Plants.AsNoTracking().Where(p => p.RemovedAt == null).OrderBy(p => p.Name).Select(p => new { p.Id, p.Name, p.BedId }).ToListAsync();
            var devices = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync();
            return Results.Ok(new { gardens, beds, plants, devices });
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Logs");

        app.MapPost("/api/device-api/logs", async (HttpContext http, ActivityLog logs, CancellationToken ct) =>
        {
            var deviceId = Guid.Parse(http.User.FindFirstValue(Claims.DeviceId)!);
            using var reader = new StreamReader(http.Request.Body);
            var text = await reader.ReadToEndAsync(ct);
            if (text.Length > 256 * 1024) text = text[^ (256 * 1024)..];
            logs.Client(deviceId, text);
            return Results.Ok();
        }).RequireAuthorization(Policies.Device).WithTags("Device API");
    }

    private static void MapUpdates(WebApplication app)
    {
        app.MapGet("/api/client-release", async (SlopDbContext db, CancellationToken ct) =>
            Results.Ok(await ClientUpdateService.ReadAsync(db, ct))).RequireAuthorization(Policies.ReadOnly).WithTags("Devices");

        app.MapPut("/api/client-release", async (ClientReleaseInfo body, SlopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Version)) return Results.BadRequest(new { error = "Version is required" });
            await ClientUpdateService.SaveAsync(db, body.Version, body.Image, http.Actor().UserId, ct);
            return Results.Ok(await ClientUpdateService.ReadAsync(db, ct));
        }).RequireAuthorization(Policies.Admin).WithTags("Devices");

        app.MapPost("/api/devices/{id:guid}/update", async (Guid id, ClientUpdateService updates, CancellationToken ct) =>
        {
            var note = await updates.PushAsync(id, manual: true, ct);
            return Results.Ok(new { started = note is null, message = note ?? "Update sent" });
        }).RequireAuthorization(Policies.Admin).WithTags("Devices");

        app.MapPut("/api/devices/{id:guid}/auto-update", async (Guid id, AutoUpdateRequest req, SlopDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw AppException.NotFound("Device");
            device.AutoUpdate = req.Enabled;
            if (!req.Enabled && device.UpdateStatus == "updating") device.UpdateStatus = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { device.Id, device.AutoUpdate });
        }).RequireAuthorization(Policies.Admin).WithTags("Devices");
    }
}

public sealed record ReportRequest(string? Kind);
public sealed record ReportScheduleRequest(string Cadence, bool Enabled);
public sealed record AutoUpdateRequest(bool Enabled);
