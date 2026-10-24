using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;
using Xunit.Abstractions;

namespace SlopGarden.Client.Tests.TestSupport;

public sealed class XunitLoggerProvider(ITestOutputHelper output, LogLevel minLevel = LogLevel.Information) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName, minLevel);

    public void Dispose()
    {
    }

    private sealed class XunitLogger(ITestOutputHelper output, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {category.Split('.')[^1]}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
            }
            catch (InvalidOperationException)
            {
                // The test already finished; xUnit rejects late output.
            }
        }
    }
}

public sealed class RecordingReporter : IDeviceReporter
{
    public ConcurrentQueue<DeviceEventReport> Events { get; } = new();
    public ConcurrentQueue<CommandAck> Acks { get; } = new();
    public ConcurrentQueue<ActuatorRunReport> Runs { get; } = new();
    public ConcurrentQueue<ActuatorStateReport> States { get; } = new();
    public ConcurrentQueue<ConfigAppliedReport> Configs { get; } = new();

    public void Event(string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null) =>
        Events.Enqueue(new DeviceEventReport(DateTimeOffset.UtcNow, severity, category, message, sensorId, actuatorId, cameraId, code));

    public void EventThrottled(string key, TimeSpan interval, string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null) =>
        Event(severity, category, message, sensorId, actuatorId, cameraId, code);

    public void Ack(Guid commandId, string status, string? reason) => Acks.Enqueue(new CommandAck(commandId, status, reason, DateTimeOffset.UtcNow));

    public void Run(ActuatorRunReport run) => Runs.Enqueue(run);

    public void ActuatorState(ActuatorStateReport state) => States.Enqueue(state);

    public void ConfigApplied(ConfigAppliedReport report) => Configs.Enqueue(report);

    public IReadOnlyList<string> AckStatuses(Guid commandId) => Acks.Where(a => a.CommandId == commandId).Select(a => a.Status).ToList();
}

/// <summary>In-memory actuator plugin recording every state change; can be told to fail.</summary>
public sealed class FakeActuatorPlugin(string pluginId = "fake-actuator") : IActuatorPlugin
{
    public string PluginId => pluginId;
    public string DisplayName => "Fake actuator";
    public string Description => "Test double";
    public JsonObject SettingsSchema => new() { ["type"] = "object" };
    public IReadOnlyList<string> SupportedKinds => ActuatorKinds.All;

    public ConcurrentDictionary<Guid, FakeActuator> Devices { get; } = new();
    public bool ThrowOnCreate { get; set; }

    public IActuator Create(PluginContext context)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("hardware missing");
        var device = new FakeActuator();
        Devices[context.ComponentId] = device;
        return device;
    }
}

public sealed class FakeActuator : IActuator
{
    public ConcurrentQueue<bool> Changes { get; } = new();
    public bool On { get; private set; }
    public double? Level { get; private set; }
    public bool FailOn { get; set; }
    public bool FailOff { get; set; }
    public bool Disposed { get; private set; }

    public ValueTask SetStateAsync(bool on, double? level, CancellationToken cancellationToken)
    {
        if (on && FailOn) throw new IOException("relay stuck");
        if (!on && FailOff) throw new IOException("relay stuck");
        On = on;
        Level = on ? level : null;
        Changes.Enqueue(on);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public static class Configs
{
    public static ActuatorConfig Actuator(Guid? id = null, ActuatorSafety? safety = null, double? flow = null, bool enabled = true,
        string failsafe = "off", string pluginId = "fake-actuator", string kind = ActuatorKinds.Valve, JsonObject? settings = null) =>
        new(id ?? Guid.NewGuid(), "Test valve", kind, pluginId, settings ?? new JsonObject(), enabled, failsafe, flow, safety ?? ActuatorSafety.None);

    public static ActuatorSafety Safety(int? maxRun = null, int? maxDaily = null, int? minBetween = null, double? maxDailyMl = null, params SafetyInterlock[] interlocks) =>
        new(maxRun, maxDaily, minBetween, maxDailyMl, interlocks);

    public static DeviceConfigDocument Document(int version = 1, string timeZoneId = "UTC", IReadOnlyList<SensorConfig>? sensors = null,
        IReadOnlyList<ActuatorConfig>? actuators = null, IReadOnlyList<CameraConfig>? cameras = null, IReadOnlyList<ScheduleConfig>? schedules = null,
        EmergencyStopState? estop = null, ClientSettings? settings = null) =>
        new(version, Guid.NewGuid(), "Test device", DateTimeOffset.UtcNow, timeZoneId, sensors ?? [], actuators ?? [], cameras ?? [], schedules ?? [],
            estop ?? EmergencyStopState.None, settings ?? ClientSettings.Default, []);

    public static IServiceProvider EmptyServices { get; } = new ServiceCollection().BuildServiceProvider();

    public static ILoggerFactory NullLogs => NullLoggerFactory.Instance;
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting until {because}.");
            await Task.Delay(25);
        }
    }

    public static async Task<T> ForAsync<T>(Func<T?> probe, TimeSpan timeout, string because) where T : class
    {
        T? result = null;
        await UntilAsync(() => (result = probe()) is not null, timeout, because);
        return result!;
    }

    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"slopgarden-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
