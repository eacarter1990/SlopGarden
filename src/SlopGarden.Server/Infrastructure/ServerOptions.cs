using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace SlopGarden.Server.Infrastructure;

public sealed class DatabaseOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "slopgarden";
    public string Username { get; set; } = "slopgarden";
    public string Password { get; set; } = "slopgarden";
    public string SslMode { get; set; } = "Disable";
    /// <summary>Extra Npgsql keywords, e.g. "Pooling=true;Timeout=30".</summary>
    public string? Extra { get; set; }

    public string ToConnectionString()
    {
        var b = new NpgsqlConnectionStringBuilder(Extra ?? "")
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = Enum.TryParse<SslMode>(SslMode, true, out var m) ? m : Npgsql.SslMode.Disable,
        };
        return b.ConnectionString;
    }
}

public sealed class ServerOptions
{
    public const string Section = "SlopGarden";

    public string DataDirectory { get; set; } = "";
    public string ServerName { get; set; } = "SlopGarden";
    /// <summary>URL clients should use to reach this server. Empty = derive from the LAN address and port.</summary>
    public string? PublicUrl { get; set; }
    public bool DiscoveryEnabled { get; set; } = true;
    public bool SeedDemo { get; set; }
    /// <summary>Optional published client image. Empty until an Admin sets it (or these two are both set).</summary>
    public string ClientReleaseVersion { get; set; } = "";
    public string ClientReleaseImage { get; set; } = "";
    /// <summary>GitHub repository whose releases publish the server image, the client image, and the server installer.</summary>
    public string GitHubRepository { get; set; } = "eacarter1990/SlopGarden";
    public string InitialAdminUsername { get; set; } = "admin";
    public string InitialAdminPassword { get; set; } = "";
    public DatabaseOptions Database { get; set; } = new();

    public static string ResolveDataDirectory(IConfiguration config)
    {
        var configured = config[$"{Section}:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".slopgarden-server");
    }

    public static string OverridesFile(string dataDir) => Path.Combine(dataDir, "slopgarden.server.json");
}

/// <summary>Reads and writes the Admin-editable overrides file (database connection etc.).</summary>
public sealed class ServerOverridesFile(string path)
{
    public string Path { get; } = path;

    public DatabaseOptions? ReadDatabase()
    {
        if (!File.Exists(Path)) return null;
        var root = JsonNode.Parse(File.ReadAllText(Path))?[ServerOptions.Section]?["Database"];
        return root?.Deserialize<DatabaseOptions>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public void WriteDatabase(DatabaseOptions db)
    {
        JsonObject root = File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path))?.AsObject() ?? new() : new();
        var section = root[ServerOptions.Section]?.AsObject();
        if (section is null)
        {
            section = new JsonObject();
            root[ServerOptions.Section] = section;
        }
        section["Database"] = JsonSerializer.SerializeToNode(db);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
