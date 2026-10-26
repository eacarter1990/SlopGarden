using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Npgsql;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Endpoints;

public sealed record LoginRequest(string Username, string Password);
public sealed record SetupRequest(string Username, string Password, string? DisplayName);
public sealed record ProposalDecision(string? Note, bool AllowAlways = false);
public sealed record NewConversation(string? Title);
public sealed record PostMessage(string Content, PageContext? Page);

public static class CoreEndpoints
{
    public static void Map(WebApplication app)
    {
        MapAuth(app);
        MapOps(app);
        MapChat(app);
        MapProposals(app);
        MapMedia(app);
        MapDeviceApi(app);
    }

    private static void MapAuth(WebApplication app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapGet("/status", async (SlopDbContext db, HttpContext http, IOptions<ServerOptions> opts, DatabaseStartup startup) =>
        {
            var database = opts.Value.Database;
            var info = new { database.Host, database.Port, database.Database, database.Username, database.SslMode };
            if (!startup.Ready)
            {
                return Results.Ok(new
                {
                    needsSetup = true,
                    databaseReady = false,
                    error = startup.LastError ?? "The database is still starting.",
                    user = (UserDto?)null,
                    database = info,
                });
            }
            try
            {
                var needsSetup = !await db.Users.AnyAsync();
                UserDto? me = null;
                if (http.User.Identity?.IsAuthenticated == true && Guid.TryParse(http.User.FindFirstValue(Claims.UserId), out var id))
                    me = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => UserDto.From(u)).FirstOrDefaultAsync();
                return Results.Ok(new
                {
                    needsSetup,
                    databaseReady = true,
                    error = (string?)null,
                    user = me,
                    database = info,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                startup.MarkUnavailable(ex);
                return Results.Ok(new
                {
                    needsSetup = true,
                    databaseReady = false,
                    error = startup.LastError,
                    user = (UserDto?)null,
                    database = info,
                });
            }
        }).AllowAnonymous();

        g.MapPost("/setup-database", async (DatabaseOptions req, SlopDbContext db, ServerOverridesFile overrides, IHostApplicationLifetime life, CancellationToken ct) =>
        {
            if (await db.Users.AnyAsync(ct)) return Results.Conflict(new { error = "Setup already completed" });
            if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.Database) || string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { error = "Host, database and username are required" });
            try
            {
                await using var conn = new NpgsqlConnection(req.ToConnectionString());
                await conn.OpenAsync(ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or ArgumentException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return Results.BadRequest(new { error = "Could not connect: " + ex.Message });
            }
            overrides.WriteDatabase(req);
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                // A clean exit leaves the Windows service stopped. Recovery restarts it only after a non-zero exit.
                if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
                    Environment.ExitCode = 1;
                life.StopApplication();
            });
            return Results.Ok(new { saved = true, restarting = true });
        }).AllowAnonymous();

        g.MapPost("/setup", async (SetupRequest req, SlopDbContext db, HttpContext http) =>
        {
            if (await db.Users.AnyAsync()) return Results.Conflict(new { error = "Setup already completed" });
            if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 8) return Results.BadRequest(new { error = "Username and a password of at least 8 characters are required" });
            var u = new User { Username = req.Username.Trim().ToLowerInvariant(), DisplayName = req.DisplayName ?? req.Username, Role = Roles.Admin };
            u.PasswordHash = PasswordUtil.Hash(u, req.Password);
            db.Users.Add(u);
            await db.SaveChangesAsync();
            await SignInAsync(http, u);
            return Results.Ok(UserDto.From(u));
        }).AllowAnonymous();

        g.MapPost("/login", async (LoginRequest req, SlopDbContext db, HttpContext http) =>
        {
            if (!await db.Users.AnyAsync())
                return Results.Json(new { error = "This server has no admin account yet. Create one to finish setup.", needsSetup = true }, statusCode: StatusCodes.Status409Conflict);
            var name = req.Username.Trim().ToLowerInvariant();
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == name);
            if (u is null || u.IsDisabled || !PasswordUtil.Verify(u, req.Password)) return Results.Json(new { error = "Invalid username or password" }, statusCode: 401);
            u.LastLoginAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await SignInAsync(http, u);
            return Results.Ok(UserDto.From(u));
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthSchemes.Cookie);
            return Results.Ok();
        });
    }

    public static Task SignInAsync(HttpContext http, User u)
    {
        var identity = new ClaimsIdentity([
            new Claim(Claims.UserId, u.Id.ToString()),
            new Claim(Claims.Name, u.DisplayName.Length > 0 ? u.DisplayName : u.Username),
            new Claim(Claims.Role, u.Role),
            new Claim(Claims.Kind, ActorTypes.User),
        ], AuthSchemes.Cookie);
        return http.SignInAsync(AuthSchemes.Cookie, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    private static void MapOps(WebApplication app)
    {
        app.MapGet("/api/tools", (ToolRegistry registry) => registry.All.OrderBy(t => t.Category).ThenBy(t => t.Name).Select(t => new
        {
            t.Name,
            t.Category,
            t.Description,
            effect = t.Effect == ToolEffect.Read ? "read" : "mutate",
            t.MinRole,
            t.AiAllowed,
            t.NoApprovalNeeded,
            t.Routine,
            t.Signature,
            inputSchema = t.InputSchema,
            outputSchema = t.OutputSchema,
            outputType = t.OutputType.Name,
        })).RequireAuthorization(Policies.ReadOnly).WithTags("Tools");

        // The web UI performs every operation through the same tool registry the AI and MCP use.
        app.MapPost("/api/ops/{tool}", async (string tool, JsonObject? args, HttpContext http, ToolExecutor executor, CancellationToken ct) =>
        {
            var outcome = await executor.ExecuteAsync(tool, args ?? new JsonObject(), http.Actor(), ToolMode.Direct, ct, origin: "web");
            return outcome.Status switch
            {
                ToolOutcome.Ok => Results.Json(outcome.Result),
                ToolOutcome.Proposed => Results.Json(new { status = "pending_approval", proposalId = outcome.ProposalId }, statusCode: 202),
                _ => Results.Json(new { error = outcome.Error }, statusCode: outcome.Error?.Contains("requires the") == true || outcome.Error?.Contains("permission") == true ? 403 : 400),
            };
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Tools");
    }

    private static void MapChat(WebApplication app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization(Policies.ReadOnly).WithTags("Chat");

        g.MapGet("/conversations", async (HttpContext http, SlopDbContext db) =>
        {
            var uid = http.Actor().UserId;
            return await db.ChatConversations.AsNoTracking().Where(c => c.UserId == uid && c.ArchivedAt == null).OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync();
        });

        g.MapPost("/conversations", async (NewConversation req, HttpContext http, SlopDbContext db) =>
        {
            var c = new ChatConversation { UserId = http.Actor().UserId!.Value, Title = string.IsNullOrWhiteSpace(req.Title) ? "New conversation" : req.Title };
            db.ChatConversations.Add(c);
            await db.SaveChangesAsync();
            return c;
        });

        g.MapDelete("/conversations/{id:guid}", async (Guid id, HttpContext http, SlopDbContext db) =>
        {
            var uid = http.Actor().UserId;
            var c = await db.ChatConversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
            if (c is null) return Results.NotFound();
            c.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/conversations/{id:guid}/messages", async (Guid id, HttpContext http, SlopDbContext db) =>
        {
            var uid = http.Actor().UserId;
            if (!await db.ChatConversations.AnyAsync(c => c.Id == id && c.UserId == uid)) return Results.NotFound();
            var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Seq).ToListAsync();
            var proposalIds = messages.Where(m => m.ProposalId != null).Select(m => m.ProposalId!.Value).Distinct().ToList();
            var proposals = await db.AiProposals.AsNoTracking().Where(p => proposalIds.Contains(p.Id)).ToListAsync();
            return Results.Ok(new { messages, proposals });
        });

        g.MapPost("/conversations/{id:guid}/messages", async (Guid id, PostMessage req, HttpContext http, AgentService agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "Message is empty" });
            var msg = await agent.PostUserMessageAsync(id, http.Actor(), req.Content.Trim(), req.Page, ct);
            return Results.Ok(msg);
        });
    }

    private static void MapProposals(WebApplication app)
    {
        var g = app.MapGroup("/api/proposals").RequireAuthorization(Policies.ReadOnly).WithTags("Proposals");

        g.MapGet("/", async (string? status, SlopDbContext db) =>
        {
            var q = db.AiProposals.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.Status == status);
            return await q.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync();
        });

        g.MapPost("/{id:guid}/approve", async (Guid id, ProposalDecision d, HttpContext http, ToolExecutor exec, CancellationToken ct) =>
            await exec.DecideAsync(id, true, d.Note, d.AllowAlways, http.Actor(), ct)).RequireAuthorization(Policies.ReadWrite);

        g.MapPost("/{id:guid}/reject", async (Guid id, ProposalDecision d, HttpContext http, ToolExecutor exec, CancellationToken ct) =>
            await exec.DecideAsync(id, false, d.Note, false, http.Actor(), ct)).RequireAuthorization(Policies.ReadWrite);

        app.MapDelete("/api/standing-approvals/{id:guid}", async (Guid id, SlopDbContext db, HttpContext http) =>
        {
            var s = await db.AiStandingApprovals.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            s.RevokedAt = DateTimeOffset.UtcNow;
            db.Audit(http.Actor(), "ai.standing_approval.revoke", s.ToolName, s.Id);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization(Policies.ReadWrite).WithTags("Proposals");
    }

    private static void MapMedia(WebApplication app)
    {
        app.MapGet("/api/cameras/{id:guid}/snapshot", (Guid id, int? width, CameraFrameStore store, HttpContext http) =>
        {
            var f = store.Scaled(id, width);
            if (f is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Captured-At"] = f.CapturedAt.ToString("O");
            return Results.File(f.Data, f.ContentType);
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Cameras");

        app.MapGet("/api/media/{id:guid}", async (Guid id, SlopDbContext db) =>
        {
            var m = await db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return m is null ? Results.NotFound() : Results.File(m.Data, m.ContentType);
        }).RequireAuthorization(Policies.ReadOnly).WithTags("Cameras");
    }

    private static void MapDeviceApi(WebApplication app)
    {
        var g = app.MapGroup("/api/device-api").WithTags("Device API");

        g.MapPost("/register", async (DeviceRegistrationRequest req, DeviceRegistry registry, HttpContext http, CancellationToken ct) =>
            await registry.RegisterAsync(req, http.Connection.RemoteIpAddress?.MapToIPv4().ToString(), ct)).AllowAnonymous();

        g.MapGet("/register/{id:guid}", async (Guid id, string secret, DeviceRegistry registry, CancellationToken ct) =>
            await registry.PollAsync(id, secret, ct)).AllowAnonymous();

        g.MapPost("/cameras/{id:guid}/frames", async (Guid id, Guid? requestId, HttpContext http, SlopDbContext db, CameraFrameStore store, LiveNotifier live, CancellationToken ct) =>
        {
            var deviceId = Guid.Parse(http.User.FindFirstValue(Claims.DeviceId)!);
            var cam = await db.Cameras.FirstOrDefaultAsync(c => c.Id == id && c.DeviceId == deviceId, ct);
            if (cam is null) return Results.NotFound();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0 || ms.Length > 20 * 1024 * 1024) return Results.BadRequest(new { error = "Frame must be 1 byte – 20 MB" });
            var frame = store.Put(id, ms.ToArray(), http.Request.ContentType ?? "image/jpeg", requestId);
            var persist = cam.LastFrameAt is null || (DateTimeOffset.UtcNow - cam.LastFrameAt.Value) > TimeSpan.FromMinutes(30)
                && !await db.Media.AnyAsync(m => m.CameraId == id && m.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(-30), ct);
            cam.LastFrameAt = frame.CapturedAt;
            cam.Status = "ok";
            if (persist)
            {
                db.Media.Add(new Media
                {
                    Kind = "camera_frame", CameraId = id, GardenId = cam.GardenId, BedId = cam.BedId, PlantId = cam.PlantId, ContentType = frame.ContentType,
                    Width = frame.Width, Height = frame.Height, SizeBytes = frame.Data.Length, Data = frame.Data, CapturedAt = frame.CapturedAt,
                });
            }
            await db.SaveChangesAsync(ct);
            await live.All(LiveEvents.CameraFrame, new { cameraId = id, capturedAt = frame.CapturedAt });
            return Results.Ok(new { frame.Width, frame.Height });
        }).RequireAuthorization(Policies.Device);
    }
}
