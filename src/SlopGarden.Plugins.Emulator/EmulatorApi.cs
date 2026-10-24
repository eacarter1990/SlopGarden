using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Emulator;

internal sealed record EmulatorSensorValue(string Id, double Value, string Metric, string Unit, DateTimeOffset Time);
internal sealed record EmulatorSensorInfo(string Id, string Name, string Metric, string Unit);
internal sealed record EmulatorActuatorInfo(string Id, string Name, string Kind, bool On);
internal sealed record EmulatorCameraInfo(string Id, string Name);

/// <summary>Minimal client for the SlopGarden sensor emulator REST API.</summary>
internal sealed class EmulatorApi(Uri baseUrl)
{
    public const string DefaultBaseUrl = "http://localhost:5090";
    public const string ConfigKey = "Plugins:Emulator:BaseUrl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public Uri BaseUrl { get; } = baseUrl;

    public static EmulatorApi For(JsonObject settings, IServiceProvider services)
    {
        var url = Settings.String(settings, "emulatorUrl");
        if (string.IsNullOrWhiteSpace(url)) url = services.GetService<IConfiguration>()?[ConfigKey];
        if (string.IsNullOrWhiteSpace(url)) url = DefaultBaseUrl;
        return new EmulatorApi(new Uri(url.TrimEnd('/') + "/"));
    }

    public async Task<EmulatorSensorValue> GetSensorValueAsync(string sensorId, CancellationToken ct) =>
        await Http.GetFromJsonAsync<EmulatorSensorValue>(Url($"api/sensors/{Uri.EscapeDataString(sensorId)}/value"), Json, ct)
        ?? throw new InvalidOperationException($"Emulator returned no value for sensor '{sensorId}'.");

    public async Task SetActuatorStateAsync(string actuatorId, bool on, double? level, CancellationToken ct)
    {
        using var response = await Http.PutAsJsonAsync(Url($"api/actuators/{Uri.EscapeDataString(actuatorId)}/state"),
            new { on, level, source = "slopgarden-client" }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<(byte[] Data, string ContentType)> GetSnapshotAsync(string cameraId, int? width, int? height, CancellationToken ct)
    {
        var query = new List<string>();
        if (width is > 0) query.Add($"width={width}");
        if (height is > 0) query.Add($"height={height}");
        var path = $"api/cameras/{Uri.EscapeDataString(cameraId)}/snapshot" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        using var response = await Http.GetAsync(Url(path), ct);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return (data, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    public async Task<IReadOnlyList<EmulatorSensorInfo>> ListSensorsAsync(CancellationToken ct) =>
        await Http.GetFromJsonAsync<List<EmulatorSensorInfo>>(Url("api/sensors"), Json, ct) ?? [];

    public async Task<IReadOnlyList<EmulatorActuatorInfo>> ListActuatorsAsync(CancellationToken ct) =>
        await Http.GetFromJsonAsync<List<EmulatorActuatorInfo>>(Url("api/actuators"), Json, ct) ?? [];

    public async Task<IReadOnlyList<EmulatorCameraInfo>> ListCamerasAsync(CancellationToken ct) =>
        await Http.GetFromJsonAsync<List<EmulatorCameraInfo>>(Url("api/cameras"), Json, ct) ?? [];

    private Uri Url(string relative) => new(BaseUrl, relative);
}
