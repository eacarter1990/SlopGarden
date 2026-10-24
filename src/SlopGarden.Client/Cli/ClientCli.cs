using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Runtime;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Cli;

/// <summary>Command-line verbs of the client binary. Without a verb the client runs as a service/console app.</summary>
public static class ClientCli
{
    private const string Usage = """
        SlopGarden client

        Usage: slopgarden-client [command] [options]

        Commands:
          run (default)                     Run the client (console, systemd or Windows service)
          status                            Print the local status (from the running client, else from disk)
          pair --server <url> [--force]     Pair with a server ("add device by IP" from the device side)
          reset                             Forget the server registration and device token
          plugins                           List plugins and their trust level
          keygen [--out <name>]             Create a plugin-signing key pair (<name>.key / <name>.pub)
          sign-plugin --key <file> --dir <plugin folder>
                                            Sign an external plugin (writes plugin.sig.json)
          version                           Print the client version

        Any configuration key can be given as --Client:Key=value, e.g. --Client:DataDirectory=/tmp/c2
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "run";
        var rest = verb == "run" && (args.Length == 0 || args[0].StartsWith('-')) ? args : args[1..];
        try
        {
            switch (verb)
            {
                case "run":
                    using (var host = ClientHost.CreateBuilder(rest).Build())
                        await host.RunAsync();
                    return 0;
                case "status": return await StatusAsync(rest);
                case "pair": return await PairAsync(rest);
                case "reset": return Reset(rest);
                case "plugins": return Plugins(rest);
                case "keygen": return Keygen(rest);
                case "sign-plugin": return SignPlugin(rest);
                case "version" or "--version":
                    Console.WriteLine(ClientInfo.Version);
                    return 0;
                case "help" or "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{verb}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static (ClientOptions Options, IConfiguration Configuration) LoadOptions(string[] args)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables();
        ClientHost.AddConfigurationSources(builder, args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains(':')).ToArray());
        var configuration = builder.Build();
        var options = new ClientOptions();
        configuration.GetSection(ClientOptions.Section).Bind(options);
        if (Option(args, "--data-dir") is { } dataDir) options.DataDirectory = dataDir;
        return (options, configuration);
    }

    private static ClientPaths ResolvePaths(ClientOptions options)
    {
        // The CLI usually runs as a login user while the service owns /var/lib/slopgarden.
        if (string.IsNullOrWhiteSpace(options.DataDirectory) && OperatingSystem.IsLinux() && File.Exists(Path.Combine(ClientPaths.SystemStateDirectory, "identity.json")))
            options.DataDirectory = ClientPaths.SystemStateDirectory;
        return ClientPaths.Resolve(options);
    }

    private static HttpClient LocalHttp() => new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<int> StatusAsync(string[] args)
    {
        var (options, _) = LoadOptions(args);
        if (options.LocalApiPort > 0)
        {
            try
            {
                using var http = LocalHttp();
                var json = await http.GetStringAsync($"http://localhost:{options.LocalApiPort}/status");
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Client not reachable on port {options.LocalApiPort} ({ex.Message}); showing stored state.");
            }
        }
        var paths = ResolvePaths(options);
        var identity = IdentityStore.TryLoad(paths.IdentityFile);
        var config = new ConfigStore(paths, NullLogger<ConfigStore>.Instance).Load();
        var status = new
        {
            state = "not_running",
            dataDirectory = paths.DataDirectory,
            hardwareId = identity?.HardwareId,
            serverUrl = identity?.ServerUrl,
            deviceId = identity?.DeviceId,
            approved = !string.IsNullOrEmpty(identity?.DeviceToken),
            appliedConfigVersion = config?.Version,
        };
        Console.WriteLine(JsonSerializer.Serialize(status, ClientJson.Indented));
        return 0;
    }

    private static async Task<int> PairAsync(string[] args)
    {
        var server = ServerUrls.TryNormalize(Option(args, "--server") ?? args.FirstOrDefault(a => !a.StartsWith('-')))
            ?? throw new CliException("Usage: slopgarden-client pair --server http://host:5080");
        var (options, _) = LoadOptions(args);
        if (options.LocalApiPort > 0)
        {
            try
            {
                using var http = LocalHttp();
                var force = args.Contains("--force") ? "?force=true" : "";
                using var response = await http.PostAsJsonAsync($"http://localhost:{options.LocalApiPort}/pair{force}", new ManualPairRequest(server.ToString()), ClientJson.Options);
                Console.WriteLine(await response.Content.ReadAsStringAsync());
                return response.IsSuccessStatusCode ? 0 : 1;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine("Client is not running; writing the server URL to the identity file.");
            }
        }
        var paths = ResolvePaths(options);
        var store = new IdentityStore(paths, Microsoft.Extensions.Options.Options.Create(options), NullLogger<IdentityStore>.Instance);
        store.Update(i => i with { ServerUrl = server.ToString(), DiscoveredVia = DiscoveredVia.ManualIp, DeviceId = null, DeviceToken = null });
        Console.WriteLine($"Will register with {server} on next start ({paths.IdentityFile}).");
        return 0;
    }

    private static int Reset(string[] args)
    {
        var (options, _) = LoadOptions(args);
        var paths = ResolvePaths(options);
        IdentityStore.Delete(paths);
        Console.WriteLine($"Identity removed from {paths.DataDirectory}. Restart the client to register again (cached config and buffered data are kept).");
        return 0;
    }

    private static int Plugins(string[] args)
    {
        var (options, _) = LoadOptions(args);
        var paths = ResolvePaths(options);
        var cachedKeys = new ConfigStore(paths, NullLogger<ConfigStore>.Instance).Load()?.TrustedPluginKeys;
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var registry = new PluginRegistry(paths.PluginDirectories, options.TrustedPluginKeys, loggerFactory.CreateLogger<PluginRegistry>());
        registry.Load(cachedKeys);
        Console.WriteLine($"Plugin directories: {string.Join(", ", paths.PluginDirectories)}");
        Console.WriteLine();
        Console.WriteLine($"{"PLUGIN ID",-24} {"KIND",-9} {"VERSION",-10} {"TRUST",-9} {"SOURCE",-28} PUBLISHER");
        foreach (var p in registry.Describe().OrderBy(p => p.Source != PluginTrust.Builtin).ThenBy(p => p.PluginId))
        {
            Console.WriteLine($"{p.PluginId,-24} {string.Join('/', p.Kinds),-9} {p.Version,-10} {p.Trust,-9} {p.Source,-28} {p.Publisher}");
            if (p.Trust is PluginTrust.Invalid or PluginTrust.Unsigned && p.Description is { } d && d.StartsWith("Not loaded", StringComparison.Ordinal))
                Console.WriteLine($"{"",-24} {d}");
        }
        return 0;
    }

    private static int Keygen(string[] args)
    {
        var name = Option(args, "--out") ?? "slopgarden-plugin-signing";
        var (publicKey, privateKey) = PluginSigning.GenerateKeyPair();
        if (File.Exists(name + ".key")) throw new CliException($"{name}.key already exists; refusing to overwrite it.");
        File.WriteAllText(name + ".key", privateKey + Environment.NewLine);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(name + ".key", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(name + ".pub", publicKey + Environment.NewLine);
        Console.WriteLine($"Private key (keep secret): {Path.GetFullPath(name + ".key")}");
        Console.WriteLine($"Public key:                {Path.GetFullPath(name + ".pub")}");
        Console.WriteLine($"Key id:                    {PluginSigning.KeyId(publicKey)}");
        Console.WriteLine();
        Console.WriteLine("Register the public key on the server (plugin signing keys) or add it to Client:TrustedPluginKeys:");
        Console.WriteLine(JsonSerializer.Serialize(new TrustedPluginKey(PluginSigning.KeyId(publicKey), "<publisher>", publicKey, false), ClientJson.Indented));
        return 0;
    }

    private static int SignPlugin(string[] args)
    {
        var keyFile = Option(args, "--key") ?? throw new CliException("Usage: slopgarden-client sign-plugin --key <pkcs8 base64 file> --dir <plugin folder>");
        var dir = Option(args, "--dir") ?? throw new CliException("Usage: slopgarden-client sign-plugin --key <pkcs8 base64 file> --dir <plugin folder>");
        var signature = SignPluginFolder(dir, File.ReadAllText(keyFile).Trim());
        Console.WriteLine($"Signed {dir}: sha256 {signature.Sha256}, key {signature.KeyId}");
        return 0;
    }

    /// <summary>Signs the plugin in <paramref name="pluginDir"/> and writes plugin.sig.json next to plugin.json.</summary>
    public static PluginSignatureFile SignPluginFolder(string pluginDir, string privateKeyPkcs8Base64)
    {
        var manifestFile = Path.Combine(pluginDir, "plugin.json");
        if (!File.Exists(manifestFile)) throw new CliException($"{manifestFile} not found.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), ClientJson.Options)
            ?? throw new CliException("plugin.json is empty.");
        var entry = Path.Combine(pluginDir, manifest.EntryAssembly);
        if (!File.Exists(entry)) throw new CliException($"Entry assembly {entry} not found.");
        string publicKey;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
            publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new CliException($"The key is not a base64 PKCS#8 EC private key: {ex.Message}");
        }
        var sha = PluginSigning.ComputeSha256(entry);
        var signature = new PluginSignatureFile(PluginSigning.KeyId(publicKey), PluginSigning.Algorithm, sha,
            PluginSigning.Sign(manifest.Id, manifest.Version, sha, privateKeyPkcs8Base64));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.sig.json"), JsonSerializer.Serialize(signature, ClientJson.Indented));
        return signature;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

}

public sealed class CliException(string message) : Exception(message);
