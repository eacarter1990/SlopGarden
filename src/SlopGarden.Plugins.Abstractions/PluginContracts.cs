using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SlopGarden.Plugins.Abstractions;

/// <summary>
/// Common metadata of every plugin component. A plugin assembly may contain any number of
/// sensor, actuator and camera components; the client discovers them by reflection.
/// </summary>
public interface IPluginComponent
{
    /// <summary>Stable id referenced by server-side config, e.g. "emulator-sensor", "ds18b20".</summary>
    string PluginId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>JSON Schema (draft 2020-12 subset) describing the <c>settings</c> object, used by the web UI and the AI.</summary>
    JsonObject SettingsSchema { get; }
}

public sealed record ChannelDescriptor(string Key, string Metric, string Unit);

/// <summary>A raw reading from a sensor channel. Calibration is applied by the client afterwards.</summary>
public sealed record RawReading(string ChannelKey, double Value, short Quality = 0);

public sealed record PluginContext(Guid ComponentId, string Name, JsonObject Settings, IServiceProvider Services, ILogger Logger);

public interface ISensorPlugin : IPluginComponent
{
    IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings);
    ISensor Create(PluginContext context);
}

public interface ISensor : IAsyncDisposable
{
    ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken);
}

public interface IActuatorPlugin : IPluginComponent
{
    /// <summary>Actuator kinds this plugin can drive (see <c>SlopGarden.Contracts.ActuatorKinds</c>).</summary>
    IReadOnlyList<string> SupportedKinds { get; }
    IActuator Create(PluginContext context);
}

public interface IActuator : IAsyncDisposable
{
    /// <param name="on">Desired on/off state.</param>
    /// <param name="level">Optional 0..1 level for dimmable/PWM outputs.</param>
    ValueTask SetStateAsync(bool on, double? level, CancellationToken cancellationToken);
}

public sealed record CameraFrame(byte[] Data, string ContentType, int Width, int Height);

public interface ICameraPlugin : IPluginComponent
{
    ICamera Create(PluginContext context);
}

public interface ICamera : IAsyncDisposable
{
    ValueTask<CameraFrame> CaptureAsync(int? width, int? height, CancellationToken cancellationToken);
}

public sealed record DiscoveredDevice(
    string PluginId,
    string Kind,
    string Address,
    string SuggestedName,
    JsonObject SuggestedSettings,
    IReadOnlyList<ChannelDescriptor> Channels,
    string? ActuatorKind = null);

/// <summary>Optional: plugins that can enumerate attached hardware (I²C scan, 1-Wire bus, emulator inventory).</summary>
public interface IHardwareDiscovery
{
    string PluginId { get; }
    ValueTask<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>Helpers for plugin authors.</summary>
public static class Settings
{
    public static string String(JsonObject s, string key, string fallback = "") =>
        s.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var str) ? str : fallback;

    public static int Int(JsonObject s, string key, int fallback = 0)
    {
        if (!s.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var str))
        {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(str[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)) return hex;
            if (int.TryParse(str, out var parsed)) return parsed;
        }
        return fallback;
    }

    public static double Double(JsonObject s, string key, double fallback = 0)
    {
        if (!s.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var str) && double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    public static bool Bool(JsonObject s, string key, bool fallback = false) =>
        s.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}

/// <summary>Tiny builder for settings schemas so plugins don't hand-write JSON.</summary>
public sealed class SchemaBuilder
{
    private readonly JsonObject _props = new();
    private readonly JsonArray _required = new();

    public SchemaBuilder Prop(string name, string type, string description, JsonNode? @default = null, bool required = false, string[]? @enum = null)
    {
        var p = new JsonObject { ["type"] = type, ["description"] = description };
        if (@default is not null) p["default"] = @default.DeepClone();
        if (@enum is not null) p["enum"] = new JsonArray(@enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        _props[name] = p;
        if (required) _required.Add(name);
        return this;
    }

    public JsonObject Build() => new()
    {
        ["type"] = "object",
        ["properties"] = _props.DeepClone(),
        ["required"] = _required.DeepClone(),
    };
}
