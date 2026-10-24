using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Endpoints;

public static class RemoteSetupEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/devices/remote-setup").WithTags("Devices").RequireAuthorization(Policies.Admin);

        g.MapPost("/probe", async (RemoteSetupRequest req, RemoteSetupService setup, SlopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var result = await setup.ProbeAsync(new RemoteSetupTarget(req.Host, req.Port, req.Username, req.Password, req.PrivateKey), ct);
            db.Audit(http.Actor(), "device.remote_setup.probe", "device", null, new() { ["host"] = req.Host, ["fingerprint"] = result.Fingerprint, ["docker"] = result.DockerInstalled });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { result.SessionId, result.Fingerprint, result.DockerInstalled, result.ServerUrl, result.Detail });
        });

        g.MapPost("/{sessionId}/install", async (string sessionId, RemoteInstallRequest req, RemoteSetupService setup, SlopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var log = await setup.InstallAsync(sessionId, req.Fingerprint, req.Name, req.Image, ct);
            db.Audit(http.Actor(), "device.remote_setup.install", "device", null, new() { ["name"] = req.Name });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { log, pending = true });
        });
    }
}

public sealed record RemoteSetupRequest(string Host, int Port, string Username, string? Password, string? PrivateKey);
public sealed record RemoteInstallRequest(string Fingerprint, string Name, string? Image);
