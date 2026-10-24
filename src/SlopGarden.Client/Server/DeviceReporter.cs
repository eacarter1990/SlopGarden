using System.Collections.Concurrent;
using SlopGarden.Client.Storage;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

public static class EventSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";
}

public static class EventCategories
{
    public const string Safety = "safety";
    public const string Sensor = "sensor";
    public const string Actuator = "actuator";
    public const string Camera = "camera";
    public const string Config = "config";
    public const string Plugin = "plugin";
    public const string System = "system";
}

/// <summary>Everything the client reports to the server. Durable reports go through the outbox.</summary>
public interface IDeviceReporter
{
    /// <param name="code">Optional machine-readable code, e.g. one of <see cref="SafetyEventCodes"/>.</param>
    void Event(string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null);

    /// <summary>Reports an event at most once per <paramref name="interval"/> for the given key.</summary>
    void EventThrottled(string key, TimeSpan interval, string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null);

    void Ack(Guid commandId, string status, string? reason);
    void Run(ActuatorRunReport run);
    void ActuatorState(ActuatorStateReport state);
    void ConfigApplied(ConfigAppliedReport report);
}

public sealed class DeviceReporter(Outbox outbox, IServerLink link, TimeProvider time, ILogger<DeviceReporter> logger) : IDeviceReporter
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _throttle = new();

    public void Event(string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null) =>
        Append(DeviceHubMethods.ReportEvent, new DeviceEventReport(time.GetUtcNow(), severity, category, message, sensorId, actuatorId, cameraId, code));

    public void EventThrottled(string key, TimeSpan interval, string severity, string category, string message, Guid? sensorId = null, Guid? actuatorId = null, Guid? cameraId = null, string? code = null)
    {
        var now = time.GetUtcNow();
        if (_throttle.TryGetValue(key, out var last) && now - last < interval) return;
        _throttle[key] = now;
        Event(severity, category, message, sensorId, actuatorId, cameraId, code);
    }

    public void Ack(Guid commandId, string status, string? reason) =>
        Append(DeviceHubMethods.AckCommand, new CommandAck(commandId, status, reason, time.GetUtcNow()));

    public void Run(ActuatorRunReport run) => Append(DeviceHubMethods.ReportActuatorRun, run);

    public void ConfigApplied(ConfigAppliedReport report) => Append(DeviceHubMethods.ReportConfigApplied, report);

    public void ActuatorState(ActuatorStateReport state)
    {
        if (link.IsConnected) link.Post(DeviceHubMethods.ReportActuatorState, state);
    }

    private void Append(string method, object payload)
    {
        try
        {
            outbox.AppendMessage(method, payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not queue {HubMethod} report", method);
        }
    }
}
