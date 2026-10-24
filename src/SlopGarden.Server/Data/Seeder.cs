using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Data;

/// <summary>Applies migrations and seeds reference data (crop profiles, hardware catalog, default safety limits) and, optionally, a demo garden.</summary>
public sealed class Seeder(SlopDbContext db, IOptions<ServerOptions> options, IConfiguration config, ILogger<Seeder> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        await SeedAdminAsync(ct);
        await SeedProfilesAsync(ct);
        await SeedHardwareCatalogAsync(ct);
        await SeedSafetyAsync(ct);
        await EnsureHydroDefaultsAsync(ct);
        if (!await db.NotificationChannels.AnyAsync(ct))
            db.NotificationChannels.Add(new NotificationChannel { Kind = "browser", Name = "Browser notifications", MinSeverity = Severities.Warning });
        await db.SaveChangesAsync(ct);
        await EnsureUsbCameraCatalogAsync(ct);
        if (options.Value.SeedDemo)
        {
            await SeedDemoAsync(ct);
            await SeedHydroDemoAsync(ct);
            await SeedSecondGardenAsync(ct);
            await EnsureDemoClientReleaseAsync(ct);
        }
    }

    private async Task EnsureUsbCameraCatalogAsync(CancellationToken ct)
    {
        if (await db.HardwareCatalog.AnyAsync(h => h.PluginId == "v4l2-camera", ct)) return;
        db.HardwareCatalog.Add(new HardwareCatalogEntry
        {
            Kind = "camera", Manufacturer = "Generic", Model = "USB webcam (V4L2)", Protocol = "v4l2", Metrics = [], PluginId = "v4l2-camera",
            Recommended = true, TestedStatus = "community", Notes = "USB UVC webcam on /dev/videoN. The client captures frames with ffmpeg.",
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A second garden so the switcher has somewhere to go. Only the explicit demo seed creates it.</summary>
    private async Task SeedSecondGardenAsync(CancellationToken ct)
    {
        if (await db.Gardens.AnyAsync(g => g.Name == "Window Herb Garden", ct)) return;
        var garden = new Garden
        {
            Name = "Window Herb Garden",
            Description = "A second garden: a kratky jar on the kitchen windowsill.",
            Environment = "indoor",
            TimezoneId = "America/New_York",
            TimezoneSource = "manual",
            Latitude = 40.71,
            Longitude = -74.0,
            LocationLabel = "New York, NY (demo)",
            LocationSource = "manual",
            DefaultGrowingMedium = GrowingMedia.Hydroponic,
            AiContext = "Indoor windowsill. The basil is in a kratky jar; do not look for soil moisture.",
        };
        var jar = new Bed
        {
            GardenId = garden.Id, Name = "Basil kratky jar", Kind = "container", Environment = "indoor",
            GrowingMedium = GrowingMedia.Hydroponic, HydroSystem = "kratky", IrrigationType = "hydroponic",
            SunExposure = "part_sun", LayoutX = 0, LayoutY = 0,
            AiContext = "Passive kratky. Top up the jar when the level drops; do not run a circulation pump.",
        };
        db.Gardens.Add(garden);
        db.Beds.Add(jar);
        var basil = await db.PlantProfiles.FirstOrDefaultAsync(p => p.Slug == "basil", ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Plants.Add(new Plant
        {
            BedId = jar.Id, PlantProfileId = basil?.Id, Name = "Windowsill basil", Variety = "Genovese", Quantity = 1,
            Stage = "vegetative", StartedFrom = "seed", PlantedAt = today.AddDays(-18), ExpectedHarvestStart = today.AddDays(20),
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded second demo garden");
    }

    private async Task EnsureDemoClientReleaseAsync(CancellationToken ct)
    {
        var placeholder = (await db.ServerSettings.Where(s => s.Key == "client_release").ToListAsync(ct))
            .FirstOrDefault(s => s.Value?["version"]?.ToString() == "0.3.0");
        if (placeholder is not null) db.ServerSettings.Remove(placeholder);
        if (!await db.ServerSettings.AnyAsync(s => s.Key == "release_track", ct))
            db.ServerSettings.Add(new ServerSetting { Key = "release_track", Value = new System.Text.Json.Nodes.JsonObject { ["mode"] = "latest", ["version"] = "", ["serverAutoUpdate"] = false } });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hydroponic EC ranges (mS/cm) for the built-in crops.</summary>
    private static readonly Dictionary<string, (double Min, double Max)> HydroEc = new()
    {
        ["tomato"] = (2.0, 5.0), ["cherry-tomato"] = (2.0, 5.0), ["lettuce"] = (0.8, 1.2), ["basil"] = (1.0, 1.6), ["pepper"] = (1.8, 2.8),
        ["cucumber"] = (1.7, 2.5), ["zucchini"] = (1.8, 2.4), ["strawberry"] = (1.0, 1.4), ["carrot"] = (1.6, 2.0), ["spinach"] = (1.8, 2.3),
        ["kale"] = (1.6, 2.5), ["bush-bean"] = (2.0, 4.0), ["parsley"] = (0.8, 1.8), ["mint"] = (2.0, 2.4), ["chili"] = (1.8, 2.8), ["microgreens"] = (0.8, 1.2),
    };

    private async Task EnsureHydroDefaultsAsync(CancellationToken ct)
    {
        var builtin = await db.PlantProfiles.Where(p => p.IsBuiltin && p.EcMin == null).ToListAsync(ct);
        foreach (var p in builtin.Where(p => HydroEc.ContainsKey(p.Slug)))
            (p.EcMin, p.EcMax) = HydroEc[p.Slug];

        if (!await db.SafetyLimits.AnyAsync(l => l.LimitType == SafetyLimitTypes.MinSecondsBetweenDoses, ct))
            db.SafetyLimits.Add(new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MinSecondsBetweenDoses, Value = 600, CreatedByType = ActorTypes.System, Notes = "Wait 10 min between doses to the same reservoir so it can mix" });
        if (!await db.SafetyLimits.AnyAsync(l => l.LimitType == SafetyLimitTypes.MaxHourlyAmountMl, ct))
            db.SafetyLimits.Add(new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MaxHourlyAmountMl, Value = 30, CreatedByType = ActorTypes.System, Notes = "Max 30 ml per doser per hour" });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedHydroDemoAsync(CancellationToken ct)
    {
        var garden = await db.Gardens.OrderBy(g => g.CreatedAt).FirstOrDefaultAsync(g => g.ArchivedAt == null, ct);
        if (garden is null || await db.Reservoirs.AnyAsync(ct)) return;
        var reservoir = new Reservoir
        {
            GardenId = garden.Id, Name = "NFT rack reservoir", VolumeLiters = 60, ChangeIntervalDays = 14, LastChangedAt = DateTimeOffset.UtcNow.AddDays(-6),
            AiContext = "Two-part nutrient (A + B, equal ml). pH Down is phosphoric acid; pH Up is potassium hydroxide.",
        };
        var rack = new Bed
        {
            GardenId = garden.Id, Name = "NFT lettuce rack", Kind = "hydroponic", Environment = "greenhouse", GrowingMedium = GrowingMedia.Hydroponic, HydroSystem = "nft",
            ReservoirId = reservoir.Id, IrrigationType = "hydroponic", SunExposure = "grow_light", LayoutX = 3, LayoutY = 0,
            AiContext = "Four 2 m NFT channels under LED grow lights in the greenhouse.",
        };
        db.Reservoirs.Add(reservoir);
        db.Beds.Add(rack);
        var profiles = await db.PlantProfiles.Where(p => p.Slug == "lettuce" || p.Slug == "basil").ToDictionaryAsync(p => p.Slug, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Plants.AddRange(
            new Plant { BedId = rack.Id, PlantProfileId = profiles.GetValueOrDefault("lettuce")?.Id, Name = "Lettuce (NFT)", Variety = "Lollo Rossa", Quantity = 18, Stage = "vegetative", StartedFrom = "transplant", PlantedAt = today.AddDays(-21), ExpectedHarvestStart = today.AddDays(10) },
            new Plant { BedId = rack.Id, PlantProfileId = profiles.GetValueOrDefault("basil")?.Id, Name = "Basil (NFT)", Variety = "Genovese", Quantity = 6, Stage = "vegetative", StartedFrom = "transplant", PlantedAt = today.AddDays(-25) });
        db.CareTargets.AddRange(
            new CareTarget { ScopeType = "reservoir", ReservoirId = reservoir.Id, Metric = Metrics.EcMsCm, MinValue = 1.0, MaxValue = 1.6, Source = "user", Rationale = "Lettuce + basil mix" },
            new CareTarget { ScopeType = "reservoir", ReservoirId = reservoir.Id, Metric = Metrics.Ph, MinValue = 5.8, MaxValue = 6.2, Source = "user" },
            new CareTarget { ScopeType = "reservoir", ReservoirId = reservoir.Id, Metric = Metrics.WaterTemperatureC, MinValue = 18, MaxValue = 22, Source = "user" });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded demo NFT rack and reservoir");
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.InitialAdminPassword) || await db.Users.AnyAsync(ct)) return;
        var u = new User { Username = o.InitialAdminUsername.ToLowerInvariant(), DisplayName = "Administrator", Role = Roles.Admin };
        u.PasswordHash = PasswordUtil.Hash(u, o.InitialAdminPassword);
        db.Users.Add(u);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Created initial admin '{User}'", u.Username);
    }

    private static PlantProfile P(string slug, string name, string sci, string family, string cat, string water, double mMin, double mMax, double lMin, double lMax,
        double tMin, double tMax, double soilMin, double phMin, double phMax, int germ, int matMin, int matMax, double spacing, string frost, string fert, string desc) => new()
    {
        Slug = slug, CommonName = name, ScientificName = sci, Family = family, Category = cat, WaterNeed = water, SoilMoistureMinPct = mMin, SoilMoistureMaxPct = mMax,
        LightHoursMin = lMin, LightHoursMax = lMax, AirTempMinC = tMin, AirTempMaxC = tMax, SoilTempMinC = soilMin, PhMin = phMin, PhMax = phMax,
        DaysToGermination = germ, DaysToMaturityMin = matMin, DaysToMaturityMax = matMax, SpacingCm = spacing, FrostTolerance = frost, FertilizerNotes = fert,
        Description = desc, HumidityMinPct = 40, HumidityMaxPct = 75, IsBuiltin = true,
    };

    private async Task SeedProfilesAsync(CancellationToken ct)
    {
        if (await db.PlantProfiles.AnyAsync(ct)) return;
        db.PlantProfiles.AddRange(
            P("tomato", "Tomato", "Solanum lycopersicum", "Solanaceae", "fruit", "high", 45, 70, 8, 16, 16, 29, 15, 6.0, 6.8, 7, 60, 85, 60, "none", "Balanced feed until flowering, then higher potassium every 1–2 weeks.", "Warm-season fruiting crop. Consistent moisture prevents blossom-end rot and splitting."),
            P("cherry-tomato", "Cherry tomato", "Solanum lycopersicum var. cerasiforme", "Solanaceae", "fruit", "high", 45, 70, 8, 16, 16, 29, 15, 6.0, 6.8, 7, 55, 70, 50, "none", "As tomato; lighter feeding is fine.", "Vigorous, early, forgiving tomato."),
            P("lettuce", "Lettuce", "Lactuca sativa", "Asteraceae", "vegetable", "medium", 50, 75, 10, 14, 7, 24, 5, 6.0, 7.0, 5, 45, 65, 25, "light", "Nitrogen-rich feed; avoid excess.", "Cool-season leafy green; bolts in heat and long days."),
            P("basil", "Basil", "Ocimum basilicum", "Lamiaceae", "herb", "medium", 40, 60, 6, 16, 18, 30, 18, 6.0, 7.5, 7, 60, 90, 25, "none", "Light balanced feed every 3–4 weeks.", "Warm-loving herb; pinch flowers to keep leaves coming."),
            P("pepper", "Sweet pepper", "Capsicum annuum", "Solanaceae", "fruit", "medium", 40, 65, 8, 16, 18, 30, 18, 6.0, 6.8, 10, 60, 90, 45, "none", "Balanced, then potassium at fruit set.", "Warm-season; slow to start, needs steady warmth."),
            P("cucumber", "Cucumber", "Cucumis sativus", "Cucurbitaceae", "fruit", "high", 55, 80, 8, 14, 18, 30, 16, 6.0, 7.0, 5, 50, 70, 45, "none", "Regular balanced feed; high water demand.", "Fast, thirsty vine; irregular water causes bitter fruit."),
            P("zucchini", "Zucchini", "Cucurbita pepo", "Cucurbitaceae", "vegetable", "high", 50, 75, 8, 14, 18, 30, 16, 6.0, 7.5, 6, 45, 60, 90, "none", "Rich soil; feed every 2–3 weeks once fruiting.", "Productive summer squash."),
            P("strawberry", "Strawberry", "Fragaria × ananassa", "Rosaceae", "fruit", "medium", 45, 70, 8, 12, 10, 26, 10, 5.5, 6.8, 14, 90, 120, 30, "hard", "Potassium-rich feed while flowering/fruiting.", "Perennial; keep fruit off wet soil."),
            P("carrot", "Carrot", "Daucus carota subsp. sativus", "Apiaceae", "vegetable", "medium", 40, 65, 6, 12, 7, 24, 7, 6.0, 6.8, 14, 60, 80, 5, "light", "Low nitrogen; avoid fresh manure.", "Root crop; even moisture for straight roots."),
            P("spinach", "Spinach", "Spinacia oleracea", "Amaranthaceae", "vegetable", "medium", 50, 75, 8, 12, 5, 22, 5, 6.5, 7.5, 7, 40, 50, 10, "hard", "Nitrogen-rich feed.", "Cool-season green; bolts in long days."),
            P("kale", "Kale", "Brassica oleracea var. sabellica", "Brassicaceae", "vegetable", "medium", 45, 70, 6, 12, 5, 26, 5, 6.0, 7.5, 6, 55, 75, 45, "hard", "Nitrogen and calcium.", "Hardy brassica; sweeter after frost."),
            P("bush-bean", "Bush bean", "Phaseolus vulgaris", "Fabaceae", "vegetable", "medium", 40, 65, 8, 14, 16, 29, 16, 6.0, 7.0, 7, 50, 60, 10, "none", "Fixes nitrogen; little feeding needed.", "Warm-season legume."),
            P("parsley", "Parsley", "Petroselinum crispum", "Apiaceae", "herb", "medium", 45, 70, 6, 12, 10, 25, 10, 6.0, 7.0, 21, 70, 90, 20, "light", "Light feed monthly.", "Biennial herb; slow to germinate."),
            P("mint", "Mint", "Mentha spp.", "Lamiaceae", "herb", "high", 50, 80, 4, 12, 10, 27, 10, 6.0, 7.0, 14, 60, 90, 40, "hard", "Light feed; vigorous spreader — keep in containers.", "Perennial herb."),
            P("chili", "Chili pepper", "Capsicum annuum / frutescens", "Solanaceae", "fruit", "medium", 35, 60, 8, 16, 18, 32, 20, 6.0, 6.8, 12, 70, 100, 40, "none", "Balanced, then potassium; slightly dry soil intensifies heat.", "Hot-season pepper."),
            P("microgreens", "Microgreens", "various", "various", "vegetable", "medium", 55, 80, 12, 16, 16, 24, 16, 6.0, 7.0, 2, 7, 14, 1, "none", "None needed.", "Indoor tray crop under grow lights."));
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedHardwareCatalogAsync(CancellationToken ct)
    {
        if (await db.HardwareCatalog.AnyAsync(ct)) return;
        HardwareCatalogEntry H(string kind, string mfr, string model, string proto, string[] metrics, string plugin, bool rec, string tested, string notes) =>
            new() { Kind = kind, Manufacturer = mfr, Model = model, Protocol = proto, Metrics = [.. metrics], PluginId = plugin, Recommended = rec, TestedStatus = tested, Notes = notes };
        db.HardwareCatalog.AddRange(
            H("bridge", "Texas Instruments", "ADS1115 16-bit ADC breakout", "i2c", [Metrics.Voltage], "ads1115", true, "community", "Default analog input next to the Pi (4 channels, address 0x48–0x4B). Pair with capacitive soil probes."),
            H("sensor", "DFRobot / generic", "Capacitive soil moisture sensor v2.0", "analog", [Metrics.SoilMoisturePct], "ads1115", true, "community", "Corrosion-resistant. Calibrate dry (air) and wet (water) with two_point calibration."),
            H("sensor", "Maxim", "DS18B20 waterproof probe", "one_wire", [Metrics.SoilTemperatureC, Metrics.WaterTemperatureC], "ds18b20", true, "community", "Soil/water temperature. Enable 1-Wire on GPIO4 with a 4.7 kΩ pull-up."),
            H("sensor", "Bosch", "BME280", "i2c", [Metrics.AirTemperatureC, Metrics.AirHumidityPct, Metrics.AirPressureHpa], "bme280", true, "community", "Air temperature, humidity and pressure. Address 0x76/0x77."),
            H("sensor", "Generic", "Float switch / rain sensor (digital)", "gpio", [Metrics.Digital, Metrics.Rain], "gpio-digital-input", false, "community", "Use pull-up and active-low wiring."),
            H("bridge", "Arduino", "Arduino Nano / Uno as ADC bridge", "usb_serial", [Metrics.Voltage], "arduino-serial-adc", true, "untested", "Remote or many analog channels over USB serial (line protocol A0=512,…)."),
            H("bridge", "Espressif", "ESP32 as ADC bridge", "usb_serial", [Metrics.Voltage], "arduino-serial-adc", false, "untested", "Same line protocol; Wi-Fi satellite mode planned."),
            H("actuator", "Generic", "Opto-isolated 5 V relay board (1–8 ch)", "gpio", [], "gpio-relay", true, "community", "Drives 12 V solenoid valves/pumps. Most boards are active-low."),
            H("actuator", "Generic", "12 V normally-closed solenoid valve", "gpio", [], "gpio-relay", true, "community", "Fails closed on power loss — preferred for irrigation."),
            H("actuator", "Generic", "Peristaltic dosing pump 12 V", "gpio", [], "gpio-relay", false, "untested", "Fertilizer dosing; measure flow rate (ml/min) and set it on the actuator."),
            H("camera", "Raspberry Pi", "Camera Module 3", "csi", [], "libcamera", true, "untested", "Planned plugin (libcamera). Use per-plant or per-bed."),
            H("sensor", "SlopGarden", "Sensor emulator", "http", [Metrics.SoilMoisturePct, Metrics.AirTemperatureC, Metrics.WaterLevelPct, Metrics.LightLux], "emulator-sensor", false, "tested", "Virtual sensors for testing without hardware."));
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSafetyAsync(CancellationToken ct)
    {
        if (await db.SafetyLimits.AnyAsync(ct)) return;
        db.SafetyLimits.AddRange(
            new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MaxRunSeconds, Value = 900, CreatedByType = ActorTypes.System, Notes = "Max 15 min per watering run" },
            new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MaxDailyRunSeconds, Value = 3600, CreatedByType = ActorTypes.System, Notes = "Max 60 min watering per actuator per day" },
            new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MinSecondsBetweenRuns, Value = 30, CreatedByType = ActorTypes.System, Notes = "At least 30 s between runs of the same actuator" },
            new SafetyLimit { ScopeType = ScopeTypes.Global, LimitType = SafetyLimitTypes.MaxDailyAmountMl, ActuatorKind = ActuatorKinds.FertilizerDoser, Value = 50, CreatedByType = ActorTypes.System, Notes = "Max 50 ml fertilizer per doser per day" });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedDemoAsync(CancellationToken ct)
    {
        foreach (var (name, display, role) in new[] { ("grower", "Grace Grower", Roles.ReadWrite), ("viewer", "Victor Viewer", Roles.ReadOnly) })
        {
            if (await db.Users.AnyAsync(u => u.Username == name, ct)) continue;
            var u = new User { Username = name, DisplayName = display, Role = role };
            u.PasswordHash = PasswordUtil.Hash(u, config["SlopGarden:DemoPassword"] ?? "slopgarden");
            db.Users.Add(u);
        }

        if (!await db.Gardens.AnyAsync(ct))
        {
            var garden = new Garden
            {
                Name = "Backyard Demo Garden",
                Description = "Virtual garden backed by the SlopGarden sensor emulator.",
                Environment = "mixed",
                TimezoneId = TimeZoneInfo.Local.Id == "Etc/UTC" ? "Europe/Amsterdam" : TimeZoneInfo.Local.Id,
                TimezoneSource = "manual",
                Latitude = 52.37,
                Longitude = 4.9,
                LocationLabel = "Amsterdam, NL (demo)",
                LocationSource = "manual",
                HardinessZone = "8b",
                AiContext = "Demo garden. The tomato bed is in full sun and dries out fast in the afternoon. Basil lives in a pot in the greenhouse next to the heater.",
            };
            var tomatoBed = new Bed { GardenId = garden.Id, Name = "Tomato bed", Kind = "raised_bed", WidthCm = 120, LengthCm = 240, DepthCm = 40, SoilType = "loam + compost", IrrigationType = "drip", SunExposure = "full_sun", LayoutX = 0, LayoutY = 0, AiContext = "Dries out fast in afternoon sun." };
            var lettuceBed = new Bed { GardenId = garden.Id, Name = "Lettuce bed", Kind = "raised_bed", WidthCm = 120, LengthCm = 180, DepthCm = 30, SoilType = "loam", IrrigationType = "drip", SunExposure = "part_shade", LayoutX = 1, LayoutY = 0 };
            var greenhouse = new Bed { GardenId = garden.Id, Name = "Greenhouse herbs", Kind = "container", Environment = "greenhouse", IrrigationType = "drip", SunExposure = "part_sun", LayoutX = 2, LayoutY = 0 };
            db.Gardens.Add(garden);
            db.Beds.AddRange(tomatoBed, lettuceBed, greenhouse);
            var profiles = await db.PlantProfiles.ToDictionaryAsync(p => p.Slug, ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            db.Plants.AddRange(
                new Plant { BedId = tomatoBed.Id, PlantProfileId = profiles["tomato"].Id, Name = "San Marzano tomatoes", Variety = "San Marzano", Quantity = 4, Stage = "fruiting", StartedFrom = "transplant", PlantedAt = today.AddDays(-72), ExpectedHarvestStart = today.AddDays(6), ExpectedHarvestEnd = today.AddDays(40) },
                new Plant { BedId = tomatoBed.Id, PlantProfileId = profiles["cherry-tomato"].Id, Name = "Sungold cherry tomato", Variety = "Sungold", Quantity = 2, Stage = "harvesting", StartedFrom = "transplant", PlantedAt = today.AddDays(-80) },
                new Plant { BedId = lettuceBed.Id, PlantProfileId = profiles["lettuce"].Id, Name = "Butterhead lettuce", Variety = "Buttercrunch", Quantity = 12, Stage = "vegetative", StartedFrom = "seed", PlantedAt = today.AddDays(-30), ExpectedHarvestStart = today.AddDays(12) },
                new Plant { BedId = lettuceBed.Id, PlantProfileId = profiles["spinach"].Id, Name = "Spinach", Quantity = 20, Stage = "seedling", StartedFrom = "seed", PlantedAt = today.AddDays(-14) },
                new Plant { BedId = greenhouse.Id, PlantProfileId = profiles["basil"].Id, Name = "Genovese basil", Variety = "Genovese", Quantity = 3, Stage = "vegetative", StartedFrom = "seed", PlantedAt = today.AddDays(-40), AiContext = "Pot dries quickly; prefers watering in the morning." },
                new Plant { BedId = greenhouse.Id, PlantProfileId = profiles["chili"].Id, Name = "Jalapeño", Variety = "Early Jalapeño", Quantity = 1, Stage = "flowering", StartedFrom = "transplant", PlantedAt = today.AddDays(-55) });
            await db.SaveChangesAsync(ct);
            log.LogInformation("Seeded demo garden");
        }

        var devLlm = config["SlopGarden:DemoAiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(devLlm) && !await db.AiProviders.AnyAsync(ct))
        {
            var p = new AiProvider { Name = "Demo model (scripted, offline)", BaseUrl = devLlm, DefaultModel = "slopgarden-demo" };
            db.AiProviders.Add(p);
            foreach (var job in AiJobs.All) db.AiModelAssignments.Add(new AiModelAssignment { Job = job, ProviderId = p.Id, Model = p.DefaultModel, ContextMessageLimit = 30 });
            await db.SaveChangesAsync(ct);
        }
    }
}
