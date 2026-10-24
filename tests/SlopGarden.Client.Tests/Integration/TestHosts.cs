using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using SlopGarden.Emulator;
using SlopGarden.Emulator.Simulation;
using Xunit.Abstractions;

namespace SlopGarden.Client.Tests.Integration;

public sealed class EmulatorHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EmulatorHost(WebApplication app)
    {
        _app = app;
        Url = new Uri(app.Urls.First().TrimEnd('/') + "/");
        Simulator = app.Services.GetRequiredService<GardenSimulator>();
    }

    public Uri Url { get; }
    public GardenSimulator Simulator { get; }

    public static async Task<EmulatorHost> StartAsync(ITestOutputHelper output)
    {
        var app = EmulatorApp.Build(["--urls", "http://127.0.0.1:0", "--Emulator:TickMilliseconds", "50"], b =>
            b.Logging.ClearProviders().AddProvider(new XunitLoggerProvider(output, LogLevel.Warning)));
        await app.StartAsync();
        return new EmulatorHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class ClientUnderTest : IAsyncDisposable
{
    private ClientUnderTest(IHost host, string dataDirectory, string hardwareId)
    {
        Host = host;
        DataDirectory = dataDirectory;
        HardwareId = hardwareId;
    }

    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;
    public string DataDirectory { get; }
    public string HardwareId { get; }

    public T Get<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    public static async Task<ClientUnderTest> StartAsync(ITestOutputHelper output, IDictionary<string, string?> settings)
    {
        var dataDirectory = Wait.TempDir("client");
        var hardwareId = $"it-{Guid.NewGuid():N}";
        var config = new Dictionary<string, string?>
        {
            ["Client:DataDirectory"] = dataDirectory,
            ["Client:PluginsDirectory"] = Path.Combine(dataDirectory, "plugins"),
            ["Client:HardwareId"] = hardwareId,
            ["Client:Name"] = "integration-client",
            ["Client:LocalApiPort"] = "0",
            ["Client:ServerUrl"] = "",
        };
        foreach (var (key, value) in settings) config[key] = value;
        var builder = ClientHost.CreateBuilder([], b =>
        {
            b.Configuration.AddInMemoryCollection(config);
            b.Logging.ClearProviders().AddProvider(new XunitLoggerProvider(output));
        });
        var host = builder.Build();
        await host.StartAsync();
        return new ClientUnderTest(host, dataDirectory, hardwareId);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Host.StopAsync(timeout.Token);
        Host.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class Ports
{
    public static int FreeTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static int FreeUdp()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}

/// <summary>A device configuration wired to the emulator's default scenario.</summary>
public sealed class GardenConfig
{
    public Guid MoistureSensor { get; } = Guid.NewGuid();
    public Guid MoistureChannel { get; } = Guid.NewGuid();
    public Guid TankSensor { get; } = Guid.NewGuid();
    public Guid TankChannel { get; } = Guid.NewGuid();
    public Guid BrokenSensor { get; } = Guid.NewGuid();
    public Guid Valve { get; } = Guid.NewGuid();
    public Guid Camera { get; } = Guid.NewGuid();

    public int MaxRunSeconds { get; init; } = 30;
    public int MaxDailyRunSeconds { get; init; } = 20;
    public double TankMinimumPct { get; init; } = 15;

    public DeviceConfigDocument Build(int version = 1) => new(
        version, Guid.NewGuid(), "Integration device", DateTimeOffset.UtcNow, "Europe/Berlin",
        [
            Sensor(MoistureSensor, "Tomato soil", "tomato-soil-moisture", MoistureChannel, Metrics.SoilMoisturePct, "%"),
            Sensor(TankSensor, "Water tank", "water-tank-level", TankChannel, Metrics.WaterLevelPct, "%"),
            Sensor(BrokenSensor, "Unplugged probe", "does-not-exist", Guid.NewGuid(), Metrics.SoilMoisturePct, "%"),
        ],
        [
            new ActuatorConfig(Valve, "Tomato valve", ActuatorKinds.Valve, "emulator-actuator", new JsonObject { ["actuatorId"] = "tomato-valve" },
                true, "off", 500,
                new ActuatorSafety(MaxRunSeconds, MaxDailyRunSeconds, null, null,
                    [new SafetyInterlock("min", TankChannel, "lt", TankMinimumPct, "water tank below minimum")])),
        ],
        [new CameraConfig(Camera, "Tomato cam", "emulator-camera", new JsonObject { ["cameraId"] = "tomato-cam" }, 320, 180, 1, true)],
        [],
        EmergencyStopState.None,
        new ClientSettings(ReadingBatchSeconds: 1),
        []);

    private static SensorConfig Sensor(Guid id, string name, string emulatorId, Guid channelId, string metric, string unit) =>
        new(id, name, "emulator-sensor", new JsonObject { ["sensorId"] = emulatorId, ["metric"] = metric, ["unit"] = unit }, 1, true,
            [new ChannelConfig(channelId, "value", metric, unit, null, 0, 100)]);
}
