using Microsoft.EntityFrameworkCore;
using Npgsql;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Endpoints;

public sealed record ProviderRequest(string Name, string BaseUrl, string? ApiKey, string? DefaultModel, bool ClearApiKey = false);
public sealed record ProviderTestRequest(string BaseUrl, string? ApiKey, Guid? ProviderId);
public sealed record AssignmentRequest(Guid ProviderId, string Model, double? Temperature, int? MaxOutputTokens, int? ContextMessageLimit);
public sealed record WebSearchRequest(string Provider, string? BaseUrl, string? ApiKey, string? Mode = null);
public sealed record ChannelRequest(string Kind, string Name, string? WebhookUrl, string? MinSeverity, List<string>? Categories, bool? Enabled);
public sealed record UserRequest(string Username, string? DisplayName, string? Email, string Role, string? Password, bool? IsDisabled);
public sealed record PasswordChange(string CurrentPassword, string NewPassword);
public sealed record TokenRequest(string Name, List<string> Scopes, int? ExpiresInDays);
public sealed record SigningKeyRequest(string Publisher, string PublicKeySpki);
public sealed record CatalogRequest(string PluginId, string Version, string DisplayName, string Publisher, List<string>? Kinds, string? Description,
    string Sha256, string Signature, string KeyId, string? HomepageUrl);

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        MapAi(app);
        MapNotifications(app);
        MapUsers(app);
        MapServer(app);
        MapPluginCatalog(app);
    }

    private static void MapAi(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/ai").WithTags("Settings").RequireAuthorization(Policies.ReadOnly);

        g.MapGet("/", async (SlopDbContext db, WebSearchService webSearch, CancellationToken ct) =>
        {
            var providers = await db.AiProviders.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct);
            var assignments = await db.AiModelAssignments.AsNoTracking().ToListAsync(ct);
            var search = await webSearch.SettingsAsync(ct);
            var searchStatus = await webSearch.StatusAsync(ct);
            return new
            {
                providers = providers.Select(p => new { p.Id, p.Name, p.BaseUrl, p.DefaultModel, hasApiKey = p.ApiKeySecretId != null, p.CreatedAt }),
                assignments = AiJobs.All.Select(j => assignments.FirstOrDefault(a => a.Job == j) is { } a
                    ? new { job = j, providerId = (Guid?)a.ProviderId, a.Model, a.Temperature, a.MaxOutputTokens, a.ContextMessageLimit }
                    : new { job = j, providerId = (Guid?)null, Model = "", Temperature = (double?)null, MaxOutputTokens = (int?)null, ContextMessageLimit = 30 }),
                webSearch = new { search.Provider, search.BaseUrl, search.Mode, hasApiKey = search.ApiKeySecretId != null, providers = WebSearchSettings.Providers, status = searchStatus },
            };
        });

        g.MapPost("/providers", async (ProviderRequest req, SlopDbContext db, SecretStore secrets, HttpContext http, CancellationToken ct) =>
        {
            var p = new AiProvider { Name = req.Name, BaseUrl = req.BaseUrl.Trim(), DefaultModel = req.DefaultModel ?? "" };
            if (!string.IsNullOrWhiteSpace(req.ApiKey)) p.ApiKeySecretId = await secrets.SetAsync(null, $"ai:{req.Name}", "ai_api_key", req.ApiKey.Trim(), ct);
            db.AiProviders.Add(p);
            if (!await db.AiModelAssignments.AnyAsync(ct) && !string.IsNullOrWhiteSpace(p.DefaultModel))
                foreach (var job in AiJobs.All) db.AiModelAssignments.Add(new AiModelAssignment { Job = job, ProviderId = p.Id, Model = p.DefaultModel });
            db.Audit(http.Actor(), "ai.provider.create", "ai_provider", p.Id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { p.Id });
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/providers/{id:guid}", async (Guid id, ProviderRequest req, SlopDbContext db, SecretStore secrets, HttpContext http, CancellationToken ct) =>
        {
            var p = await db.AiProviders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            p.Name = req.Name;
            p.BaseUrl = req.BaseUrl.Trim();
            p.DefaultModel = req.DefaultModel ?? p.DefaultModel;
            if (req.ClearApiKey)
            {
                await secrets.DeleteAsync(p.ApiKeySecretId, ct);
                p.ApiKeySecretId = null;
            }
            else if (!string.IsNullOrWhiteSpace(req.ApiKey))
            {
                p.ApiKeySecretId = await secrets.SetAsync(p.ApiKeySecretId, $"ai:{req.Name}", "ai_api_key", req.ApiKey.Trim(), ct);
            }
            db.Audit(http.Actor(), "ai.provider.update", "ai_provider", p.Id);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapDelete("/providers/{id:guid}", async (Guid id, SlopDbContext db, SecretStore secrets, CancellationToken ct) =>
        {
            var p = await db.AiProviders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            db.AiProviders.Remove(p);
            await db.SaveChangesAsync(ct);
            await secrets.DeleteAsync(p.ApiKeySecretId, ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/providers/test", async (ProviderTestRequest req, SlopDbContext db, SecretStore secrets, OpenAiClient client, CancellationToken ct) =>
        {
            var key = req.ApiKey;
            if (string.IsNullOrWhiteSpace(key) && req.ProviderId is { } pid)
                key = await secrets.GetAsync(await db.AiProviders.Where(p => p.Id == pid).Select(p => p.ApiKeySecretId).FirstOrDefaultAsync(ct), ct);
            try
            {
                var models = await client.ListModelsAsync(req.BaseUrl, key, ct);
                return Results.Ok(new { ok = true, models });
            }
            catch (Exception ex) when (ex is AiProviderException or HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
            {
                return Results.Ok(new { ok = false, error = ex.Message, models = Array.Empty<string>() });
            }
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/assignments/{job}", async (string job, AssignmentRequest req, SlopDbContext db, CancellationToken ct) =>
        {
            if (!AiJobs.All.Contains(job)) return Results.BadRequest(new { error = "Unknown job" });
            var a = await db.AiModelAssignments.FirstOrDefaultAsync(x => x.Job == job, ct);
            if (a is null)
            {
                a = new AiModelAssignment { Job = job };
                db.AiModelAssignments.Add(a);
            }
            a.ProviderId = req.ProviderId;
            a.Model = req.Model;
            a.Temperature = req.Temperature;
            a.MaxOutputTokens = req.MaxOutputTokens;
            a.ContextMessageLimit = Math.Clamp(req.ContextMessageLimit ?? 30, 4, 200);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/web-search", async (WebSearchRequest req, SettingsService settings, WebSearchService webSearch, SecretStore secrets, HttpContext http, CancellationToken ct) =>
        {
            if (!WebSearchSettings.Providers.Contains(req.Provider)) return Results.BadRequest(new { error = "Unknown provider" });
            if (req.Mode is not null && !WebSearchSettings.Modes.Contains(req.Mode)) return Results.BadRequest(new { error = "mode must be auto or custom" });
            var current = await webSearch.SettingsAsync(ct);
            var secretId = current.ApiKeySecretId;
            if (!string.IsNullOrWhiteSpace(req.ApiKey)) secretId = await secrets.SetAsync(secretId, "search", "search_api_key", req.ApiKey.Trim(), ct);
            await settings.SetAsync(WebSearchService.SettingsKey, new WebSearchSettings(req.Provider, req.BaseUrl, secretId, req.Mode ?? current.Mode), http.Actor().UserId, ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);
    }

    private static void MapNotifications(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/notification-channels").WithTags("Settings").RequireAuthorization(Policies.ReadOnly);

        g.MapGet("/", async (SlopDbContext db) => (await db.NotificationChannels.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync())
            .Select(c => new { c.Id, c.Kind, c.Name, c.MinSeverity, c.Categories, c.Enabled, hasWebhook = c.SecretId != null, c.CreatedAt }));

        g.MapPost("/", async (ChannelRequest req, SlopDbContext db, SecretStore secrets, CancellationToken ct) =>
        {
            if (req.Kind is not ("discord" or "browser")) return Results.BadRequest(new { error = "kind must be discord or browser" });
            var c = new NotificationChannel { Kind = req.Kind, Name = req.Name, MinSeverity = req.MinSeverity ?? Severities.Info, Categories = req.Categories ?? [], Enabled = req.Enabled ?? true };
            if (req.Kind == "discord")
            {
                if (string.IsNullOrWhiteSpace(req.WebhookUrl)) return Results.BadRequest(new { error = "Discord webhook URL is required" });
                c.SecretId = await secrets.SetAsync(null, "discord", "discord_webhook", req.WebhookUrl.Trim(), ct);
            }
            db.NotificationChannels.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { c.Id });
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/{id:guid}", async (Guid id, ChannelRequest req, SlopDbContext db, SecretStore secrets, CancellationToken ct) =>
        {
            var c = await db.NotificationChannels.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            c.Name = req.Name;
            c.MinSeverity = req.MinSeverity ?? c.MinSeverity;
            c.Categories = req.Categories ?? c.Categories;
            c.Enabled = req.Enabled ?? c.Enabled;
            if (!string.IsNullOrWhiteSpace(req.WebhookUrl)) c.SecretId = await secrets.SetAsync(c.SecretId, "discord", "discord_webhook", req.WebhookUrl.Trim(), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapDelete("/{id:guid}", async (Guid id, SlopDbContext db, SecretStore secrets, CancellationToken ct) =>
        {
            var c = await db.NotificationChannels.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            db.NotificationChannels.Remove(c);
            await db.SaveChangesAsync(ct);
            await secrets.DeleteAsync(c.SecretId, ct);
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/test", async (Guid id, SlopDbContext db, IEnumerable<INotificationSender> senders, CancellationToken ct) =>
        {
            var c = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            var sender = senders.FirstOrDefault(s => s.Kind == c.Kind);
            if (sender is null) return Results.BadRequest(new { error = "No sender for this channel kind" });
            try
            {
                await sender.SendAsync(c, new Notification { Title = "SlopGarden test notification", Body = "If you can read this, the channel works. 🌱", Severity = Severities.Info, Category = "system" }, ct);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        }).RequireAuthorization(Policies.Admin);
    }

    private static void MapUsers(WebApplication app)
    {
        var g = app.MapGroup("/api/users").WithTags("Users");

        g.MapGet("/", async (SlopDbContext db) => (await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync()).Select(UserDto.From))
            .RequireAuthorization(Policies.Admin);

        g.MapPost("/", async (UserRequest req, SlopDbContext db, HttpContext http) =>
        {
            if (!Roles.All.Contains(req.Role)) return Results.BadRequest(new { error = "Invalid role" });
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters" });
            var name = req.Username.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Username == name)) return Results.Conflict(new { error = "Username taken" });
            var u = new User { Username = name, DisplayName = req.DisplayName ?? req.Username, Email = req.Email, Role = req.Role };
            u.PasswordHash = PasswordUtil.Hash(u, req.Password);
            db.Users.Add(u);
            db.Audit(http.Actor(), "user.create", "user", u.Id, new() { ["role"] = u.Role });
            await db.SaveChangesAsync();
            return Results.Ok(UserDto.From(u));
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/{id:guid}", async (Guid id, UserRequest req, SlopDbContext db, HttpContext http) =>
        {
            if (!Roles.All.Contains(req.Role)) return Results.BadRequest(new { error = "Invalid role" });
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            if (u.Role == Roles.Admin && (req.Role != Roles.Admin || req.IsDisabled == true) && await db.Users.CountAsync(x => x.Role == Roles.Admin && !x.IsDisabled) <= 1)
                return Results.BadRequest(new { error = "At least one active Admin is required" });
            u.DisplayName = req.DisplayName ?? u.DisplayName;
            u.Email = req.Email ?? u.Email;
            u.Role = req.Role;
            u.IsDisabled = req.IsDisabled ?? u.IsDisabled;
            if (!string.IsNullOrWhiteSpace(req.Password))
            {
                if (req.Password.Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters" });
                u.PasswordHash = PasswordUtil.Hash(u, req.Password);
            }
            db.Audit(http.Actor(), "user.update", "user", u.Id, new() { ["role"] = u.Role });
            await db.SaveChangesAsync();
            return Results.Ok(UserDto.From(u));
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/me/password", async (PasswordChange req, SlopDbContext db, HttpContext http) =>
        {
            var uid = http.Actor().UserId;
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid);
            if (u is null || !PasswordUtil.Verify(u, req.CurrentPassword)) return Results.BadRequest(new { error = "Current password is wrong" });
            if (req.NewPassword.Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters" });
            u.PasswordHash = PasswordUtil.Hash(u, req.NewPassword);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization(Policies.ReadOnly);

        var t = app.MapGroup("/api/tokens").WithTags("Tokens").RequireAuthorization(Policies.ReadOnly);

        t.MapGet("/", async (SlopDbContext db, HttpContext http) =>
        {
            var uid = http.Actor().UserId;
            return await db.PersonalAccessTokens.AsNoTracking().Where(p => p.UserId == uid).OrderByDescending(p => p.CreatedAt)
                .Select(p => new { p.Id, p.Name, p.TokenPrefix, p.Scopes, p.CreatedAt, p.ExpiresAt, p.LastUsedAt, p.RevokedAt }).ToListAsync();
        });

        t.MapPost("/", async (TokenRequest req, SlopDbContext db, HttpContext http) =>
        {
            var actor = http.Actor();
            if (actor.Type != ActorTypes.User) return Results.Forbid();
            if (req.Scopes.Count == 0 || req.Scopes.Any(s => !PatScopes.All.Contains(s))) return Results.BadRequest(new { error = "Scopes must be read, propose and/or write" });
            if (!actor.CanWrite && req.Scopes.Any(s => s != PatScopes.Read)) return Results.BadRequest(new { error = "ReadOnly users can only create read tokens" });
            var token = TokenUtil.NewToken(TokenUtil.PatPrefix);
            var pat = new PersonalAccessToken
            {
                UserId = actor.UserId!.Value, Name = req.Name, TokenHash = TokenUtil.Hash(token), TokenPrefix = token[..10], Scopes = req.Scopes.Distinct().ToList(),
                ExpiresAt = req.ExpiresInDays is { } d && d > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null,
            };
            db.PersonalAccessTokens.Add(pat);
            db.Audit(actor, "pat.create", "pat", pat.Id, new() { ["scopes"] = string.Join(',', pat.Scopes) });
            await db.SaveChangesAsync();
            return Results.Ok(new { pat.Id, token, pat.Name, pat.Scopes, pat.ExpiresAt });
        });

        t.MapDelete("/{id:guid}", async (Guid id, SlopDbContext db, HttpContext http) =>
        {
            var uid = http.Actor().UserId;
            var p = await db.PersonalAccessTokens.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
            if (p is null) return Results.NotFound();
            p.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }

    private static void MapServer(WebApplication app)
    {
        var g = app.MapGroup("/api/settings/server").WithTags("Settings");

        g.MapGet("/", (IConfiguration config, ServerUrlProvider url, ServerOverridesFile overrides, Microsoft.Extensions.Options.IOptions<ServerOptions> opts) =>
        {
            var db = opts.Value.Database;
            return new
            {
                version = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString(3),
                serverUrl = url.Get(),
                dataDirectory = opts.Value.DataDirectory,
                overridesFile = overrides.Path,
                discoveryEnabled = opts.Value.DiscoveryEnabled,
                discoveryPort = Discovery.UdpPort,
                database = new { db.Host, db.Port, db.Database, db.Username, db.SslMode, db.Extra, hasPassword = !string.IsNullOrEmpty(db.Password) },
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            };
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/database/test", async (DatabaseOptions req, Microsoft.Extensions.Options.IOptions<ServerOptions> opts, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Password)) req.Password = opts.Value.Database.Password;
            try
            {
                await using var conn = new NpgsqlConnection(req.ToConnectionString());
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("select version()", conn);
                return Results.Ok(new { ok = true, version = (string?)await cmd.ExecuteScalarAsync(ct) });
            }
            catch (Exception ex) when (ex is NpgsqlException or ArgumentException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/database", (DatabaseOptions req, ServerOverridesFile overrides, Microsoft.Extensions.Options.IOptions<ServerOptions> opts, HttpContext http, ILogger<ServerOptions> log) =>
        {
            if (string.IsNullOrEmpty(req.Password)) req.Password = opts.Value.Database.Password;
            overrides.WriteDatabase(req);
            log.LogWarning("Database connection changed by {User}; restart required", http.Actor().Label);
            return Results.Ok(new { saved = true, restartRequired = true, file = overrides.Path });
        }).RequireAuthorization(Policies.Admin);

        app.MapGet("/api/audit", async (int? limit, SlopDbContext db) =>
            await db.AuditLog.AsNoTracking().OrderByDescending(a => a.At).Take(Math.Clamp(limit ?? 200, 1, 2000)).ToListAsync())
            .RequireAuthorization(Policies.Admin).WithTags("Settings");
    }

    private static void MapPluginCatalog(WebApplication app)
    {
        var g = app.MapGroup("/api/plugin-catalog").WithTags("Plugins").RequireAuthorization(Policies.ReadOnly);

        g.MapGet("/keys", async (SlopDbContext db) => await db.PluginSigningKeys.AsNoTracking().OrderBy(k => k.Publisher).ToListAsync());

        g.MapPost("/keys", async (SigningKeyRequest req, SlopDbContext db, HttpContext http, ConfigPushQueue queue) =>
        {
            try
            {
                using var ecdsa = System.Security.Cryptography.ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(req.PublicKeySpki), out _);
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                return Results.BadRequest(new { error = "Not a valid base64 SubjectPublicKeyInfo ECDSA key" });
            }
            var keyId = PluginSigning.KeyId(req.PublicKeySpki);
            if (await db.PluginSigningKeys.AnyAsync(k => k.KeyId == keyId)) return Results.Conflict(new { error = "Key already trusted" });
            var k = new PluginSigningKey { KeyId = keyId, Publisher = req.Publisher, PublicKeySpki = req.PublicKeySpki };
            db.PluginSigningKeys.Add(k);
            db.Audit(http.Actor(), "plugin.key.add", "plugin_signing_key", k.Id, new() { ["publisher"] = req.Publisher });
            await db.SaveChangesAsync();
            queue.Dirty();
            return Results.Ok(k);
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/keys/{id:guid}/revoke", async (Guid id, SlopDbContext db, ConfigPushQueue queue) =>
        {
            var k = await db.PluginSigningKeys.FirstOrDefaultAsync(x => x.Id == id);
            if (k is null) return Results.NotFound();
            k.Status = "revoked";
            k.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            queue.Dirty();
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/", async (CatalogRequest req, SlopDbContext db, HttpContext http) =>
        {
            var key = await db.PluginSigningKeys.FirstOrDefaultAsync(k => k.KeyId == req.KeyId && k.Status == "active");
            if (key is null) return Results.BadRequest(new { error = "Unknown or revoked signing key" });
            if (!PluginSigning.Verify(req.PluginId, req.Version, req.Sha256, req.Signature, key.PublicKeySpki))
                return Results.BadRequest(new { error = "Signature does not verify for this plugin id, version and hash" });
            var e = new PluginCatalogEntry
            {
                PluginId = req.PluginId, Version = req.Version, DisplayName = req.DisplayName, Publisher = req.Publisher, Kinds = req.Kinds ?? [],
                Description = req.Description ?? "", Sha256 = req.Sha256.ToLowerInvariant(), Signature = req.Signature, SigningKeyId = key.Id,
                HomepageUrl = req.HomepageUrl, ApprovedAt = DateTimeOffset.UtcNow,
            };
            db.PluginCatalog.Add(e);
            db.Audit(http.Actor(), "plugin.catalog.add", "plugin_catalog", e.Id, new() { ["plugin"] = req.PluginId, ["version"] = req.Version });
            await db.SaveChangesAsync();
            await db.DevicePlugins.Where(p => p.PluginId == e.PluginId && p.Version == e.Version && p.Sha256 == e.Sha256)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogEntryId, e.Id));
            return Results.Ok(e);
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/revoke", async (Guid id, SlopDbContext db) =>
        {
            var e = await db.PluginCatalog.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            e.Status = "revoked";
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization(Policies.Admin);
    }
}
