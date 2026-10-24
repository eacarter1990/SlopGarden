using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>Reads DS18B20 probes through the Linux w1-gpio/w1-therm sysfs interface.</summary>
public static class Ds18b20Sysfs
{
    public const string DefaultRoot = "/sys/bus/w1/devices";
    public const string RootConfigKey = "Plugins:Ds18b20:RootPath";

    /// <summary>The value a DS18B20 reports before its first conversion after power-up.</summary>
    public const double PowerOnResetCelsius = 85.0;

    public static IReadOnlyList<string> ListProbes(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root, "28-*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Parses the <c>temperature</c> attribute (millidegrees Celsius).</summary>
    public static double? ParseTemperatureFile(string content) =>
        int.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli) ? milli / 1000.0 : null;

    /// <summary>Parses the legacy <c>w1_slave</c> attribute; returns null when the CRC check failed.</summary>
    public static double? ParseW1Slave(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !lines[0].EndsWith("YES", StringComparison.Ordinal)) return null;
        var idx = lines[1].LastIndexOf("t=", StringComparison.Ordinal);
        return idx >= 0 && int.TryParse(lines[1][(idx + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli)
            ? milli / 1000.0
            : null;
    }

    public static async Task<double> ReadCelsiusAsync(string root, string deviceId, CancellationToken ct)
    {
        var dir = Path.Combine(root, deviceId);
        var temperatureFile = Path.Combine(dir, "temperature");
        if (File.Exists(temperatureFile))
        {
            return ParseTemperatureFile(await File.ReadAllTextAsync(temperatureFile, ct))
                ?? throw new IOException($"DS18B20 {deviceId}: unreadable temperature value.");
        }
        var slave = Path.Combine(dir, "w1_slave");
        if (File.Exists(slave))
        {
            return ParseW1Slave(await File.ReadAllTextAsync(slave, ct))
                ?? throw new IOException($"DS18B20 {deviceId}: CRC check failed.");
        }
        throw new FileNotFoundException($"DS18B20 probe '{deviceId}' not found under {root}. Is the w1-gpio overlay enabled?");
    }

    public static string ResolveRoot(JsonObject settings, IServiceProvider services)
    {
        var root = Settings.String(settings, "rootPath");
        if (string.IsNullOrWhiteSpace(root)) root = services.GetService<IConfiguration>()?[RootConfigKey];
        return string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
    }
}

public sealed class Ds18b20Plugin : ISensorPlugin
{
    public const string Id = "ds18b20";
    public const string ChannelKey = "temperature";

    public string PluginId => Id;
    public string DisplayName => "DS18B20 1-Wire temperature probe";
    public string Description => "Waterproof 1-Wire temperature probe (soil or water temperature), read via Linux sysfs.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("deviceId", "string", "1-Wire id such as 28-0316a2795cff. Optional when exactly one probe is connected.")
        .Prop("metric", "string", "Metric to report.", Metrics.SoilTemperatureC,
            @enum: [Metrics.SoilTemperatureC, Metrics.WaterTemperatureC, Metrics.AirTemperatureC])
        .Prop("rootPath", "string", "sysfs 1-Wire devices directory.", Ds18b20Sysfs.DefaultRoot)
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) =>
        [new(ChannelKey, Settings.String(settings, "metric", Metrics.SoilTemperatureC), "°C")];

    public ISensor Create(PluginContext context)
    {
        var root = Ds18b20Sysfs.ResolveRoot(context.Settings, context.Services);
        var deviceId = Settings.String(context.Settings, "deviceId");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            var probes = Ds18b20Sysfs.ListProbes(root);
            deviceId = probes.Count == 1
                ? probes[0]
                : throw new ArgumentException($"Setting 'deviceId' is required ({probes.Count} probes found under {root}).");
        }
        return new Probe(root, deviceId);
    }

    private sealed class Probe(string root, string deviceId) : ISensor
    {
        public async ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            var celsius = await Ds18b20Sysfs.ReadCelsiusAsync(root, deviceId, cancellationToken);
            var quality = celsius == Ds18b20Sysfs.PowerOnResetCelsius ? ReadingQuality.Suspect : ReadingQuality.Good;
            return [new RawReading(ChannelKey, celsius, quality)];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class Ds18b20Discovery : IHardwareDiscovery
{
    public string PluginId => Ds18b20Plugin.Id;

    public ValueTask<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var root = Ds18b20Sysfs.ResolveRoot(new JsonObject(), services);
        IReadOnlyList<DiscoveredDevice> devices = Ds18b20Sysfs.ListProbes(root)
            .Select(id => new DiscoveredDevice(
                Ds18b20Plugin.Id, PluginKinds.Sensor, $"w1:{id}", $"Temperature probe {id}",
                new JsonObject { ["deviceId"] = id },
                [new ChannelDescriptor(Ds18b20Plugin.ChannelKey, Metrics.SoilTemperatureC, "°C")]))
            .ToList();
        return ValueTask.FromResult(devices);
    }
}
