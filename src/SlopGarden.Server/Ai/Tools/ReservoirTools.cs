using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai.Tools;

public sealed record ReservoirIdInput(Guid ReservoirId);

public sealed record CreateReservoirInput(
    string Name,
    Guid? GardenId = null,
    double? VolumeLiters = null,
    [property: Description("Days between full nutrient solution changes (typical 7–14)")] int? ChangeIntervalDays = null,
    [property: Description("When the solution was last fully changed; defaults to now")] DateTimeOffset? LastChangedAt = null,
    [property: Description("Notes the AI must respect, e.g. nutrient brand and mixing ratio")] string? AiContext = null,
    string? Notes = null);

public sealed record UpdateReservoirInput(
    Guid ReservoirId,
    string? Name = null,
    double? VolumeLiters = null,
    int? ChangeIntervalDays = null,
    string? AiContext = null,
    string? Notes = null);

public sealed record LogReservoirEventInput(
    Guid ReservoirId,
    [property: Description("change (full solution change; resets the change timer) | top_up (water/solution added) | note")] string Kind,
    [property: Description("Liters added (top_up) or new volume (change)")] double? Liters = null,
    string? Notes = null);

public static class ReservoirTools
{
    public const string Category = "Hydroponics";

    private static async Task<Reservoir> FindAsync(ToolContext ctx, Guid id, bool tracking = true) =>
        Ensure.Found(await (tracking ? ctx.Db.Reservoirs : ctx.Db.Reservoirs.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == id, ctx.Ct), "Reservoir");

    public static void Register(ToolRegistry r)
    {
        r.Read<EmptyInput, IReadOnlyList<HydroSystemInfo>>("list_hydro_systems", Category,
            "Supported hydroponic systems (NFT, DWC, RDWC, ebb & flow, drip, Kratky, wick, aeroponics): pump pattern, key metrics and care guidance.",
            (_, _) => Task.FromResult(HydroSystems.All));

        r.Read<GardenIdInput, List<ReservoirStatus>>("list_reservoirs", Category,
            "Nutrient reservoirs with live EC/pH/water temperature/level/flow vs targets, pumps and dosers, beds fed, days since the last solution change, doses since then and alerts.",
            async (ctx, i) => await ctx.Get<OverviewService>().ReservoirsAsync((await GardenTools.ResolveGardenAsync(ctx.Db, i.GardenId, ctx.Ct)).Id, null, ctx.Ct));

        r.Read<ReservoirIdInput, ReservoirStatus>("get_reservoir", Category, "One reservoir's full status (see list_reservoirs).",
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId, false);
                return (await ctx.Get<OverviewService>().ReservoirsAsync(res.GardenId, res.Id, ctx.Ct)).Single();
            });

        r.Mutate<CreateReservoirInput, Reservoir>("create_reservoir", Category, "Create a nutrient reservoir that feeds hydroponic or soilless beds.",
            async (ctx, i) =>
            {
                var garden = await GardenTools.ResolveGardenAsync(ctx.Db, i.GardenId, ctx.Ct);
                var res = new Reservoir
                {
                    GardenId = garden.Id, Name = i.Name, VolumeLiters = i.VolumeLiters, ChangeIntervalDays = i.ChangeIntervalDays ?? 14,
                    LastChangedAt = i.LastChangedAt ?? DateTimeOffset.UtcNow, AiContext = i.AiContext ?? "", Notes = i.Notes ?? "",
                };
                ctx.Db.Reservoirs.Add(res);
                ctx.Db.Audit(ctx.Actor, "reservoir.create", "reservoir", res.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("reservoirs");
                return res;
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Create reservoir '{i.Name}'", "garden", i.GardenId, i.GardenId, "low", Patch.CreateChanges(i))));

        r.Mutate<UpdateReservoirInput, Reservoir>("update_reservoir", Category, "Rename a reservoir or change its volume, change interval or AI notes.",
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId);
                Patch.Apply(res, i, nameof(i.ReservoirId));
                ctx.Db.Audit(ctx.Actor, "reservoir.update", "reservoir", res.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("reservoirs");
                return res;
            },
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId, false);
                return new ProposalPreview($"Update reservoir '{res.Name}'", "reservoir", res.Id, res.GardenId, "low", Patch.UpdateChanges(res, i, nameof(i.ReservoirId)));
            });

        r.Mutate<ReservoirIdInput, Reservoir>("archive_reservoir", Category, "Archive a reservoir. Beds keep their history but are unlinked.",
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId);
                res.ArchivedAt = DateTimeOffset.UtcNow;
                await ctx.Db.Beds.Where(b => b.ReservoirId == res.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.ReservoirId, (Guid?)null), ctx.Ct);
                ctx.Db.Audit(ctx.Actor, "reservoir.archive", "reservoir", res.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("reservoirs", "beds");
                return res;
            },
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId, false);
                return new ProposalPreview($"Archive reservoir '{res.Name}'", "reservoir", res.Id, res.GardenId, "medium", [Patch.Change("archived", false, true)]);
            });

        r.Mutate<LogReservoirEventInput, Reservoir>("log_reservoir_event", Category,
            "Record a full solution change (resets the change timer), a top-up (liters added) or a note in the journal.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Kind, ["change", "top_up", "note"], "kind");
                var res = await FindAsync(ctx, i.ReservoirId);
                var now = DateTimeOffset.UtcNow;
                switch (i.Kind)
                {
                    case "change":
                        res.LastChangedAt = now;
                        res.ToppedUpLitersSinceChange = 0;
                        if (i.Liters is { } vol and > 0) res.VolumeLiters = vol;
                        break;
                    case "top_up":
                        if (i.Liters is not { } l || l <= 0) throw AppException.Bad("liters must be positive for a top-up");
                        res.ToppedUpLitersSinceChange += l;
                        break;
                }
                ctx.Db.JournalEntries.Add(new JournalEntry
                {
                    GardenId = res.GardenId, ReservoirId = res.Id, Kind = i.Kind == "change" ? "reservoir_change" : i.Kind,
                    Quantity = i.Liters, Unit = i.Liters is null ? null : "L", Notes = i.Notes ?? (i.Kind == "change" ? $"Solution changed in {res.Name}" : $"{i.Kind} {res.Name}"),
                    CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId, OccurredAt = now,
                });
                ctx.Db.Audit(ctx.Actor, $"reservoir.{i.Kind}", "reservoir", res.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await ctx.Get<LiveNotifier>().Changed("reservoirs", "journal");
                return res;
            },
            async (ctx, i) =>
            {
                var res = await FindAsync(ctx, i.ReservoirId, false);
                var title = i.Kind switch { "change" => $"Log solution change for '{res.Name}'", "top_up" => $"Log top-up of {i.Liters:0.#} L for '{res.Name}'", _ => $"Add note to '{res.Name}'" };
                return new ProposalPreview(title, "reservoir", res.Id, res.GardenId, "low", Patch.CreateChanges(i, nameof(i.ReservoirId)));
            });
    }
}
