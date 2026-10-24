using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Emulator;

public sealed class EmulatorSensorPlugin : ISensorPlugin
{
    public const string Id = "emulator-sensor";
    public const string ChannelKey = "value";

    public string PluginId => Id;
    public string DisplayName => "Emulated sensor";
    public string Description => "Reads a sensor of the SlopGarden sensor emulator over HTTP (for demos and tests).";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("sensorId", "string", "Id of the sensor in the emulator, e.g. tomato-soil-moisture.", required: true)
        .Prop("metric", "string", "Metric reported by the emulated sensor (informational).")
        .Prop("unit", "string", "Unit reported by the emulated sensor (informational).")
        .Prop("emulatorUrl", "string", "Emulator base URL. Defaults to the client's Plugins:Emulator:BaseUrl setting.")
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings)
    {
        var metric = Settings.String(settings, "metric", "unknown");
        return [new(ChannelKey, metric, Settings.String(settings, "unit", Metrics.DefaultUnits.GetValueOrDefault(metric, "")))];
    }

    public ISensor Create(PluginContext context)
    {
        var sensorId = Settings.String(context.Settings, "sensorId");
        if (string.IsNullOrWhiteSpace(sensorId)) throw new ArgumentException("Setting 'sensorId' is required.");
        return new EmulatorSensor(EmulatorApi.For(context.Settings, context.Services), sensorId);
    }

    private sealed class EmulatorSensor(EmulatorApi api, string sensorId) : ISensor
    {
        public async ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            var value = await api.GetSensorValueAsync(sensorId, cancellationToken);
            return [new RawReading(ChannelKey, value.Value)];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class EmulatorActuatorPlugin : IActuatorPlugin
{
    public const string Id = "emulator-actuator";

    public string PluginId => Id;
    public string DisplayName => "Emulated actuator";
    public string Description => "Switches an actuator of the SlopGarden sensor emulator over HTTP (for demos and tests).";
    public IReadOnlyList<string> SupportedKinds => ActuatorKinds.All;

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("actuatorId", "string", "Id of the actuator in the emulator, e.g. tomato-valve.", required: true)
        .Prop("emulatorUrl", "string", "Emulator base URL. Defaults to the client's Plugins:Emulator:BaseUrl setting.")
        .Build();

    public IActuator Create(PluginContext context)
    {
        var actuatorId = Settings.String(context.Settings, "actuatorId");
        if (string.IsNullOrWhiteSpace(actuatorId)) throw new ArgumentException("Setting 'actuatorId' is required.");
        return new EmulatorActuator(EmulatorApi.For(context.Settings, context.Services), actuatorId);
    }

    private sealed class EmulatorActuator(EmulatorApi api, string actuatorId) : IActuator
    {
        public async ValueTask SetStateAsync(bool on, double? level, CancellationToken cancellationToken) =>
            await api.SetActuatorStateAsync(actuatorId, on, level, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class EmulatorCameraPlugin : ICameraPlugin
{
    public const string Id = "emulator-camera";

    public string PluginId => Id;
    public string DisplayName => "Emulated camera";
    public string Description => "Fetches snapshots from a camera of the SlopGarden sensor emulator.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("cameraId", "string", "Id of the camera in the emulator, e.g. tomato-cam.", required: true)
        .Prop("emulatorUrl", "string", "Emulator base URL. Defaults to the client's Plugins:Emulator:BaseUrl setting.")
        .Build();

    public ICamera Create(PluginContext context)
    {
        var cameraId = Settings.String(context.Settings, "cameraId");
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("Setting 'cameraId' is required.");
        return new EmulatorCamera(EmulatorApi.For(context.Settings, context.Services), cameraId);
    }

    private sealed class EmulatorCamera(EmulatorApi api, string cameraId) : ICamera
    {
        public async ValueTask<CameraFrame> CaptureAsync(int? width, int? height, CancellationToken cancellationToken)
        {
            var (data, contentType) = await api.GetSnapshotAsync(cameraId, width, height, cancellationToken);
            return new CameraFrame(data, contentType, width ?? 1280, height ?? 720);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Lists everything in the emulator inventory with ready-to-use settings.</summary>
public sealed class EmulatorDiscovery : IHardwareDiscovery
{
    public string PluginId => "emulator";

    public async ValueTask<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var api = EmulatorApi.For(new JsonObject(), services);
        try
        {
            var sensors = await api.ListSensorsAsync(cancellationToken);
            var actuators = await api.ListActuatorsAsync(cancellationToken);
            var cameras = await api.ListCamerasAsync(cancellationToken);
            var address = api.BaseUrl.ToString().TrimEnd('/');
            var items = new List<DiscoveredDevice>();
            items.AddRange(sensors.Select(s => new DiscoveredDevice(
                EmulatorSensorPlugin.Id, PluginKinds.Sensor, $"{address}/api/sensors/{s.Id}", s.Name,
                new JsonObject { ["sensorId"] = s.Id, ["metric"] = s.Metric, ["unit"] = s.Unit },
                [new ChannelDescriptor(EmulatorSensorPlugin.ChannelKey, s.Metric, s.Unit)])));
            items.AddRange(actuators.Select(a => new DiscoveredDevice(
                EmulatorActuatorPlugin.Id, PluginKinds.Actuator, $"{address}/api/actuators/{a.Id}", a.Name,
                new JsonObject { ["actuatorId"] = a.Id }, [], a.Kind)));
            items.AddRange(cameras.Select(c => new DiscoveredDevice(
                EmulatorCameraPlugin.Id, PluginKinds.Camera, $"{address}/api/cameras/{c.Id}", c.Name,
                new JsonObject { ["cameraId"] = c.Id }, [])));
            return items;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<EmulatorDiscovery>()
                .LogDebug(ex, "Sensor emulator not reachable at {EmulatorUrl}; nothing to discover", api.BaseUrl);
            return [];
        }
    }
}
