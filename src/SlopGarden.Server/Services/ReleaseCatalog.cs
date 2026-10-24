using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed record PublishedRelease(string Tag, string Version, string ServerImage, string ClientImage);

public sealed record ReleaseChoice(string Mode, string Version, string ServerImage, string ClientImage, bool ServerAutoUpdate);

/// <summary>Turns GitHub releases into the server and client image tags published with that release.</summary>
public static class ReleaseImages
{
    public const string TrackKey = "release_track";

    public static string Owner(string repository)
    {
        var slash = repository.IndexOf('/');
        var owner = slash > 0 ? repository[..slash] : repository;
        return owner.Trim().ToLowerInvariant();
    }

    public static string Server(string owner, string version) => $"ghcr.io/{owner.ToLowerInvariant()}/slopgarden-server:{ClientVersions.Normalize(version)}";
    public static string Client(string owner, string version) => $"ghcr.io/{owner.ToLowerInvariant()}/slopgarden-client:{ClientVersions.Normalize(version)}";

    public static IReadOnlyList<PublishedRelease> Parse(string json, string owner)
    {
        if (JsonNode.Parse(json) is not JsonArray arr) return [];
        var list = new List<PublishedRelease>();
        foreach (var node in arr)
        {
            if (node?["draft"] is JsonValue draft && draft.TryGetValue<bool>(out var isDraft) && isDraft) continue;
            var tag = node?["tag_name"]?.ToString();
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var version = ClientVersions.Normalize(tag);
            if (version.Length == 0) continue;
            list.Add(new PublishedRelease(tag, version, Server(owner, version), Client(owner, version)));
        }
        return list
            .OrderByDescending(r => r.Version, Comparer<string>.Create((a, b) =>
            {
                if (ClientVersions.IsSame(a, b)) return 0;
                if (ClientVersions.IsOutdated(a, b)) return -1;
                if (ClientVersions.IsOutdated(b, a)) return 1;
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }))
            .ToList();
    }

    /// <summary>Latest release, or the pinned version. A pin still names the images when the list has not loaded.</summary>
    public static PublishedRelease? Choose(IReadOnlyList<PublishedRelease> releases, string? mode, string? pinnedVersion, string owner)
    {
        if (string.Equals(mode, "pinned", StringComparison.OrdinalIgnoreCase))
        {
            var version = ClientVersions.Normalize(pinnedVersion);
            if (version.Length == 0) return null;
            return releases.FirstOrDefault(r => ClientVersions.IsSame(r.Version, version))
                ?? new PublishedRelease("v" + version, version, Server(owner, version), Client(owner, version));
        }
        return releases.FirstOrDefault();
    }
}

public sealed class ReleaseCatalog(IHttpClientFactory http, IOptions<ServerOptions> options, ILogger<ReleaseCatalog> log)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<PublishedRelease> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private string? _error;

    public string Owner => ReleaseImages.Owner(options.Value.GitHubRepository);
    public string Repository => options.Value.GitHubRepository;

    public async Task<IReadOnlyList<PublishedRelease>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(10) && _cached.Count > 0) return _cached;
        }
        try
        {
            var client = http.CreateClient("github");
            using var res = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub returned {(int)res.StatusCode}");
            var list = ReleaseImages.Parse(body, Owner);
            lock (_gate) { _cached = list; _cachedAt = DateTimeOffset.UtcNow; _error = null; }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not list GitHub releases");
            lock (_gate) _error = ex.Message;
            return _cached;
        }
    }

    public string? LastError { get { lock (_gate) return _error; } }

    public static async Task<ReleaseChoice> ReadTrackAsync(SlopDbContext db, string owner, CancellationToken ct)
    {
        var row = await db.ServerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ReleaseImages.TrackKey, ct);
        var mode = row?.Value?["mode"]?.ToString();
        if (mode is not ("latest" or "pinned")) mode = "latest";
        var version = row?.Value?["version"]?.ToString() ?? "";
        var auto = row?.Value?["serverAutoUpdate"] is JsonValue autoNode && autoNode.TryGetValue<bool>(out var flag) && flag;
        var chosen = ReleaseImages.Choose([], mode, version, owner);
        return new ReleaseChoice(mode, chosen?.Version ?? "", chosen?.ServerImage ?? "", chosen?.ClientImage ?? "", auto);
    }

    public async Task RememberAsync(SlopDbContext db, PublishedRelease release, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == ClientUpdateService.SettingKey, ct);
        var value = new JsonObject { ["version"] = release.Version, ["image"] = release.ClientImage, ["serverImage"] = release.ServerImage };
        if (row is null) db.ServerSettings.Add(new ServerSetting { Key = ClientUpdateService.SettingKey, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
