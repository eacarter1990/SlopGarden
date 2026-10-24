using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai.Tools;

public static class GardenVocab
{
    public static readonly string[] Environments = ["outdoor", "indoor", "greenhouse", "mixed"];
    public static readonly string[] BedKinds = ["raised_bed", "in_ground", "container", "greenhouse_bench", "hydroponic", "vertical", "orchard_row", "other"];
    public static readonly string[] Irrigation = ["drip", "sprinkler", "soaker", "manual", "hydroponic", "none"];
    public static readonly string[] Sun = ["full_sun", "part_sun", "part_shade", "full_shade", "grow_light"];
    public static readonly string[] Stages = ["planned", "seed", "germinating", "seedling", "vegetative", "flowering", "fruiting", "harvesting", "dormant", "removed"];
    public static readonly string[] StartedFrom = ["seed", "transplant", "cutting", "unknown"];
    public static readonly string[] Autonomy = ["propose_all", "standing_approvals"];
    public static readonly string[] JournalKinds = ["note", "sown", "transplanted", "watered", "fertilized", "pruned", "pest", "disease", "harvest", "observation", "stage_change", "reservoir_change", "top_up", "dose"];
    public static readonly string[] Categories = ["vegetable", "fruit", "herb", "flower", "other"];
    public static readonly string[] WaterNeeds = ["low", "medium", "high"];
}

public sealed record GardenIdInput([property: Description("Garden id. Omit to use the first garden.")] Guid? GardenId = null);
public sealed record EmptyInput;

public sealed record CreateGardenInput(
    string Name,
    string? Description = null,
    [property: Description("outdoor | indoor | greenhouse | mixed")] string? Environment = null,
    [property: Description("Manual latitude; leave empty to inherit from the first device")] double? Latitude = null,
    double? Longitude = null,
    string? LocationLabel = null,
    [property: Description("IANA timezone, e.g. Europe/Berlin; leave empty to inherit from the device")] string? TimezoneId = null,
    string? HardinessZone = null,
    string? ClimateNotes = null,
    [property: Description("Free-form notes the AI must take into account for this garden")] string? AiContext = null);

public sealed record UpdateGardenInput(
    Guid GardenId,
    string? Name = null,
    string? Description = null,
    string? Environment = null,
    [property: Description("device (inherit from client) | manual")] string? LocationSource = null,
    double? Latitude = null,
    double? Longitude = null,
    double? ElevationM = null,
    string? LocationLabel = null,
    string? TimezoneId = null,
    [property: Description("device | manual")] string? TimezoneSource = null,
    string? HardinessZone = null,
    string? ClimateNotes = null,
    string? AiContext = null,
    [property: Description("propose_all (every AI change needs approval) | standing_approvals (approved routine actions may repeat autonomously)")] string? AiAutonomy = null,
    [property: Description("Default growing medium for beds that don't set their own: soil | soilless | hydroponic")] string? DefaultGrowingMedium = null);

public sealed record ListBedsInput(Guid? GardenId = null, bool IncludeArchived = false);

public sealed record CreateBedInput(
    Guid GardenId,
    string Name,
    [property: Description("raised_bed | in_ground | container | greenhouse_bench | hydroponic | vertical | orchard_row | other")] string? Kind = null,
    [property: Description("Overrides the garden environment: outdoor | indoor | greenhouse")] string? Environment = null,
    double? WidthCm = null,
    double? LengthCm = null,
    double? DepthCm = null,
    string? SoilType = null,
    string? SoilNotes = null,
    [property: Description("drip | sprinkler | soaker | manual | hydroponic | none")] string? IrrigationType = null,
    [property: Description("full_sun | part_sun | part_shade | full_shade | grow_light")] string? SunExposure = null,
    [property: Description("soil | soilless (coco, perlite, rockwool) | hydroponic. Omit to inherit the garden default")] string? GrowingMedium = null,
    [property: Description("For hydroponic beds: nft | dwc | rdwc | ebb_flow | drip_recirculating | drip_to_waste | kratky | wick | aeroponic (see list_hydro_systems)")] string? HydroSystem = null,
    [property: Description("Reservoir feeding this bed (hydroponic/soilless)")] Guid? ReservoirId = null,
    double? LayoutX = null,
    double? LayoutY = null,
    string? AiContext = null,
    string? Notes = null);

public sealed record UpdateBedInput(
    Guid BedId,
    string? Name = null,
    string? Kind = null,
    string? Environment = null,
    double? WidthCm = null,
    double? LengthCm = null,
    double? DepthCm = null,
    string? SoilType = null,
    string? SoilNotes = null,
    string? IrrigationType = null,
    string? SunExposure = null,
    [property: Description("soil | soilless (coco, perlite, rockwool) | hydroponic. Omit to inherit the garden default")] string? GrowingMedium = null,
    [property: Description("For hydroponic beds: nft | dwc | rdwc | ebb_flow | drip_recirculating | drip_to_waste | kratky | wick | aeroponic (see list_hydro_systems)")] string? HydroSystem = null,
    [property: Description("Reservoir feeding this bed (hydroponic/soilless)")] Guid? ReservoirId = null,

    double? LayoutX = null,
    double? LayoutY = null,
    string? AiContext = null,
    string? Notes = null);

public sealed record BedIdInput(Guid BedId);

public sealed record ListPlantsInput(Guid? GardenId = null, Guid? BedId = null, bool IncludeRemoved = false);

public sealed record PlantIdInput(Guid PlantId);

public sealed record CreatePlantInput(
    Guid BedId,
    string Name,
    [property: Description("Plant profile id or slug (see list_plant_profiles), e.g. 'tomato'")] string? Profile = null,
    string? Variety = null,
    [property: Description("Number of plants in this planting (default 1)")] int? Quantity = null,
    [property: Description("planned | seed | germinating | seedling | vegetative | flowering | fruiting | harvesting | dormant")] string? Stage = null,
    [property: Description("seed | transplant | cutting | unknown")] string? StartedFrom = null,
    DateOnly? PlantedAt = null,
    DateOnly? ExpectedHarvestStart = null,
    DateOnly? ExpectedHarvestEnd = null,
    double? LayoutX = null,
    double? LayoutY = null,
    string? AiContext = null,
    string? Notes = null);

public sealed record UpdatePlantInput(
    Guid PlantId,
    string? Name = null,
    string? Profile = null,
    Guid? BedId = null,
    string? Variety = null,
    int? Quantity = null,
    string? Stage = null,
    string? StartedFrom = null,
    DateOnly? PlantedAt = null,
    DateOnly? ExpectedHarvestStart = null,
    DateOnly? ExpectedHarvestEnd = null,
    string? HarvestPredictionNotes = null,
    double? LayoutX = null,
    double? LayoutY = null,
    string? AiContext = null,
    string? Notes = null);

public sealed record ListProfilesInput([property: Description("Optional text filter on name/scientific name/category")] string? Search = null);
public sealed record ProfileInput([property: Description("Profile id or slug")] string Profile);

public sealed record UpsertPlantProfileInput(
    [property: Description("Existing profile id or slug to update; omit to create a new profile")] string? Profile = null,
    string? Slug = null,
    string? CommonName = null,
    string? ScientificName = null,
    string? Family = null,
    string? Category = null,
    string? Description = null,
    [property: Description("low | medium | high")] string? WaterNeed = null,
    double? SoilMoistureMinPct = null,
    double? SoilMoistureMaxPct = null,
    double? LightHoursMin = null,
    double? LightHoursMax = null,
    double? DliMin = null,
    double? AirTempMinC = null,
    double? AirTempMaxC = null,
    double? SoilTempMinC = null,
    double? HumidityMinPct = null,
    double? HumidityMaxPct = null,
    double? PhMin = null,
    double? PhMax = null,
    double? EcMin = null,
    double? EcMax = null,
    string? FertilizerNotes = null,
    int? DaysToGermination = null,
    int? DaysToMaturityMin = null,
    int? DaysToMaturityMax = null,
    double? SpacingCm = null,
    [property: Description("none | light | hard")] string? FrostTolerance = null,
    [property: Description("Citations/URLs backing the values")] List<string>? Sources = null);

public sealed record SetCareTargetInput(
    [property: Description("plant | bed | reservoir (EC/pH/water temperature of a nutrient reservoir)")] string ScopeType,
    Guid? PlantId,
    Guid? BedId,
    [property: Description("Metric id, e.g. soil_moisture_pct, air_temperature_c, light_hours, ph, ec_ms_cm")] string Metric,
    double? MinValue = null,
    double? MaxValue = null,
    double? IdealValue = null,
    string? Rationale = null,
    Guid? ReservoirId = null);

public sealed record CareTargetIdInput(Guid CareTargetId);

public sealed record ListCareTargetsInput(Guid? PlantId = null, Guid? BedId = null, Guid? ReservoirId = null);

public sealed record ListJournalInput(Guid? GardenId = null, Guid? BedId = null, Guid? PlantId = null, int Limit = 50, Guid? ReservoirId = null);

public sealed record AddJournalEntryInput(
    [property: Description("note | sown | transplanted | watered | fertilized | pruned | pest | disease | harvest | observation | stage_change | reservoir_change | top_up | dose")] string Kind,
    string Notes,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    DateTimeOffset? OccurredAt = null,
    double? Quantity = null,
    string? Unit = null,
    Guid? MediaId = null);

public sealed record PlantDetail(
    PlantDto Plant,
    BedDto Bed,
    PlantProfile? Profile,
    IReadOnlyList<CareTarget> CareTargets,
    IReadOnlyList<SensorDto> Sensors,
    IReadOnlyList<ActuatorDto> Actuators,
    IReadOnlyList<CameraDto> Cameras,
    IReadOnlyList<JournalEntry> RecentJournal);

public static class GardenTools
{
    public const string Category = "Garden";

    public static async Task<Garden> ResolveGardenAsync(SlopDbContext db, Guid? gardenId, CancellationToken ct)
    {
        var q = db.Gardens.Where(g => g.ArchivedAt == null);
        var g = gardenId is { } id ? await db.Gardens.FirstOrDefaultAsync(x => x.Id == id, ct) : await q.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        return g ?? throw AppException.NotFound("Garden");
    }

    public static async Task<PlantProfile?> ResolveProfileAsync(SlopDbContext db, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (Guid.TryParse(key, out var id)) return await db.PlantProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        var slug = key.Trim().ToLowerInvariant();
        return await db.PlantProfiles.FirstOrDefaultAsync(p => p.Slug == slug || p.CommonName.ToLower() == slug, ct);
    }

    private static Task Changed(ToolContext ctx, params string[] areas) => ctx.Get<LiveNotifier>().Changed(areas);

    public static void Register(ToolRegistry r)
    {
        r.Read<EmptyInput, List<GardenDto>>("list_gardens", Category, "List all gardens with location, timezone, environment and AI context.",
            async (ctx, _) => (await ctx.Db.Gardens.AsNoTracking().Where(g => g.ArchivedAt == null).OrderBy(g => g.CreatedAt).ToListAsync(ctx.Ct)).Select(GardenDto.From).ToList());

        r.Read<GardenIdInput, GardenOverview>("get_garden_overview", Category,
            "Complete snapshot of a garden: beds, plants, latest sensor readings per target, actuator states, cameras, devices, emergency stops, pending proposals and running AI activity. Start here.",
            (ctx, i) => ctx.Get<OverviewService>().BuildAsync(i.GardenId, ctx.Ct));

        r.Mutate<CreateGardenInput, GardenDto>("create_garden", Category, "Create a new garden (a site: backyard, greenhouse, farm field…).",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Environment, GardenVocab.Environments, "environment");
                var g = new Garden
                {
                    Name = i.Name,
                    Description = i.Description ?? "",
                    Environment = i.Environment ?? "outdoor",
                    Latitude = i.Latitude,
                    Longitude = i.Longitude,
                    LocationLabel = i.LocationLabel,
                    LocationSource = i.Latitude is not null ? "manual" : "device",
                    TimezoneId = i.TimezoneId ?? TimeZoneInfo.Local.Id,
                    TimezoneSource = i.TimezoneId is not null ? "manual" : "device",
                    HardinessZone = i.HardinessZone,
                    ClimateNotes = i.ClimateNotes,
                    AiContext = i.AiContext ?? "",
                };
                ctx.Db.Gardens.Add(g);
                ctx.Db.Audit(ctx.Actor, "garden.create", "garden", g.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "gardens");
                return GardenDto.From(g);
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Create garden '{i.Name}'", "garden", null, null, "low", Patch.CreateChanges(i))));

        r.Mutate<UpdateGardenInput, GardenDto>("update_garden", Category, "Update garden settings, location/timezone overrides, AI context or AI autonomy mode. Null fields are unchanged.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Environment, GardenVocab.Environments, "environment");
                Ensure.OneOf(i.AiAutonomy, GardenVocab.Autonomy, "aiAutonomy");
                Ensure.OneOf(i.LocationSource, ["device", "manual"], "locationSource");
                Ensure.OneOf(i.TimezoneSource, ["device", "manual"], "timezoneSource");
                Ensure.OneOf(i.DefaultGrowingMedium, GrowingMedia.All, "defaultGrowingMedium");
                var g = Ensure.Found(await ctx.Db.Gardens.FirstOrDefaultAsync(x => x.Id == i.GardenId, ctx.Ct), "Garden");
                Patch.Apply(g, i, nameof(i.GardenId));
                if (i.Latitude is not null && i.LocationSource is null) g.LocationSource = "manual";
                if (i.TimezoneId is not null && i.TimezoneSource is null) g.TimezoneSource = "manual";
                ctx.Db.Audit(ctx.Actor, "garden.update", "garden", g.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty();
                await Changed(ctx, "gardens");
                return GardenDto.From(g);
            },
            async (ctx, i) =>
            {
                var g = Ensure.Found(await ctx.Db.Gardens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.GardenId, ctx.Ct), "Garden");
                return new ProposalPreview($"Update garden '{g.Name}'", "garden", g.Id, g.Id, "low", Patch.UpdateChanges(g, i, nameof(i.GardenId)));
            });

        // ---------------------------------------------------------------- beds
        r.Read<ListBedsInput, List<BedDto>>("list_beds", Category, "List beds (raised beds, in-ground plots, containers, benches…) in a garden.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Beds.AsNoTracking().AsQueryable();
                if (i.GardenId is { } gid) q = q.Where(b => b.GardenId == gid);
                if (!i.IncludeArchived) q = q.Where(b => b.ArchivedAt == null);
                var beds = await q.OrderBy(b => b.Name).Select(b => new { b, n = b.Plants.Count(p => p.RemovedAt == null) }).ToListAsync(ctx.Ct);
                return beds.Select(x => BedDto.From(x.b, x.n)).ToList();
            });

        r.Mutate<CreateBedInput, BedDto>("create_bed", Category, "Create a bed in a garden.",
            async (ctx, i) =>
            {
                ValidateBed(i.Kind, i.Environment, i.IrrigationType, i.SunExposure);
                await ValidateHydroAsync(ctx, i.GrowingMedium, i.HydroSystem, i.ReservoirId);
                _ = Ensure.Found(await ctx.Db.Gardens.FirstOrDefaultAsync(g => g.Id == i.GardenId, ctx.Ct), "Garden");
                var b = new Bed { GardenId = i.GardenId };
                Patch.Apply(b, i);
                ctx.Db.Beds.Add(b);
                ctx.Db.Audit(ctx.Actor, "bed.create", "bed", b.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "beds");
                return BedDto.From(b, 0);
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Create bed '{i.Name}'", "garden", i.GardenId, i.GardenId, "low", Patch.CreateChanges(i, nameof(i.GardenId)))));

        r.Mutate<UpdateBedInput, BedDto>("update_bed", Category, "Update a bed. Null fields are unchanged.",
            async (ctx, i) =>
            {
                ValidateBed(i.Kind, i.Environment, i.IrrigationType, i.SunExposure);
                await ValidateHydroAsync(ctx, i.GrowingMedium, i.HydroSystem, i.ReservoirId);
                var b = Ensure.Found(await ctx.Db.Beds.FirstOrDefaultAsync(x => x.Id == i.BedId, ctx.Ct), "Bed");
                Patch.Apply(b, i, nameof(i.BedId));
                ctx.Db.Audit(ctx.Actor, "bed.update", "bed", b.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "beds");
                return BedDto.From(b, await ctx.Db.Plants.CountAsync(p => p.BedId == b.Id && p.RemovedAt == null, ctx.Ct));
            },
            async (ctx, i) =>
            {
                var b = Ensure.Found(await ctx.Db.Beds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.BedId, ctx.Ct), "Bed");
                return new ProposalPreview($"Update bed '{b.Name}'", "bed", b.Id, b.GardenId, "low", Patch.UpdateChanges(b, i, nameof(i.BedId)));
            });

        r.Mutate<BedIdInput, BedDto>("archive_bed", Category, "Archive (soft-delete) a bed. Its history is kept.",
            async (ctx, i) =>
            {
                var b = Ensure.Found(await ctx.Db.Beds.FirstOrDefaultAsync(x => x.Id == i.BedId, ctx.Ct), "Bed");
                b.ArchivedAt = DateTimeOffset.UtcNow;
                ctx.Db.Audit(ctx.Actor, "bed.archive", "bed", b.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "beds");
                return BedDto.From(b, 0);
            },
            async (ctx, i) =>
            {
                var b = Ensure.Found(await ctx.Db.Beds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.BedId, ctx.Ct), "Bed");
                return new ProposalPreview($"Archive bed '{b.Name}'", "bed", b.Id, b.GardenId, "medium", [Patch.Change("archived", false, true)]);
            });

        // ---------------------------------------------------------------- plants
        r.Read<ListPlantsInput, List<PlantDto>>("list_plants", Category, "List plants (or planting groups) in a garden or bed.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Plants.AsNoTracking().Include(p => p.PlantProfile).Include(p => p.Bed).AsQueryable();
                if (i.BedId is { } bid) q = q.Where(p => p.BedId == bid);
                if (i.GardenId is { } gid) q = q.Where(p => p.Bed!.GardenId == gid);
                if (!i.IncludeRemoved) q = q.Where(p => p.RemovedAt == null);
                var list = await q.OrderBy(p => p.Bed!.Name).ThenBy(p => p.Name).ToListAsync(ctx.Ct);
                return list.Select(p => PlantDto.From(p, p.Bed!.GardenId)).ToList();
            });

        r.Read<PlantIdInput, PlantDetail>("get_plant", Category, "Everything about one plant: profile needs, care targets, sensors/actuators/cameras targeting it (or its bed), latest readings and recent journal.",
            async (ctx, i) =>
            {
                var db = ctx.Db;
                var p = Ensure.Found(await db.Plants.AsNoTracking().Include(x => x.PlantProfile).Include(x => x.Bed).FirstOrDefaultAsync(x => x.Id == i.PlantId, ctx.Ct), "Plant");
                var bed = p.Bed!;
                var plantCount = await db.Plants.CountAsync(x => x.BedId == bed.Id && x.RemovedAt == null, ctx.Ct);
                var targets = await db.CareTargets.AsNoTracking().Where(t => t.PlantId == p.Id || (t.BedId == bed.Id && t.ScopeType == "bed")).ToListAsync(ctx.Ct);
                var sensors = await db.Sensors.AsNoTracking().Include(s => s.Channels).Include(s => s.Device)
                    .Where(s => s.PlantId == p.Id || (s.BedId == bed.Id && s.TargetType == "bed")).ToListAsync(ctx.Ct);
                var actuators = await db.Actuators.AsNoTracking().Include(a => a.Device)
                    .Where(a => a.PlantId == p.Id || (a.BedId == bed.Id && a.TargetType == "bed")).ToListAsync(ctx.Ct);
                var cameras = await db.Cameras.AsNoTracking().Include(c => c.Device)
                    .Where(c => c.PlantId == p.Id || (c.BedId == bed.Id && c.TargetType == "bed")).ToListAsync(ctx.Ct);
                var journal = await db.JournalEntries.AsNoTracking().Where(j => j.PlantId == p.Id || (j.BedId == bed.Id && j.PlantId == null))
                    .OrderByDescending(j => j.OccurredAt).Take(20).ToListAsync(ctx.Ct);
                return new PlantDetail(PlantDto.From(p, bed.GardenId), BedDto.From(bed, plantCount), p.PlantProfile, targets,
                    sensors.Select(SensorDto.From).ToList(), actuators.Select(ActuatorDto.From).ToList(), cameras.Select(CameraDto.From).ToList(), journal);
            });

        r.Mutate<CreatePlantInput, PlantDto>("create_plant", Category, "Add a plant (or a group of identical plants) to a bed.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Stage, GardenVocab.Stages, "stage");
                Ensure.OneOf(i.StartedFrom, GardenVocab.StartedFrom, "startedFrom");
                var bed = Ensure.Found(await ctx.Db.Beds.FirstOrDefaultAsync(b => b.Id == i.BedId, ctx.Ct), "Bed");
                var profile = await ResolveProfileAsync(ctx.Db, i.Profile, ctx.Ct);
                if (i.Profile is not null && profile is null) throw AppException.Bad($"Unknown plant profile '{i.Profile}'. Use list_plant_profiles or create one.");
                var p = new Plant { BedId = bed.Id, PlantProfileId = profile?.Id };
                Patch.Apply(p, i, nameof(i.Profile));
                ctx.Db.Plants.Add(p);
                ctx.Db.JournalEntries.Add(new JournalEntry
                {
                    GardenId = bed.GardenId, BedId = bed.Id, PlantId = p.Id, Kind = i.StartedFrom == "seed" ? "sown" : "transplanted",
                    Notes = $"Added {p.Quantity} × {p.Name}", CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId,
                    OccurredAt = i.PlantedAt is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.UtcNow,
                });
                ctx.Db.Audit(ctx.Actor, "plant.create", "plant", p.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                p.PlantProfile = profile;
                await Changed(ctx, "plants");
                return PlantDto.From(p, bed.GardenId);
            },
            async (ctx, i) =>
            {
                var bed = Ensure.Found(await ctx.Db.Beds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == i.BedId, ctx.Ct), "Bed");
                return new ProposalPreview($"Add {i.Quantity ?? 1} × {i.Name} to '{bed.Name}'", "bed", bed.Id, bed.GardenId, "low", Patch.CreateChanges(i, nameof(i.BedId)));
            });

        r.Mutate<UpdatePlantInput, PlantDto>("update_plant", Category, "Update a plant: stage, variety, dates, harvest prediction, notes, AI context, or move it to another bed.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Stage, GardenVocab.Stages, "stage");
                Ensure.OneOf(i.StartedFrom, GardenVocab.StartedFrom, "startedFrom");
                var p = Ensure.Found(await ctx.Db.Plants.Include(x => x.Bed).Include(x => x.PlantProfile).FirstOrDefaultAsync(x => x.Id == i.PlantId, ctx.Ct), "Plant");
                var oldStage = p.Stage;
                if (i.Profile is not null)
                {
                    var profile = await ResolveProfileAsync(ctx.Db, i.Profile, ctx.Ct) ?? throw AppException.Bad($"Unknown plant profile '{i.Profile}'");
                    p.PlantProfileId = profile.Id;
                    p.PlantProfile = profile;
                }
                Patch.Apply(p, i, nameof(i.PlantId), nameof(i.Profile));
                if (i.Stage is not null && i.Stage != oldStage)
                {
                    ctx.Db.JournalEntries.Add(new JournalEntry
                    {
                        GardenId = p.Bed!.GardenId, BedId = p.BedId, PlantId = p.Id, Kind = "stage_change",
                        Notes = $"Stage {oldStage} → {i.Stage}", CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId,
                    });
                    if (i.Stage == "removed") p.RemovedAt = DateTimeOffset.UtcNow;
                }
                ctx.Db.Audit(ctx.Actor, "plant.update", "plant", p.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "plants");
                var gardenId = await ctx.Db.Beds.Where(b => b.Id == p.BedId).Select(b => b.GardenId).FirstAsync(ctx.Ct);
                return PlantDto.From(p, gardenId);
            },
            async (ctx, i) =>
            {
                var p = Ensure.Found(await ctx.Db.Plants.AsNoTracking().Include(x => x.Bed).FirstOrDefaultAsync(x => x.Id == i.PlantId, ctx.Ct), "Plant");
                return new ProposalPreview($"Update plant '{p.Name}'", "plant", p.Id, p.Bed!.GardenId, "low", Patch.UpdateChanges(p, i, nameof(i.PlantId)));
            });

        r.Mutate<PlantIdInput, PlantDto>("remove_plant", Category, "Mark a plant as removed (harvested out, died, pulled). History is kept.",
            async (ctx, i) =>
            {
                var p = Ensure.Found(await ctx.Db.Plants.Include(x => x.Bed).FirstOrDefaultAsync(x => x.Id == i.PlantId, ctx.Ct), "Plant");
                p.RemovedAt = DateTimeOffset.UtcNow;
                p.Stage = "removed";
                ctx.Db.Audit(ctx.Actor, "plant.remove", "plant", p.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "plants");
                return PlantDto.From(p, p.Bed!.GardenId);
            },
            async (ctx, i) =>
            {
                var p = Ensure.Found(await ctx.Db.Plants.AsNoTracking().Include(x => x.Bed).FirstOrDefaultAsync(x => x.Id == i.PlantId, ctx.Ct), "Plant");
                return new ProposalPreview($"Remove plant '{p.Name}'", "plant", p.Id, p.Bed!.GardenId, "medium", [Patch.Change("stage", p.Stage, "removed")]);
            });

        // ---------------------------------------------------------------- profiles
        r.Read<ListProfilesInput, List<PlantProfile>>("list_plant_profiles", Category, "List crop profiles with water/light/temperature/pH needs.",
            async (ctx, i) =>
            {
                var q = ctx.Db.PlantProfiles.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(i.Search))
                {
                    var s = $"%{i.Search.Trim()}%";
                    q = q.Where(p => EF.Functions.ILike(p.CommonName, s) || EF.Functions.ILike(p.ScientificName ?? "", s) || EF.Functions.ILike(p.Category, s) || EF.Functions.ILike(p.Slug, s));
                }
                return await q.OrderBy(p => p.CommonName).ToListAsync(ctx.Ct);
            });

        r.Read<ProfileInput, PlantProfile>("get_plant_profile", Category, "Get one crop profile by id or slug.",
            async (ctx, i) => Ensure.Found(await ResolveProfileAsync(ctx.Db, i.Profile, ctx.Ct), "Plant profile"));

        r.Mutate<UpsertPlantProfileInput, PlantProfile>("upsert_plant_profile", Category,
            "Create a crop profile or tweak an existing one (e.g. from research). Include sources for researched values.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Category, GardenVocab.Categories, "category");
                Ensure.OneOf(i.WaterNeed, GardenVocab.WaterNeeds, "waterNeed");
                Ensure.OneOf(i.FrostTolerance, ["none", "light", "hard"], "frostTolerance");
                var p = await ResolveProfileAsync(ctx.Db, i.Profile, ctx.Ct);
                if (i.Profile is not null && p is null) throw AppException.NotFound("Plant profile");
                if (p is null)
                {
                    if (string.IsNullOrWhiteSpace(i.CommonName)) throw AppException.Bad("commonName is required for a new profile");
                    p = new PlantProfile { CreatedByType = ctx.ChangeActorType, Slug = Slugify(i.Slug ?? i.CommonName) };
                    if (await ctx.Db.PlantProfiles.AnyAsync(x => x.Slug == p.Slug, ctx.Ct)) throw AppException.Conflict($"Profile '{p.Slug}' already exists");
                    ctx.Db.PlantProfiles.Add(p);
                }
                Patch.Apply(p, i, nameof(i.Profile), nameof(i.Slug), nameof(i.Sources));
                if (i.Sources is not null) p.Sources = new JsonArray(i.Sources.Select(s => (JsonNode)s).ToArray());
                ctx.Db.Audit(ctx.Actor, "plant_profile.upsert", "plant_profile", p.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "profiles");
                return p;
            },
            async (ctx, i) =>
            {
                var p = await ResolveProfileAsync(ctx.Db, i.Profile, ctx.Ct);
                return p is null
                    ? new ProposalPreview($"Create crop profile '{i.CommonName}'", "plant_profile", null, null, "low", Patch.CreateChanges(i, nameof(i.Profile)))
                    : new ProposalPreview($"Update crop profile '{p.CommonName}'", "plant_profile", p.Id, null, "low", Patch.UpdateChanges(p, i, nameof(i.Profile)));
            });

        // ---------------------------------------------------------------- care targets
        r.Read<ListCareTargetsInput, List<CareTarget>>("list_care_targets", Category, "List per-plant / per-bed care targets (overrides of the crop profile ranges).",
            async (ctx, i) =>
            {
                var q = ctx.Db.CareTargets.AsNoTracking().AsQueryable();
                if (i.PlantId is { } pid) q = q.Where(t => t.PlantId == pid);
                if (i.BedId is { } bid) q = q.Where(t => t.BedId == bid);
                if (i.ReservoirId is { } rid) q = q.Where(t => t.ReservoirId == rid);
                return await q.OrderBy(t => t.Metric).ToListAsync(ctx.Ct);
            });

        r.Mutate<SetCareTargetInput, CareTarget>("set_care_target", Category,
            "Set the desired range for one metric on a plant or bed (e.g. soil_moisture_pct 45–65 for the tomatoes). Overrides the crop profile.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.ScopeType, ["plant", "bed", "reservoir"], "scopeType");
                var (bedId, plantId) = await ResolveCareScopeAsync(ctx, i);
                var reservoirId = i.ScopeType == "reservoir" ? i.ReservoirId : null;
                var t = await ctx.Db.CareTargets.FirstOrDefaultAsync(x => x.ScopeType == i.ScopeType && x.BedId == bedId && x.PlantId == plantId && x.ReservoirId == reservoirId && x.Metric == i.Metric, ctx.Ct);
                if (t is null)
                {
                    t = new CareTarget { ScopeType = i.ScopeType, BedId = bedId, PlantId = plantId, ReservoirId = reservoirId, Metric = i.Metric };
                    ctx.Db.CareTargets.Add(t);
                }
                t.MinValue = i.MinValue ?? t.MinValue;
                t.MaxValue = i.MaxValue ?? t.MaxValue;
                t.IdealValue = i.IdealValue ?? t.IdealValue;
                t.Rationale = i.Rationale ?? ctx.Reason ?? t.Rationale;
                t.Source = ctx.ChangeActorType == ActorTypes.Ai ? "ai" : "user";
                t.UpdatedByType = ctx.ChangeActorType;
                t.UpdatedById = ctx.Actor.UserId;
                t.UpdatedAt = DateTimeOffset.UtcNow;
                ctx.Db.Audit(ctx.Actor, "care_target.set", i.ScopeType, plantId ?? bedId ?? reservoirId);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "plants");
                return t;
            },
            async (ctx, i) =>
            {
                var (bedId, plantId) = await ResolveCareScopeAsync(ctx, i);
                var reservoirId = i.ScopeType == "reservoir" ? i.ReservoirId : null;
                var existing = await ctx.Db.CareTargets.AsNoTracking().FirstOrDefaultAsync(x => x.ScopeType == i.ScopeType && x.BedId == bedId && x.PlantId == plantId && x.ReservoirId == reservoirId && x.Metric == i.Metric, ctx.Ct);
                var name = plantId is { } p ? (await ctx.Db.Plants.AsNoTracking().Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct))
                    : reservoirId is { } r ? (await ctx.Db.Reservoirs.AsNoTracking().Where(x => x.Id == r).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct))
                    : (await ctx.Db.Beds.AsNoTracking().Where(x => x.Id == bedId).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct));
                var gardenId = reservoirId is { } rr ? await ctx.Db.Reservoirs.Where(x => x.Id == rr).Select(x => (Guid?)x.GardenId).FirstOrDefaultAsync(ctx.Ct)
                    : await ctx.Db.Beds.Where(b => b.Id == bedId).Select(b => (Guid?)b.GardenId).FirstOrDefaultAsync(ctx.Ct);
                var changes = new JsonArray(
                    Patch.Change($"{i.Metric}.min", existing?.MinValue, i.MinValue ?? existing?.MinValue),
                    Patch.Change($"{i.Metric}.max", existing?.MaxValue, i.MaxValue ?? existing?.MaxValue));
                if (i.IdealValue is not null) changes.Add(Patch.Change($"{i.Metric}.ideal", existing?.IdealValue, i.IdealValue));
                return new ProposalPreview($"Set {i.Metric} target for '{name}'", i.ScopeType, plantId ?? bedId ?? reservoirId, gardenId, "low", changes);
            });

        r.Mutate<CareTargetIdInput, bool>("delete_care_target", Category, "Remove a care target override (falls back to the crop profile).",
            async (ctx, i) =>
            {
                var t = Ensure.Found(await ctx.Db.CareTargets.FirstOrDefaultAsync(x => x.Id == i.CareTargetId, ctx.Ct), "Care target");
                ctx.Db.CareTargets.Remove(t);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "plants");
                return true;
            },
            async (ctx, i) =>
            {
                var t = Ensure.Found(await ctx.Db.CareTargets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.CareTargetId, ctx.Ct), "Care target");
                return new ProposalPreview($"Remove {t.Metric} target", t.ScopeType, t.PlantId ?? t.BedId, null, "low", [Patch.Change(t.Metric, $"{t.MinValue}–{t.MaxValue}", null)]);
            });

        // ---------------------------------------------------------------- journal
        r.Read<ListJournalInput, List<JournalEntry>>("list_journal", Category, "Growth journal: sowing, watering, fertilizing, observations, harvests.",
            async (ctx, i) =>
            {
                var q = ctx.Db.JournalEntries.AsNoTracking().AsQueryable();
                if (i.GardenId is { } g) q = q.Where(j => j.GardenId == g);
                if (i.BedId is { } b) q = q.Where(j => j.BedId == b);
                if (i.PlantId is { } p) q = q.Where(j => j.PlantId == p);
                if (i.ReservoirId is { } r) q = q.Where(j => j.ReservoirId == r);
                return await q.OrderByDescending(j => j.OccurredAt).Take(Math.Clamp(i.Limit, 1, 500)).ToListAsync(ctx.Ct);
            });

        r.Mutate<AddJournalEntryInput, JournalEntry>("add_journal_entry", Category, "Record an observation, harvest (with quantity/unit), pest sighting, etc.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Kind, GardenVocab.JournalKinds, "kind");
                var (gardenId, bedId) = await ResolveJournalScopeAsync(ctx, i);
                var j = new JournalEntry
                {
                    GardenId = gardenId, BedId = bedId, PlantId = i.PlantId, Kind = i.Kind, Notes = i.Notes, OccurredAt = i.OccurredAt ?? DateTimeOffset.UtcNow,
                    Quantity = i.Quantity, Unit = i.Unit, MediaId = i.MediaId, CreatedByType = ctx.ChangeActorType, CreatedById = ctx.Actor.UserId,
                };
                ctx.Db.JournalEntries.Add(j);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                await Changed(ctx, "journal");
                return j;
            },
            async (ctx, i) =>
            {
                var (gardenId, _) = await ResolveJournalScopeAsync(ctx, i);
                return new ProposalPreview($"Add journal entry ({i.Kind})", i.PlantId is not null ? "plant" : "garden", i.PlantId ?? gardenId, gardenId, "low", Patch.CreateChanges(i));
            });
    }

    private static void ValidateBed(string? kind, string? env, string? irrigation, string? sun)
    {
        Ensure.OneOf(kind, GardenVocab.BedKinds, "kind");
        Ensure.OneOf(env, GardenVocab.Environments, "environment");
        Ensure.OneOf(irrigation, GardenVocab.Irrigation, "irrigationType");
        Ensure.OneOf(sun, GardenVocab.Sun, "sunExposure");
    }

    private static async Task ValidateHydroAsync(ToolContext ctx, string? medium, string? system, Guid? reservoirId)
    {
        Ensure.OneOf(medium, GrowingMedia.All, "growingMedium");
        Ensure.OneOf(system, HydroSystems.Ids, "hydroSystem");
        if (reservoirId is { } rid && !await ctx.Db.Reservoirs.AnyAsync(r => r.Id == rid, ctx.Ct)) throw AppException.NotFound("Reservoir");
    }

    private static async Task<(Guid? BedId, Guid? PlantId)> ResolveCareScopeAsync(ToolContext ctx, SetCareTargetInput i)
    {
        if (i.ScopeType == "reservoir")
        {
            var rid = i.ReservoirId ?? throw AppException.Bad("reservoirId is required for scopeType=reservoir");
            if (!await ctx.Db.Reservoirs.AnyAsync(r => r.Id == rid, ctx.Ct)) throw AppException.NotFound("Reservoir");
            return (null, null);
        }
        if (i.ScopeType == "plant")
        {
            var pid = i.PlantId ?? throw AppException.Bad("plantId is required for scopeType=plant");
            var bed = await ctx.Db.Plants.Where(p => p.Id == pid).Select(p => (Guid?)p.BedId).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Plant");
            return (bed, pid);
        }
        var bid = i.BedId ?? throw AppException.Bad("bedId is required for scopeType=bed");
        if (!await ctx.Db.Beds.AnyAsync(b => b.Id == bid, ctx.Ct)) throw AppException.NotFound("Bed");
        return (bid, null);
    }

    private static async Task<(Guid GardenId, Guid? BedId)> ResolveJournalScopeAsync(ToolContext ctx, AddJournalEntryInput i)
    {
        if (i.PlantId is { } pid)
        {
            var x = await ctx.Db.Plants.Where(p => p.Id == pid).Select(p => new { p.BedId, p.Bed!.GardenId }).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Plant");
            return (x.GardenId, x.BedId);
        }
        if (i.BedId is { } bid)
        {
            var g = await ctx.Db.Beds.Where(b => b.Id == bid).Select(b => (Guid?)b.GardenId).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Bed");
            return (g, bid);
        }
        return ((await ResolveGardenAsync(ctx.Db, i.GardenId, ctx.Ct)).Id, null);
    }

    public static string Slugify(string s) =>
        new string(s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').Replace("--", "-");
}
