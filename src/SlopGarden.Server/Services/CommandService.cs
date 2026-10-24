using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed record CommandResult(Guid CommandId, string Status, string? BlockedReason, double? DurationSec, IReadOnlyList<string> Notes);

public sealed class CommandService(
    SlopDbContext db,
    SafetyService safety,
    DeviceConnections connections,
    IHubContext<DeviceHub> deviceHub,
    LiveNotifier live,
    NotificationService notifications,
    ActivityLog activity)
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(2);

    public async Task<CommandResult> IssueAsync(Guid actuatorId, string action, double? durationSec, double? amountMl, double? level, string reason,
        Actor actor, Guid? proposalId, Guid? scheduleId, CancellationToken ct)
    {
        if (action is not (ActuatorActions.On or ActuatorActions.Off or ActuatorActions.Pulse or ActuatorActions.Dose))
            throw AppException.Bad("action must be on, off, pulse or dose");
        var actuator = await db.Actuators.FirstOrDefaultAsync(a => a.Id == actuatorId, ct) ?? throw AppException.NotFound("Actuator");

        var decision = await safety.CheckAsync(actuator, action, durationSec, amountMl, ct);
        var cmd = new ActuatorCommand
        {
            ActuatorId = actuatorId,
            Action = action,
            DurationSec = decision.DurationSec ?? durationSec,
            AmountMl = amountMl,
            Level = level,
            Reason = reason,
            RequestedByType = actor.Type == ActorTypes.Pat ? ActorTypes.Pat : actor.Type,
            RequestedById = actor.UserId,
            ProposalId = proposalId,
            ScheduleId = scheduleId,
            ExpiresAt = DateTimeOffset.UtcNow + DefaultExpiry,
        };

        if (!decision.Allowed)
        {
            cmd.Status = CommandStatuses.Blocked;
            cmd.BlockedReason = decision.Reason;
            db.ActuatorCommands.Add(cmd);
            db.Audit(actor, "actuator.command.blocked", "actuator", actuatorId, new() { ["action"] = action, ["reason"] = decision.Reason });
            await db.SaveChangesAsync(ct);
            await notifications.CreateAsync(new NotificationRequest(actuator.GardenId, Severities.Warning, "safety",
                $"Command blocked: {actuator.Name}", $"{action} requested by {actor.Label} was blocked: {decision.Reason}", "actuator", actuator.Id,
                $"blocked:{actuator.Id}:{decision.Reason}"), ct);
            return new CommandResult(cmd.Id, cmd.Status, decision.Reason, null, decision.Notes);
        }

        db.ActuatorCommands.Add(cmd);
        db.Audit(actor, "actuator.command", "actuator", actuatorId, new() { ["action"] = action, ["durationSec"] = cmd.DurationSec, ["reason"] = reason });
        await db.SaveChangesAsync(ct);
        var line = $"{actuator.Name}: {action} requested by {actor.Label}";
        activity.Note(actuator.GardenId, actuator.BedId is null ? null : "bed", actuator.BedId, line);
        if (actuator.PlantId is { } plantId) activity.Plant(plantId, line);
        await TrySendAsync(cmd, actuator.DeviceId, ct);
        await live.Changed("commands");
        return new CommandResult(cmd.Id, cmd.Status, null, cmd.DurationSec, decision.Notes);
    }

    public async Task TrySendAsync(ActuatorCommand cmd, Guid deviceId, CancellationToken ct)
    {
        if (connections.Get(deviceId) is not { } conn) return;
        var msg = new ActuatorCommandMessage(cmd.Id, cmd.ActuatorId, cmd.Action, cmd.DurationSec, cmd.AmountMl, cmd.Level,
            cmd.CreatedAt, cmd.ExpiresAt, cmd.RequestedByType, cmd.Reason);
        await deviceHub.Clients.Client(conn).SendAsync(DeviceHubMethods.ExecuteCommand, msg, ct);
        cmd.Status = CommandStatuses.Sent;
        cmd.SentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends queued, unexpired commands after a device reconnects.</summary>
    public async Task FlushPendingAsync(Guid deviceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await db.ActuatorCommands
            .Where(c => c.Status == CommandStatuses.Pending && db.Actuators.Any(a => a.Id == c.ActuatorId && a.DeviceId == deviceId))
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        foreach (var c in pending)
        {
            if (c.ExpiresAt < now)
            {
                c.Status = CommandStatuses.Expired;
                continue;
            }
            await TrySendAsync(c, deviceId, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
