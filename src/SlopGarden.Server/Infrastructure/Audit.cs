using System.Text.Json.Nodes;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

public static class AuditExtensions
{
    /// <summary>Adds an audit row to the context; saved with the caller's next SaveChanges.</summary>
    public static void Audit(this SlopDbContext db, Actor actor, string action, string targetType, Guid? targetId, JsonObject? details = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorType = actor.Type,
            ActorId = actor.UserId,
            ActorLabel = actor.Label,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details ?? new JsonObject(),
        });
    }
}

public sealed class AppException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static AppException NotFound(string what) => new(404, $"{what} not found");
    public static AppException Bad(string message) => new(400, message);
    public static AppException Forbidden(string message = "You don't have permission to do that") => new(403, message);
    public static AppException Conflict(string message) => new(409, message);
}
