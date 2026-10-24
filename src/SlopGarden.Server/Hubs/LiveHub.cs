using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Hubs;

/// <summary>Browser-facing hub: live readings, actuator/device state, AI activity, chat streaming, notifications.</summary>
[Authorize(Policy = Policies.ReadOnly)]
public sealed class LiveHub : Hub;

public static class LiveEvents
{
    public const string Readings = "readings";
    public const string ActuatorState = "actuatorState";
    public const string DeviceStatus = "deviceStatus";
    public const string AiActivity = "aiActivity";
    public const string Chat = "chat";
    public const string Notification = "notification";
    public const string Proposal = "proposal";
    public const string EmergencyStop = "emergencyStop";
    public const string CameraFrame = "cameraFrame";
    public const string Changed = "changed";
}

public sealed class LiveNotifier(IHubContext<LiveHub> hub)
{
    public Task All(string evt, object payload) => hub.Clients.All.SendAsync(evt, payload);

    public Task User(Guid userId, string evt, object payload) => hub.Clients.User(userId.ToString()).SendAsync(evt, payload);

    /// <summary>Generic "something in this area changed" hint so pages can refetch.</summary>
    public Task Changed(params string[] areas) => hub.Clients.All.SendAsync(LiveEvents.Changed, new { areas });
}
