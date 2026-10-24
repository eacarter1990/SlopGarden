using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SlopGarden.Client.Cli;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Client.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private const string SampleId = "http-json-sensor";

    private readonly string _root = Wait.TempDir("plugins");
    private readonly string _pluginDir;

    public PluginLoaderTests()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "sample-plugin");
        Assert.True(File.Exists(Path.Combine(source, "plugin.json")), $"sample plugin was not staged into {source}");
        _pluginDir = Path.Combine(_root, SampleId);
        Directory.CreateDirectory(_pluginDir);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(_pluginDir, Path.GetFileName(file)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PluginRegistry Load(IReadOnlyList<TrustedPluginKey>? localKeys = null, IReadOnlyList<TrustedPluginKey>? remoteKeys = null)
    {
        var registry = new PluginRegistry([_root], localKeys ?? [], NullLogger<PluginRegistry>.Instance);
        registry.Load(remoteKeys);
        return registry;
    }

    private static (TrustedPluginKey Key, string PrivateKey) NewKey(string publisher = "Test publisher", bool revoked = false)
    {
        var (publicKey, privateKey) = PluginSigning.GenerateKeyPair();
        return (new TrustedPluginKey(PluginSigning.KeyId(publicKey), publisher, publicKey, revoked), privateKey);
    }

    private string EntryAssembly => Path.Combine(_pluginDir, "SlopGarden.Plugins.Sample.HttpJson.dll");

    [Fact]
    public void Builtin_plugins_are_always_available()
    {
        var registry = Load();
        Assert.NotNull(registry.FindSensor("emulator-sensor"));
        Assert.NotNull(registry.FindActuator("gpio-relay"));
        Assert.NotNull(registry.FindCamera("emulator-camera"));
        Assert.NotNull(registry.FindSensor("ds18b20"));
        var info = registry.Describe().Single(p => p.PluginId == "ads1115");
        Assert.Equal(PluginTrust.Builtin, info.Trust);
        var component = Assert.Single(info.Components!);
        Assert.Equal(PluginKinds.Sensor, component.Kind);
        Assert.NotNull(component.SettingsSchema["properties"]);
    }

    [Fact]
    public void Unsigned_plugin_loads_with_a_warning_trust_level()
    {
        var registry = Load();

        var sensor = registry.FindSensor(SampleId);
        Assert.NotNull(sensor);
        Assert.Equal("HTTP JSON sensor", sensor.DisplayName);
        var package = Assert.Single(registry.Packages);
        Assert.Equal(PluginTrust.Unsigned, package.TrustResult.Trust);
        var info = registry.Describe().Single(p => p.PluginId == SampleId);
        Assert.Equal(PluginTrust.Unsigned, info.Trust);
        Assert.Equal($"external:{SampleId}", info.Source);
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal(PluginSigning.ComputeSha256(EntryAssembly), info.Sha256);
    }

    [Fact]
    public void Plugin_is_loaded_in_its_own_context_but_shares_the_abstractions()
    {
        var sensor = Load().FindSensor(SampleId)!;
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(sensor.GetType().Assembly);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, context);
        Assert.IsAssignableFrom<ISensorPlugin>(sensor);
        var channels = sensor.DescribeChannels(new JsonObject { ["metric"] = Metrics.AirTemperatureC, ["unit"] = "°C" });
        Assert.Equal(Metrics.AirTemperatureC, Assert.Single(channels).Metric);
    }

    [Fact]
    public void Signed_plugin_with_a_trusted_key_is_signed()
    {
        var (key, privateKey) = NewKey();
        var signature = ClientCli.SignPluginFolder(_pluginDir, privateKey);
        Assert.Equal(key.KeyId, signature.KeyId);

        var registry = Load(localKeys: [key]);

        Assert.NotNull(registry.FindSensor(SampleId));
        var trust = Assert.Single(registry.Packages).TrustResult;
        Assert.Equal(PluginTrust.Signed, trust.Trust);
        Assert.Equal("Test publisher", trust.Publisher);
        var info = registry.Describe().Single(p => p.PluginId == SampleId);
        Assert.Equal(PluginTrust.Signed, info.Trust);
        Assert.Equal(key.KeyId, info.SignatureKeyId);
    }

    [Fact]
    public void Tampered_plugin_is_refused()
    {
        var (key, privateKey) = NewKey();
        ClientCli.SignPluginFolder(_pluginDir, privateKey);
        using (var stream = File.OpenWrite(EntryAssembly))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.WriteByte(0x42);
        }

        var registry = Load(localKeys: [key]);

        Assert.Null(registry.FindSensor(SampleId));
        var package = Assert.Single(registry.Packages);
        Assert.Equal(PluginTrust.Invalid, package.TrustResult.Trust);
        Assert.False(package.Loaded);
        Assert.Contains("does not match", package.TrustResult.Problem);
        var info = registry.Describe().Single(p => p.PluginId == SampleId);
        Assert.Equal(PluginTrust.Invalid, info.Trust);
        Assert.StartsWith("Not loaded", info.Description);
    }

    [Fact]
    public void Tampered_plugin_is_refused_even_without_trusted_keys()
    {
        ClientCli.SignPluginFolder(_pluginDir, NewKey().PrivateKey);
        File.AppendAllText(EntryAssembly, "x");
        Assert.Null(Load().FindSensor(SampleId));
    }

    [Fact]
    public void Forged_signature_is_refused()
    {
        var (trusted, _) = NewKey();
        var (_, attackerKey) = NewKey("Attacker");
        var forged = ClientCli.SignPluginFolder(_pluginDir, attackerKey) with { KeyId = trusted.KeyId };
        File.WriteAllText(Path.Combine(_pluginDir, "plugin.sig.json"), JsonSerializer.Serialize(forged, SlopJson.Options));

        var registry = Load(localKeys: [trusted]);

        Assert.Null(registry.FindSensor(SampleId));
        Assert.Equal("signature verification failed", registry.Packages.Single().TrustResult.Problem);
    }

    [Fact]
    public void Plugin_signed_with_a_revoked_key_is_refused()
    {
        var (key, privateKey) = NewKey(revoked: true);
        ClientCli.SignPluginFolder(_pluginDir, privateKey);

        var registry = Load(localKeys: [key]);

        Assert.Null(registry.FindSensor(SampleId));
        Assert.Contains("revoked", registry.Packages.Single().TrustResult.Problem);
    }

    [Fact]
    public void Plugin_signed_with_an_unknown_key_counts_as_unsigned()
    {
        ClientCli.SignPluginFolder(_pluginDir, NewKey().PrivateKey);
        var registry = Load();
        Assert.NotNull(registry.FindSensor(SampleId));
        var trust = registry.Packages.Single().TrustResult;
        Assert.Equal(PluginTrust.Unsigned, trust.Trust);
        Assert.Contains("unknown key", trust.Problem);
    }

    [Fact]
    public void Keys_pushed_by_the_server_upgrade_trust()
    {
        var (key, privateKey) = NewKey("Server-trusted");
        ClientCli.SignPluginFolder(_pluginDir, privateKey);
        var registry = Load();
        Assert.Equal(PluginTrust.Unsigned, registry.Packages.Single().TrustResult.Trust);

        registry.UpdateTrustedKeys([key]);
        Assert.Equal(PluginTrust.Signed, registry.Packages.Single().TrustResult.Trust);

        registry.UpdateTrustedKeys([key with { Revoked = true }]);
        Assert.Equal(PluginTrust.Invalid, registry.Packages.Single().TrustResult.Trust);
        Assert.Null(registry.FindSensor(SampleId));
    }

    [Fact]
    public void Broken_plugin_folders_are_skipped_without_affecting_others()
    {
        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "plugin.json"), "{ not json");
        var missing = Path.Combine(_root, "missing-dll");
        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "plugin.json"), """{"id":"missing","version":"1.0.0","entryAssembly":"Missing.dll"}""");
        var garbage = Path.Combine(_root, "garbage");
        Directory.CreateDirectory(garbage);
        File.WriteAllText(Path.Combine(garbage, "plugin.json"), """{"id":"garbage","version":"1.0.0","entryAssembly":"Garbage.dll"}""");
        File.WriteAllText(Path.Combine(garbage, "Garbage.dll"), "this is not an assembly");

        var registry = Load();

        Assert.NotNull(registry.FindSensor(SampleId));
        var garbagePackage = registry.Packages.Single(p => p.Manifest.Id == "garbage");
        Assert.False(garbagePackage.Loaded);
        Assert.NotNull(garbagePackage.LoadError);
        Assert.Contains(registry.Describe(), p => p.PluginId == "garbage" && p.Description!.StartsWith("Not loaded"));
    }

    [Fact]
    public void Trust_evaluation_rejects_unknown_algorithms()
    {
        var manifest = new PluginManifest("p", "1.0.0", "P", "p.dll", null, null, ["sensor"]);
        var (key, privateKey) = NewKey();
        var sig = new PluginSignatureFile(key.KeyId, "rsa-md5", "abc", PluginSigning.Sign("p", "1.0.0", "abc", privateKey));
        Assert.Equal(PluginTrust.Invalid, PluginRegistry.EvaluateTrust(manifest, "abc", sig, [key]).Trust);
        Assert.Equal(PluginTrust.Signed, PluginRegistry.EvaluateTrust(manifest, "abc", sig with { Algorithm = PluginSigning.Algorithm }, [key]).Trust);
        Assert.Equal(PluginTrust.Unsigned, PluginRegistry.EvaluateTrust(manifest, "abc", null, [key]).Trust);
    }
}
