using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;

namespace SlopGarden.Server.Services;

/// <summary>Pushes emergency-stop state to every connected device immediately (not debounced) and refreshes configs.</summary>
public sealed class EmergencyStopService(
    SlopDbContext db,
    DeviceConfigService configs,
    DeviceConnections connections,
    IHubContext<DeviceHub> hub,
    LiveNotifier live)
{
    public async Task<int> ApplyAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved).Select(d => d.Id).ToListAsync(ct);
        var affected = 0;
        foreach (var id in devices)
        {
            var doc = await configs.SyncAsync(id, forcePush: false, ct);
            if (doc is null) continue;
            affected += doc.EmergencyStop.DeviceStopped ? doc.Actuators.Count : doc.EmergencyStop.StoppedActuatorIds.Count;
            if (connections.Get(id) is { } conn) await hub.Clients.Client(conn).SendAsync(DeviceHubMethods.EmergencyStop, doc.EmergencyStop, ct);
        }
        await db.ActuatorCommands.Where(c => c.Status == CommandStatuses.Pending).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatuses.Cancelled), ct);
        var active = await db.EmergencyStops.AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        await live.All(LiveEvents.EmergencyStop, new { active });
        await live.Changed("safety");
        return affected;
    }
}
