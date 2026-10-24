using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using SlopGarden.Contracts;

namespace SlopGarden.Client;

public sealed class ClientOptions
{
    public const string Section = "Client";

    /// <summary>Server base URL. Empty = LAN auto-discovery.</summary>
    public string? ServerUrl { get; set; }
    public string? DataDirectory { get; set; }
    /// <summary>Display name reported to the server instead of the machine host name.</summary>
    public string? Name { get; set; }
    /// <summary>Port of the local status/pairing API. 0 disables it.</summary>
    public int LocalApiPort { get; set; } = Discovery.ClientLocalApiPort;
    public string? PluginsDirectory { get; set; }
    public List<TrustedPluginKey> TrustedPluginKeys { get; set; } = [];
    public ClientLocation? Location { get; set; }
    /// <summary>Overrides the machine-derived hardware id, e.g. to run several emulated clients on one machine.</summary>
    public string? HardwareId { get; set; }
    public int DiscoveryPort { get; set; } = Discovery.UdpPort;
}

public sealed class ClientLocation
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Label { get; set; }
}

/// <summary>Resolved on-disk locations of the client.</summary>
public sealed class ClientPaths
{
    public const string SystemStateDirectory = "/var/lib/slopgarden";

    public ClientPaths(string dataDirectory, IReadOnlyList<string> pluginDirectories)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        PluginDirectories = pluginDirectories;
    }

    public string DataDirectory { get; }
    public IReadOnlyList<string> PluginDirectories { get; }
    public string IdentityFile => Path.Combine(DataDirectory, "identity.json");
    public string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public string DatabaseFile => Path.Combine(DataDirectory, "outbox.db");
    public string LogFile => Path.Combine(DataDirectory, "client.log");

    public static ClientPaths Resolve(ClientOptions options)
    {
        var data = string.IsNullOrWhiteSpace(options.DataDirectory) ? DefaultDataDirectory() : ExpandHome(options.DataDirectory);
        Directory.CreateDirectory(data);
        var pluginDirs = new List<string>
        {
            string.IsNullOrWhiteSpace(options.PluginsDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "plugins")
                : Path.GetFullPath(ExpandHome(options.PluginsDirectory)),
            Path.Combine(Path.GetFullPath(data), "plugins"),
        };
        return new ClientPaths(data, pluginDirs.Distinct(StringComparer.Ordinal).ToList());
    }

    public static string DefaultDataDirectory()
    {
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(stateDir)) return stateDir.Split(':')[0];
        if (OperatingSystem.IsLinux() && SystemdHelpers.IsSystemdService() && IsWritable(SystemStateDirectory)) return SystemStateDirectory;
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SlopGarden", "client");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".slopgarden-client");
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
