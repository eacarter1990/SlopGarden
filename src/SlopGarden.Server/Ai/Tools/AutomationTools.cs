using System.ComponentModel;
using System.Text.Json.Nodes;
using Cronos;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai.Tools;

public sealed record ListSchedulesInput(Guid? GardenId = null, Guid? ActuatorId = null);

public sealed record CreateScheduleInput(
    string Name,
    [property: Description("actuator (runs on the device, works offline) | ai_review (how often garden data is sent to the AI) | camera_snapshot | report")] string Kind,
    [property: Description("5-field cron in the garden's timezone, e.g. '0 7 * * *' = daily 07:00, '*/30 * * * *' = every 30 min")] string Cron,
    Guid? GardenId = null,
    Guid? ActuatorId = null,
    [property: Description("Seconds the actuator runs each time (actuator schedules)")] int? DurationSec = null,
    double? AmountMl = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    [property: Description("Extra options, e.g. { \"prompt\": \"Check soil moisture and water if needed\" } for ai_review")] JsonObject? Payload = null,
    bool? Enabled = null);

public sealed record UpdateScheduleInput(
    Guid ScheduleId,
    string? Name = null,
    string? Cron = null,
    int? DurationSec = null,
    double? AmountMl = null,
    JsonObject? Payload = null,
    bool? Enabled = null);

public sealed record ScheduleIdInput(Guid ScheduleId);
public sealed record ScheduleDto(Schedule Schedule, string? ActuatorName, IReadOnlyList<DateTimeOffset> NextRuns);

public sealed record ListSafetyInput([property: Description("global | garden | bed | plant | device | actuator; omit for all")] string? ScopeType = null, Guid? ScopeId = null);

public sealed record SetSafetyLimitInput(
    [property: Description("Existing limit id to update; omit to create")] Guid? LimitId = null,
    [property: Description("global | garden | bed | plant | device | actuator")] string? ScopeType = null,
    Guid? ScopeId = null,
    [property: Description("max_run_seconds | max_daily_run_seconds | min_seconds_between_runs | max_daily_amount_ml | tank_low_lockout | max_temperature_cutoff | min_temperature_cutoff")] string? LimitType = null,
    [property: Description("Limit value: seconds, ml, tank % or °C depending on type")] double? Value = null,
    [property: Description("Only apply to this actuator kind (e.g. fertilizer_doser); omit for the sensible default kinds")] string? ActuatorKind = null,
    [property: Description("Sensor channel watched by interlock limits (tank level, temperature)")] Guid? ChannelId = null,
    [property: Description("lt | gt — interlock trips when channel <op> value")] string? Operator = null,
    bool? Enabled = null,
    string? Notes = null);

public sealed record SafetyLimitIdInput(Guid LimitId);

public sealed record TriggerStopInput(
    [property: Description("global | garden | bed | plant | device | actuator")] string ScopeType,
    Guid? ScopeId,
    string Reason);

public sealed record ReleaseStopInput(Guid StopId);

public sealed record StopResult(EmergencyStop Stop, int AffectedActuators);

public static class AutomationTools
{
    public const string Category = "Automation & safety";

    private static Task Changed(ToolContext ctx, params string[] areas) => ctx.Get<LiveNotifier>().Changed(areas);

    public static IReadOnlyList<DateTimeOffset> NextRuns(string cron, string tz, int count = 3)
    {
        try
        {
            var expr = CronExpression.Parse(cron);
            var zone = TimeZones.Find(tz);
            var list = new List<DateTimeOffset>();
            var from = DateTimeOffset.UtcNow;
            for (var n = 0; n < count; n++)
            {
                var next = expr.GetNextOccurrence(from, zone);
                if (next is null) break;
                list.Add(next.Value);
                from = next.Value;
            }
            return list;
        }
        catch (CronFormatException)
        {
            return [];
        }
    }

    private static void ValidateCron(string cron)
    {
        try
        {
            CronExpression.Parse(cron);
        }
        catch (CronFormatException ex)
        {
            throw AppException.Bad($"Invalid cron '{cron}': {ex.Message}");
        }
    }

    public static void Register(ToolRegistry r)
    {
        r.Read<ListSchedulesInput, List<ScheduleDto>>("list_schedules", Category, "List schedules (actuator runs, AI review cadence, snapshots) with their next run times.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Schedules.AsNoTracking().AsQueryable();
                if (i.GardenId is { } g) q = q.Where(s => s.GardenId == g);
                if (i.ActuatorId is { } a) q = q.Where(s => s.ActuatorId == a);
                var list = await q.OrderBy(s => s.Name).ToListAsync(ctx.Ct);
                var names = await ctx.Db.Actuators.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ctx.Ct);
                return list.Select(s => new ScheduleDto(s, s.ActuatorId is { } aid ? names.GetValueOrDefault(aid) : null, s.Enabled ? NextRuns(s.Cron, s.TimezoneId) : [])).ToList();
            });

        r.Mutate<CreateScheduleInput, ScheduleDto>("create_schedule", Category,
            "Create a schedule. Actuator schedules are pushed to the device and keep running offline (always within safety limits). ai_review schedules control how often the AI reviews the garden.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Kind, ScheduleKinds.All, "kind");
                ValidateCron(i.Cron);
                Garden garden;
                Actuator? act = null;
                if (i.Kind == ScheduleKinds.Actuator)
                {
                    act = Ensure.Found(await ctx.Db.Actuators.FirstOrDefaultAsync(a => a.Id == i.ActuatorId, ctx.Ct), "Actuator");
                    if (i.DurationSec is null or <= 0 && i.AmountMl is null) throw AppException.Bad("durationSec (or amountMl for dosers) is required for actuator schedules");
                    garden = await GardenTools.ResolveGardenAsync(ctx.Db, act.GardenId, ctx.Ct);
                }
                else
                {
                    garden = await GardenTools.ResolveGardenAsync(ctx.Db, i.GardenId, ctx.Ct);
                }
                var s = new Schedule
                {
                    GardenId = garden.Id, Name = i.Name, Kind = i.Kind, Cron = i.Cron, TimezoneId = garden.TimezoneId, ActuatorId = act?.Id,
                    DurationSec = i.DurationSec, AmountMl = i.AmountMl, BedId = i.BedId, PlantId = i.PlantId,
                    TargetType = i.PlantId is not null ? "plant" : i.BedId is not null ? "bed" : act is not null ? "actuator" : "garden",
                    Payload = i.Payload ?? new JsonObject(), Enabled = i.Enabled ?? true, CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId, ProposalId = ctx.ProposalId,
                };
                s.NextRunAt = NextRuns(s.Cron, s.TimezoneId, 1).FirstOrDefault();
                ctx.Db.Schedules.Add(s);
                ctx.Db.Audit(ctx.Actor, "schedule.create", "schedule", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                if (act is not null) ctx.Get<ConfigPushQueue>().Dirty(act.DeviceId);
                await Changed(ctx, "schedules");
                return new ScheduleDto(s, act?.Name, NextRuns(s.Cron, s.TimezoneId));
            },
            async (ctx, i) =>
            {
                var act = i.ActuatorId is { } aid ? await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ctx.Ct) : null;
                var title = act is not null ? $"Schedule '{act.Name}': {i.DurationSec}s at '{i.Cron}'" : $"Create {i.Kind} schedule '{i.Name}' ({i.Cron})";
                return new ProposalPreview(title, act is not null ? "actuator" : "garden", act?.Id ?? i.GardenId, act?.GardenId ?? i.GardenId, "medium", Patch.CreateChanges(i));
            });

        r.Mutate<UpdateScheduleInput, ScheduleDto>("update_schedule", Category, "Change a schedule's cron, duration, payload or enabled flag.",
            async (ctx, i) =>
            {
                if (i.Cron is not null) ValidateCron(i.Cron);
                var s = Ensure.Found(await ctx.Db.Schedules.FirstOrDefaultAsync(x => x.Id == i.ScheduleId, ctx.Ct), "Schedule");
                Patch.Apply(s, i, nameof(i.ScheduleId));
                s.NextRunAt = NextRuns(s.Cron, s.TimezoneId, 1).FirstOrDefault();
                ctx.Db.Audit(ctx.Actor, "schedule.update", "schedule", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                if (s.ActuatorId is { } aid) ctx.Get<ConfigPushQueue>().Dirty(await ctx.Db.Actuators.Where(a => a.Id == aid).Select(a => a.DeviceId).FirstAsync(ctx.Ct));
                await Changed(ctx, "schedules");
                return new ScheduleDto(s, null, NextRuns(s.Cron, s.TimezoneId));
            },
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ScheduleId, ctx.Ct), "Schedule");
                return new ProposalPreview($"Update schedule '{s.Name}'", s.ActuatorId is not null ? "actuator" : "garden", s.ActuatorId ?? s.GardenId, s.GardenId, "medium", Patch.UpdateChanges(s, i, nameof(i.ScheduleId)));
            });

        r.Mutate<ScheduleIdInput, bool>("delete_schedule", Category, "Delete a schedule.",
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Schedules.FirstOrDefaultAsync(x => x.Id == i.ScheduleId, ctx.Ct), "Schedule");
                ctx.Db.Schedules.Remove(s);
                ctx.Db.Audit(ctx.Actor, "schedule.delete", "schedule", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                if (s.ActuatorId is { } aid) ctx.Get<ConfigPushQueue>().Dirty(await ctx.Db.Actuators.Where(a => a.Id == aid).Select(a => a.DeviceId).FirstAsync(ctx.Ct));
                await Changed(ctx, "schedules");
                return true;
            },
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ScheduleId, ctx.Ct), "Schedule");
                return new ProposalPreview($"Delete schedule '{s.Name}'", "garden", s.GardenId, s.GardenId, "medium", [Patch.Change("schedule", s.Name, null)]);
            });

        // ---------------------------------------------------------------- safety limits
        r.Read<ListSafetyInput, List<SafetyLimit>>("list_safety_limits", Category, "List configured hard safety limits at every scope.",
            async (ctx, i) =>
            {
                var q = ctx.Db.SafetyLimits.AsNoTracking().AsQueryable();
                if (i.ScopeType is { } st) q = q.Where(l => l.ScopeType == st);
                if (i.ScopeId is { } sid) q = q.Where(l => l.ScopeId == sid);
                return await q.OrderBy(l => l.ScopeType).ThenBy(l => l.LimitType).ToListAsync(ctx.Ct);
            });

        r.Read<ActuatorIdInput, ActuatorSafety>("get_effective_safety", Category, "The effective (most restrictive) safety limits and interlocks that apply to one actuator.",
            async (ctx, i) => await ctx.Get<SafetyService>().ResolveAsync(Ensure.Found(await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(a => a.Id == i.ActuatorId, ctx.Ct), "Actuator"), ctx.Ct));

        r.Mutate<SetSafetyLimitInput, SafetyLimit>("set_safety_limit", Category,
            "Create or change a hard safety limit. The AI may only propose this; a human must approve.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.ScopeType, Data.ScopeTypes.All, "scopeType");
                Ensure.OneOf(i.LimitType, SafetyLimitTypes.All, "limitType");
                Ensure.OneOf(i.Operator, ["lt", "lte", "gt", "gte"], "operator");
                Ensure.OneOf(i.ActuatorKind, ActuatorKinds.All, "actuatorKind");
                SafetyLimit l;
                if (i.LimitId is { } id)
                {
                    l = Ensure.Found(await ctx.Db.SafetyLimits.FirstOrDefaultAsync(x => x.Id == id, ctx.Ct), "Safety limit");
                }
                else
                {
                    if (i.LimitType is null || i.Value is null || i.ScopeType is null) throw AppException.Bad("scopeType, limitType and value are required");
                    if (i.ScopeType != Data.ScopeTypes.Global && i.ScopeId is null) throw AppException.Bad("scopeId is required unless scopeType=global");
                    l = new SafetyLimit { CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId, ProposalId = ctx.ProposalId };
                    ctx.Db.SafetyLimits.Add(l);
                }
                Patch.Apply(l, i, nameof(i.LimitId));
                if (SafetyLimitTypes.Interlocks.Contains(l.LimitType))
                {
                    if (l.ChannelId is null) throw AppException.Bad($"{l.LimitType} needs channelId (the sensor channel to watch)");
                    l.Operator ??= SafetyLimitTypes.DefaultOperator(l.LimitType);
                }
                if (l.Value < 0) throw AppException.Bad("value must be >= 0");
                ctx.Db.Audit(ctx.Actor, "safety_limit.set", "safety_limit", l.Id, new() { ["type"] = l.LimitType, ["value"] = l.Value });
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty();
                await Changed(ctx, "safety");
                return l;
            },
            async (ctx, i) =>
            {
                var existing = i.LimitId is { } id ? await ctx.Db.SafetyLimits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.Ct) : null;
                var type = i.LimitType ?? existing?.LimitType ?? "limit";
                var changes = existing is null ? Patch.CreateChanges(i) : Patch.UpdateChanges(existing, i, nameof(i.LimitId));
                return new ProposalPreview($"{(existing is null ? "Add" : "Change")} safety limit {type}", i.ScopeType ?? existing?.ScopeType ?? "global",
                    i.ScopeId ?? existing?.ScopeId, null, "high", changes);
            });

        r.Mutate<SafetyLimitIdInput, bool>("delete_safety_limit", Category, "Delete a safety limit. Requires approval when proposed by the AI.",
            async (ctx, i) =>
            {
                var l = Ensure.Found(await ctx.Db.SafetyLimits.FirstOrDefaultAsync(x => x.Id == i.LimitId, ctx.Ct), "Safety limit");
                ctx.Db.SafetyLimits.Remove(l);
                ctx.Db.Audit(ctx.Actor, "safety_limit.delete", "safety_limit", l.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty();
                await Changed(ctx, "safety");
                return true;
            },
            async (ctx, i) =>
            {
                var l = Ensure.Found(await ctx.Db.SafetyLimits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.LimitId, ctx.Ct), "Safety limit");
                return new ProposalPreview($"Remove safety limit {l.LimitType} ({l.Value})", l.ScopeType, l.ScopeId, null, "high", [Patch.Change(l.LimitType, l.Value, null)]);
            });

        // ---------------------------------------------------------------- emergency stops
        r.Read<EmptyInput, List<EmergencyStop>>("list_emergency_stops", Category, "Active emergency stops.",
            async (ctx, _) => await ctx.Db.EmergencyStops.AsNoTracking().Where(s => s.Active).OrderByDescending(s => s.ActivatedAt).ToListAsync(ctx.Ct));

        r.Mutate<TriggerStopInput, StopResult>("trigger_emergency_stop", Category,
            "EMERGENCY STOP: immediately switch off and lock every actuator in the scope (global, garden, bed, plant, device or actuator). Anyone, including the AI, may trigger it without approval.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.ScopeType, Data.ScopeTypes.All, "scopeType");
                if (i.ScopeType != Data.ScopeTypes.Global && i.ScopeId is null) throw AppException.Bad("scopeId is required unless scopeType=global");
                var stop = new EmergencyStop { ScopeType = i.ScopeType, ScopeId = i.ScopeId, Reason = i.Reason, ActivatedByType = ctx.Actor.Type, ActivatedById = ctx.Actor.UserId };
                ctx.Db.EmergencyStops.Add(stop);
                ctx.Db.Audit(ctx.Actor, "estop.trigger", i.ScopeType, i.ScopeId, new() { ["reason"] = i.Reason });
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                var affected = await ctx.Get<EmergencyStopService>().ApplyAsync(ctx.Ct);
                await ctx.Get<NotificationService>().CreateAsync(new NotificationRequest(null, Severities.Critical, "safety",
                    $"Emergency stop: {i.ScopeType}", $"Triggered by {ctx.Actor.Label}: {i.Reason}", i.ScopeType, i.ScopeId), ctx.Ct);
                return new StopResult(stop, affected);
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Emergency stop ({i.ScopeType})", i.ScopeType, i.ScopeId, null, "low", [])),
            minRole: Roles.ReadWrite, noApprovalNeeded: true);

        r.Mutate<ReleaseStopInput, StopResult>("release_emergency_stop", Category, "Release an emergency stop so actuators may run again. Needs approval when requested by the AI.",
            async (ctx, i) =>
            {
                var stop = Ensure.Found(await ctx.Db.EmergencyStops.FirstOrDefaultAsync(s => s.Id == i.StopId, ctx.Ct), "Emergency stop");
                stop.Active = false;
                stop.ReleasedAt = DateTimeOffset.UtcNow;
                stop.ReleasedById = ctx.Actor.UserId;
                ctx.Db.Audit(ctx.Actor, "estop.release", stop.ScopeType, stop.ScopeId);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                var affected = await ctx.Get<EmergencyStopService>().ApplyAsync(ctx.Ct);
                return new StopResult(stop, affected);
            },
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.EmergencyStops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.StopId, ctx.Ct), "Emergency stop");
                return new ProposalPreview($"Release emergency stop ({s.ScopeType}: {s.Reason})", s.ScopeType, s.ScopeId, null, "high", [Patch.Change("active", true, false)]);
            });
    }
}
