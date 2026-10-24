using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Sample.HttpJson;

/// <summary>
/// Sample external plugin: GETs a URL and reads one number at a JSON path such as <c>current.temperature_2m</c>
/// or <c>$.items[0].value</c>. Any public, non-abstract class with a parameterless constructor implementing
/// <see cref="ISensorPlugin"/>, <see cref="IActuatorPlugin"/>, <see cref="ICameraPlugin"/> or
/// <see cref="IHardwareDiscovery"/> is picked up by the client.
/// </summary>
public sealed class HttpJsonSensorPlugin : ISensorPlugin
{
    public const string ChannelKey = "value";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public string PluginId => "http-json-sensor";
    public string DisplayName => "HTTP JSON sensor";
    public string Description => "Reads a numeric value from a JSON HTTP endpoint (weather APIs, other controllers, home automation).";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("url", "string", "URL returning JSON.", required: true)
        .Prop("path", "string", "Path to the number, e.g. current.temperature_2m or $.items[0].value.", required: true)
        .Prop("metric", "string", "Metric reported for the value, e.g. air_temperature_c.", "value")
        .Prop("unit", "string", "Unit of the value.", "")
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) =>
        [new(ChannelKey, Settings.String(settings, "metric", "value"), Settings.String(settings, "unit"))];

    public ISensor Create(PluginContext context)
    {
        var url = Settings.String(context.Settings, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Setting 'url' must be an absolute http(s) URL.");
        var path = Settings.String(context.Settings, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Setting 'path' is required.");
        context.Logger.LogInformation("HTTP JSON sensor {Name} reads {Path} from {Url}", context.Name, path, uri);
        return new HttpJsonSensor(uri, path);
    }

    private sealed class HttpJsonSensor(Uri url, string path) : ISensor
    {
        public async ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return [new RawReading(ChannelKey, JsonPath.ReadNumber(doc.RootElement, path))];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Minimal JSON path: dot-separated property names with optional [index], optional leading "$.".</summary>
public static class JsonPath
{
    public static double ReadNumber(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in Segments(path))
        {
            current = segment switch
            {
                int index when current.ValueKind == JsonValueKind.Array && index < current.GetArrayLength() => current[index],
                string name when current.ValueKind == JsonValueKind.Object && current.TryGetProperty(name, out var child) => child,
                _ => throw new KeyNotFoundException($"JSON path '{path}' not found (at '{segment}')."),
            };
        }
        return current.ValueKind switch
        {
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String when double.TryParse(current.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new FormatException($"Value at '{path}' is not a number ({current.ValueKind})."),
        };
    }

    private static IEnumerable<object> Segments(string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$')) p = p[1..].TrimStart('.');
        foreach (var part in p.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            var bracket = name.IndexOf('[');
            if (bracket < 0)
            {
                yield return name;
                continue;
            }
            if (bracket > 0) yield return name[..bracket];
            foreach (var idx in name[bracket..].Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries))
                yield return int.Parse(idx, CultureInfo.InvariantCulture);
        }
    }
}
