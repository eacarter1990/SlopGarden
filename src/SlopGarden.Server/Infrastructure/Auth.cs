using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

public static class AuthSchemes
{
    public const string Cookie = "Cookies";
    public const string Token = "Token";
    public const string Smart = "Smart";
}

public static class Claims
{
    public const string Role = ClaimTypes.Role;
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Name = ClaimTypes.Name;
    public const string Kind = "sg:kind";
    public const string Scopes = "sg:scopes";
    public const string PatId = "sg:pat";
    public const string DeviceId = "sg:device";
}

public static class Policies
{
    public const string Admin = "admin";
    public const string ReadWrite = "read_write";
    public const string ReadOnly = "read_only";
    public const string Device = "device";
}

/// <summary>Who is acting. Shared by REST endpoints, the AI agent and MCP.</summary>
public sealed record Actor(string Type, Guid? UserId, string Label, string Role, IReadOnlyList<string> Scopes, Guid? PatId = null)
{
    public static readonly Actor System = new(ActorTypes.System, null, "system", Roles.Admin, PatScopes.All);

    public bool CanRead => Roles.AtLeast(Role, Roles.ReadOnly);
    public bool CanWrite => Roles.AtLeast(Role, Roles.ReadWrite) && (Type != ActorTypes.Pat || Scopes.Contains(PatScopes.Write));
    public bool IsAdmin => Role == Roles.Admin && (Type != ActorTypes.Pat || Scopes.Contains(PatScopes.Write));

    public static Actor FromPrincipal(ClaimsPrincipal p)
    {
        var kind = p.FindFirstValue(Claims.Kind) ?? ActorTypes.User;
        var id = Guid.TryParse(p.FindFirstValue(Claims.UserId), out var g) ? g : (Guid?)null;
        var scopes = (p.FindFirstValue(Claims.Scopes) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var patId = Guid.TryParse(p.FindFirstValue(Claims.PatId), out var pg) ? pg : (Guid?)null;
        return new Actor(kind, id, p.FindFirstValue(Claims.Name) ?? "unknown", p.FindFirstValue(Claims.Role) ?? Roles.ReadOnly,
            kind == ActorTypes.Pat ? scopes : PatScopes.All, patId);
    }
}

public static class TokenUtil
{
    public const string PatPrefix = "sgp_";
    public const string DevicePrefix = "sgd_";

    public static string NewToken(string prefix) =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Bearer authentication for personal access tokens (MCP/API) and device tokens (client hub).</summary>
public sealed class TokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SlopDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken(Request);
        if (token is null) return AuthenticateResult.NoResult();
        var hash = TokenUtil.Hash(token);

        if (token.StartsWith(TokenUtil.DevicePrefix, StringComparison.Ordinal))
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.CredentialHash == hash);
            if (device is null || device.Status != DeviceStatuses.Approved) return AuthenticateResult.Fail("invalid device token");
            var identity = new ClaimsIdentity([
                new Claim(Claims.Kind, ActorTypes.Device),
                new Claim(Claims.DeviceId, device.Id.ToString()),
                new Claim(Claims.Name, device.Name),
                new Claim(Claims.Role, "device"),
            ], Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }

        if (token.StartsWith(TokenUtil.PatPrefix, StringComparison.Ordinal))
        {
            var pat = await db.PersonalAccessTokens.Include(p => p.User).FirstOrDefaultAsync(p => p.TokenHash == hash);
            if (pat is null || pat.RevokedAt is not null || (pat.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow) || pat.User is null || pat.User.IsDisabled)
                return AuthenticateResult.Fail("invalid token");
            if (pat.LastUsedAt is null || pat.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                pat.LastUsedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            var identity = new ClaimsIdentity([
                new Claim(Claims.Kind, ActorTypes.Pat),
                new Claim(Claims.UserId, pat.UserId.ToString()),
                new Claim(Claims.PatId, pat.Id.ToString()),
                new Claim(Claims.Name, $"{pat.User.Username} (token '{pat.Name}')"),
                new Claim(Claims.Role, pat.User.Role),
                new Claim(Claims.Scopes, string.Join(',', pat.Scopes)),
            ], Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }

        return AuthenticateResult.Fail("unknown token type");
    }

    public static string? ExtractToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        if (request.Path.StartsWithSegments("/hubs") && request.Query.TryGetValue("access_token", out var q)) return q.ToString();
        return null;
    }
}

public static class PasswordUtil
{
    private static readonly Microsoft.AspNetCore.Identity.PasswordHasher<User> Hasher = new();

    public static string Hash(User u, string password) => Hasher.HashPassword(u, password);

    public static bool Verify(User u, string password) =>
        Hasher.VerifyHashedPassword(u, u.PasswordHash, password) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
}

public static class HttpContextActorExtensions
{
    public static Actor Actor(this HttpContext ctx) => Infrastructure.Actor.FromPrincipal(ctx.User);
}
