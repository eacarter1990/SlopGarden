using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;
using SlopGarden.Plugins.Emulator;
using SlopGarden.Plugins.Hardware;

namespace SlopGarden.Client.Plugins;

public sealed record TrustResult(string Trust, string? Publisher, string? KeyId, string? Problem);

/// <summary>An external plugin folder (plugin.json + entry assembly + optional plugin.sig.json).</summary>
public sealed class PluginPackage
{
    public required string Directory { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required string EntryPath { get; init; }
    public required string Sha256 { get; init; }
    public PluginSignatureFile? Signature { get; init; }
    public TrustResult TrustResult { get; set; } = new(PluginTrust.Unsigned, null, null, null);
    public bool Loaded { get; set; }
    public string? LoadError { get; set; }
    public List<IPluginComponent> Components { get; } = [];
    public List<IHardwareDiscovery> Discoveries { get; } = [];
    public string Source => $"external:{Manifest.Id}";
}

/// <summary>Built-in and external plugin components, looked up by plugin id.</summary>
public sealed class PluginRegistry
{
    private readonly Lock _gate = new();
    private readonly ILogger<PluginRegistry> _logger;
    private readonly IReadOnlyList<TrustedPluginKey> _localKeys;
    private readonly List<IPluginComponent> _builtin = [];
    private readonly List<IHardwareDiscovery> _builtinDiscoveries = [];
    private readonly List<PluginPackage> _packages = [];
    private IReadOnlyList<TrustedPluginKey> _remoteKeys = [];
    private bool _loaded;

    public PluginRegistry(ClientPaths paths, IOptions<ClientOptions> options, ILogger<PluginRegistry> logger)
        : this(paths.PluginDirectories, options.Value.TrustedPluginKeys, logger)
    {
    }

    public PluginRegistry(IReadOnlyList<string> pluginDirectories, IReadOnlyList<TrustedPluginKey> localKeys, ILogger<PluginRegistry> logger)
    {
        PluginDirectories = pluginDirectories;
        _localKeys = localKeys;
        _logger = logger;
    }

    public IReadOnlyList<string> PluginDirectories { get; }

    public static IReadOnlyList<Assembly> BuiltinAssemblies { get; } =
        [typeof(EmulatorSensorPlugin).Assembly, typeof(GpioRelayPlugin).Assembly];

    /// <summary>Loads built-in plugins and scans plugin directories. Safe to call more than once.</summary>
    public void Load(IReadOnlyList<TrustedPluginKey>? remoteKeys = null)
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            _remoteKeys = remoteKeys ?? [];
            foreach (var asm in BuiltinAssemblies)
            {
                var (components, discoveries) = Instantiate(asm, _logger);
                _builtin.AddRange(components);
                _builtinDiscoveries.AddRange(discoveries);
            }
            foreach (var dir in PluginDirectories.Where(Directory.Exists))
            {
                foreach (var pluginDir in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
                {
                    if (!File.Exists(Path.Combine(pluginDir, "plugin.json"))) continue;
                    var package = ReadPackage(pluginDir, _logger);
                    if (package is null) continue;
                    _packages.Add(package);
                    EvaluateAndLoad(package);
                }
            }
        }
    }

    /// <summary>Adds an in-process component as built-in (used by tests and embedding hosts).</summary>
    internal void RegisterBuiltin(IPluginComponent component)
    {
        lock (_gate) _builtin.Add(component);
    }

    /// <summary>Re-evaluates external plugin trust with keys pushed by the server.</summary>
    public void UpdateTrustedKeys(IReadOnlyList<TrustedPluginKey> remoteKeys)
    {
        lock (_gate)
        {
            _remoteKeys = remoteKeys;
            foreach (var p in _packages) EvaluateAndLoad(p);
        }
    }

    private void EvaluateAndLoad(PluginPackage package)
    {
        var keys = _localKeys.Concat(_remoteKeys).ToList();
        var previous = package.TrustResult.Trust;
        package.TrustResult = EvaluateTrust(package.Manifest, package.Sha256, package.Signature, keys);
        var trust = package.TrustResult.Trust;
        if (trust == PluginTrust.Invalid)
        {
            if (previous != PluginTrust.Invalid || !package.Loaded)
                _logger.LogError("Refusing plugin {PluginId} {Version} from {PluginDir}: {Problem}", package.Manifest.Id, package.Manifest.Version, package.Directory, package.TrustResult.Problem);
            return;
        }
        if (!package.Loaded && package.LoadError is null) LoadAssembly(package);
        if (previous == trust && package.Loaded) return;
        if (trust == PluginTrust.Unsigned)
        {
            _logger.LogWarning("WARNING: unsigned plugin {PluginId} {Version} loaded from {PluginDir}; its integrity and publisher cannot be verified, security can't be guaranteed{Problem}",
                package.Manifest.Id, package.Manifest.Version, package.Directory, package.TrustResult.Problem is { } p ? $" ({p})" : "");
        }
        else
        {
            _logger.LogInformation("Plugin {PluginId} {Version} signed by {Publisher} (key {KeyId})", package.Manifest.Id, package.Manifest.Version, package.TrustResult.Publisher, package.TrustResult.KeyId);
        }
    }

    private void LoadAssembly(PluginPackage package)
    {
        try
        {
            var context = new PluginLoadContext(package.EntryPath);
            var asm = context.LoadFromAssemblyPath(package.EntryPath);
            var (components, discoveries) = Instantiate(asm, _logger);
            foreach (var c in components)
            {
                if (_builtin.Any(b => b.PluginId == c.PluginId) || _packages.Any(p => p != package && p.Components.Any(o => o.PluginId == c.PluginId)))
                {
                    _logger.LogWarning("Plugin {Package} declares component id {PluginId} which is already provided; ignoring it", package.Manifest.Id, c.PluginId);
                    continue;
                }
                package.Components.Add(c);
            }
            package.Discoveries.AddRange(discoveries);
            package.Loaded = true;
        }
        catch (Exception ex)
        {
            package.LoadError = ex.Message;
            _logger.LogError(ex, "Plugin {PluginId} from {PluginDir} could not be loaded", package.Manifest.Id, package.Directory);
        }
    }

    public static PluginPackage? ReadPackage(string pluginDir, ILogger logger)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(pluginDir, "plugin.json")), ClientJson.Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            {
                logger.LogError("Invalid plugin.json in {PluginDir}: id and entryAssembly are required", pluginDir);
                return null;
            }
            var entry = Path.GetFullPath(Path.Combine(pluginDir, manifest.EntryAssembly));
            if (!entry.StartsWith(Path.GetFullPath(pluginDir), StringComparison.Ordinal) || !File.Exists(entry))
            {
                logger.LogError("Plugin {PluginId}: entry assembly {EntryAssembly} not found in {PluginDir}", manifest.Id, manifest.EntryAssembly, pluginDir);
                return null;
            }
            var sigFile = Path.Combine(pluginDir, "plugin.sig.json");
            PluginSignatureFile? signature = null;
            if (File.Exists(sigFile))
            {
                try
                {
                    signature = JsonSerializer.Deserialize<PluginSignatureFile>(File.ReadAllText(sigFile), ClientJson.Options);
                }
                catch (JsonException)
                {
                    signature = new PluginSignatureFile("", "", "", "");
                }
            }
            return new PluginPackage
            {
                Directory = pluginDir,
                Manifest = manifest,
                EntryPath = entry,
                Sha256 = PluginSigning.ComputeSha256(entry),
                Signature = signature,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read plugin in {PluginDir}", pluginDir);
            return null;
        }
    }

    /// <summary>Signed = valid signature by a trusted, non-revoked key. Invalid = signature present but wrong
    /// (hash mismatch, bad signature, revoked key). A signature by an unknown key cannot be checked: unsigned.</summary>
    public static TrustResult EvaluateTrust(PluginManifest manifest, string sha256, PluginSignatureFile? signature, IReadOnlyList<TrustedPluginKey> keys)
    {
        if (signature is null) return new(PluginTrust.Unsigned, null, null, null);
        if (!string.Equals(signature.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            return new(PluginTrust.Invalid, null, signature.KeyId, "entry assembly hash does not match the signature (modified after signing)");
        if (signature.Algorithm != PluginSigning.Algorithm)
            return new(PluginTrust.Invalid, null, signature.KeyId, $"unsupported signature algorithm '{signature.Algorithm}'");
        var key = keys.FirstOrDefault(k => string.Equals(k.KeyId, signature.KeyId, StringComparison.OrdinalIgnoreCase));
        if (key is null) return new(PluginTrust.Unsigned, null, signature.KeyId, $"signed with unknown key {signature.KeyId}");
        if (key.Revoked) return new(PluginTrust.Invalid, key.Publisher, key.KeyId, $"signing key {key.KeyId} ({key.Publisher}) is revoked");
        if (!PluginSigning.Verify(manifest.Id, manifest.Version, sha256, signature.Signature, key.PublicKeySpki))
            return new(PluginTrust.Invalid, key.Publisher, key.KeyId, "signature verification failed");
        return new(PluginTrust.Signed, key.Publisher, key.KeyId, null);
    }

    public static (List<IPluginComponent> Components, List<IHardwareDiscovery> Discoveries) Instantiate(Assembly asm, ILogger logger)
    {
        var components = new List<IPluginComponent>();
        var discoveries = new List<IHardwareDiscovery>();
        Type[] types;
        try
        {
            types = asm.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().Where(t => t.IsPublic).ToArray();
        }
        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) is not null))
        {
            var isComponent = typeof(ISensorPlugin).IsAssignableFrom(type) || typeof(IActuatorPlugin).IsAssignableFrom(type) || typeof(ICameraPlugin).IsAssignableFrom(type);
            var isDiscovery = typeof(IHardwareDiscovery).IsAssignableFrom(type);
            if (!isComponent && !isDiscovery) continue;
            try
            {
                var instance = Activator.CreateInstance(type)!;
                if (isComponent) components.Add((IPluginComponent)instance);
                if (isDiscovery) discoveries.Add((IHardwareDiscovery)instance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not instantiate plugin type {PluginType}", type.FullName);
            }
        }
        return (components, discoveries);
    }

    private IEnumerable<IPluginComponent> Available()
    {
        lock (_gate)
        {
            return _builtin.Concat(_packages.Where(p => p.Loaded && p.TrustResult.Trust != PluginTrust.Invalid).SelectMany(p => p.Components)).ToList();
        }
    }

    public ISensorPlugin? FindSensor(string pluginId) => Available().OfType<ISensorPlugin>().FirstOrDefault(p => p.PluginId == pluginId);

    public IActuatorPlugin? FindActuator(string pluginId) => Available().OfType<IActuatorPlugin>().FirstOrDefault(p => p.PluginId == pluginId);

    public ICameraPlugin? FindCamera(string pluginId) => Available().OfType<ICameraPlugin>().FirstOrDefault(p => p.PluginId == pluginId);

    public IReadOnlyList<IHardwareDiscovery> Discoveries
    {
        get
        {
            lock (_gate)
            {
                return _builtinDiscoveries.Concat(_packages.Where(p => p.Loaded && p.TrustResult.Trust != PluginTrust.Invalid).SelectMany(p => p.Discoveries)).ToList();
            }
        }
    }

    public IReadOnlyList<PluginPackage> Packages
    {
        get { lock (_gate) return _packages.ToList(); }
    }

    /// <summary>One entry per component (plus refused/broken packages) for registration and Hello.</summary>
    public IReadOnlyList<PluginInfo> Describe()
    {
        lock (_gate)
        {
            var list = new List<PluginInfo>();
            foreach (var c in _builtin)
            {
                var asm = c.GetType().Assembly;
                list.Add(new PluginInfo(c.PluginId, ClientInfo.Version, c.DisplayName, [KindOf(c)], AssemblyHash(asm), PluginTrust.Builtin,
                    PluginTrust.Builtin, "SlopGarden", null, c.Description, [ComponentInfo(c)]));
            }
            foreach (var p in _packages)
            {
                var t = p.TrustResult;
                if (!p.Loaded || t.Trust == PluginTrust.Invalid)
                {
                    var problem = t.Problem ?? p.LoadError ?? "not loaded";
                    list.Add(new PluginInfo(p.Manifest.Id, p.Manifest.Version, p.Manifest.DisplayName, p.Manifest.Kinds ?? [], p.Sha256, p.Source,
                        t.Trust, t.Publisher ?? p.Manifest.Publisher, t.KeyId, $"Not loaded: {problem}"));
                    continue;
                }
                foreach (var c in p.Components)
                {
                    list.Add(new PluginInfo(c.PluginId, p.Manifest.Version, c.DisplayName, [KindOf(c)], p.Sha256, p.Source,
                        t.Trust, t.Publisher ?? p.Manifest.Publisher, t.KeyId, c.Description, [ComponentInfo(c)]));
                }
            }
            return list;
        }
    }

    private PluginComponentInfo ComponentInfo(IPluginComponent c)
    {
        try
        {
            return new PluginComponentInfo(c.PluginId, KindOf(c), c.DisplayName, c.Description, c.SettingsSchema.DeepClone().AsObject(),
                (c as IActuatorPlugin)?.SupportedKinds.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin {PluginId} has no usable settings schema", c.PluginId);
            return new PluginComponentInfo(c.PluginId, KindOf(c), c.DisplayName, c.Description, new JsonObject { ["type"] = "object" });
        }
    }

    private static string KindOf(IPluginComponent c) => c switch
    {
        ISensorPlugin => PluginKinds.Sensor,
        IActuatorPlugin => PluginKinds.Actuator,
        ICameraPlugin => PluginKinds.Camera,
        _ => "unknown",
    };

    private static readonly Dictionary<Assembly, string> HashCache = [];

    private static string AssemblyHash(Assembly asm)
    {
        lock (HashCache)
        {
            if (HashCache.TryGetValue(asm, out var h)) return h;
            // Location is empty for assemblies inside a single-file bundle; fall back to the module version id.
#pragma warning disable IL3000
            var location = asm.Location;
#pragma warning restore IL3000
            h = !string.IsNullOrEmpty(location) && File.Exists(location)
                ? PluginSigning.ComputeSha256(location)
                : asm.ManifestModule.ModuleVersionId.ToString("N");
            HashCache[asm] = h;
            return h;
        }
    }
}
