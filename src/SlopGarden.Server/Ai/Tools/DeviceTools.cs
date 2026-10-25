using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Data;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Ai.Tools;

public sealed record DeviceIdInput(Guid DeviceId);
public sealed record ListDevicesInput([property: Description("pending | approved | rejected | revoked; omit for all")] string? Status = null);
public sealed record ApproveDeviceInput(Guid DeviceId, [property: Description("Garden new components default to")] Guid? GardenId = null, string? Name = null);
public sealed record UpdateDeviceInput(Guid DeviceId, string? Name = null, Guid? GardenId = null);
public sealed record AddDeviceByIpInput([property: Description("IP address or host of the client, optionally with :port (default 8787)")] string Address);
public sealed record AddDeviceByIpResult(bool Contacted, string Message, string ServerUrlSent);

public sealed record DeviceDetail(DeviceDto Device, IReadOnlyList<DevicePlugin> Plugins, IReadOnlyList<DiscoveredHardware> Discovered,
    IReadOnlyList<SensorDto> Sensors, IReadOnlyList<ActuatorDto> Actuators, IReadOnlyList<CameraDto> Cameras, IReadOnlyList<DeviceEvent> RecentEvents);

public sealed record TargetInput(
    [property: Description("garden | bed | plant — what this component measures or serves")] string? TargetType = null,
    Guid? GardenId = null, Guid? BedId = null, Guid? PlantId = null);

public sealed record ChannelInput(
    [property: Description("Plugin channel key, e.g. 'value', 'temperature', 'raw'")] string Key,
    [property: Description("Metric id, e.g. soil_moisture_pct, air_temperature_c, water_level_pct")] string Metric,
    string? Unit = null,
    string? DisplayName = null,
    CalibrationConfig? Calibration = null,
    double? MinValid = null,
    double? MaxValid = null,
    double? AlertLow = null,
    double? AlertHigh = null);

public sealed record AdoptHardwareInput(
    Guid DiscoveredId,
    string? Name = null,
    [property: Description("garden | bed | plant | reservoir")] string? TargetType = null,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    int? ReadIntervalSec = null,
    Guid? ReservoirId = null);

public sealed record AdoptResult(string Kind, Guid ComponentId, string Name);

public sealed record ListComponentsInput(Guid? GardenId = null, Guid? DeviceId = null, Guid? BedId = null, Guid? PlantId = null, Guid? ReservoirId = null);

public sealed record CreateSensorInput(
    Guid DeviceId,
    string Name,
    [property: Description("Sensor plugin id on the device, e.g. emulator-sensor, ds18b20, ads1115, bme280, gpio-digital-input")] string PluginId,
    [property: Description("Plugin-specific settings object (pins, I2C address, emulator sensorId…)")] JsonObject? Settings,
    List<ChannelInput> Channels,
    [property: Description("garden | bed | plant")] string TargetType,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    int? ReadIntervalSec = null,
    bool? Enabled = null,
    Guid? ReservoirId = null);

public sealed record UpdateSensorInput(
    Guid SensorId,
    string? Name = null,
    JsonObject? Settings = null,
    [property: Description("Seconds between readings")] int? ReadIntervalSec = null,
    bool? Enabled = null,
    string? TargetType = null,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    Guid? ReservoirId = null);

public sealed record SensorIdInput(Guid SensorId);

public sealed record UpdateChannelInput(
    Guid ChannelId,
    string? Metric = null,
    string? Unit = null,
    string? DisplayName = null,
    double? MinValid = null,
    double? MaxValid = null,
    [property: Description("Notify when the value drops below this")] double? AlertLow = null,
    [property: Description("Notify when the value rises above this")] double? AlertHigh = null);

public sealed record CalibrateChannelInput(
    Guid ChannelId,
    [property: Description("{ type: none|linear|two_point|polynomial, scale, offset, rawLow, valueLow, rawHigh, valueHigh, coefficients, clamp }. For capacitive soil probes use two_point: rawLow=dry reading→valueLow=0, rawHigh=wet reading→valueHigh=100.")] CalibrationConfig Calibration);

public sealed record ReadingsInput(
    Guid? ChannelId = null,
    Guid? SensorId = null,
    [property: Description("Hours of history (default 24, max 2160)")] double? Hours = null,
    [property: Description("Aggregate into buckets of this many minutes (default: automatic)")] int? BucketMinutes = null);

public sealed record ReadingPoint(DateTimeOffset Time, double Avg, double Min, double Max, int Count);
public sealed record ChannelSeries(Guid ChannelId, Guid SensorId, string SensorName, string Metric, string Unit, int BucketMinutes, IReadOnlyList<ReadingPoint> Points);

public sealed record CreateActuatorInput(
    Guid DeviceId,
    string Name,
    [property: Description("pump | valve | light | heater | fan | fertilizer_doser | mister | other")] string Kind,
    [property: Description("Actuator plugin id, e.g. emulator-actuator, gpio-relay")] string PluginId,
    JsonObject? Settings,
    [property: Description("garden | bed | plant")] string TargetType,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    [property: Description("Flow rate for pumps/dosers; enables amount-based dosing and per-day ml limits")] double? FlowRateMlPerMin = null,
    [property: Description("off | on — state applied on shutdown, errors and emergency stop")] string? FailsafeState = null,
    bool? Enabled = null,
    [property: Description("irrigation | circulation_pump | air_pump | dose_nutrient_a | dose_nutrient_b | dose_ph_up | dose_ph_down | dose_calmag | dose_additive | chiller | top_up_valve | drain")] string? Role = null,
    [property: Description("Keep it running 24/7 on the device (NFT/DWC/RDWC circulation and air pumps)")] bool? ContinuousRun = null,
    [property: Description("Flow sensor channel that proves this pump/valve is moving water (stall detection)")] Guid? FlowChannelId = null,
    [property: Description("Minimum L/min expected while on; below it after the grace period = flow stall")] double? FlowMinWhenOn = null,
    [property: Description("Seconds to wait after switching before judging flow (default 20)")] int? FlowGraceSec = null,
    [property: Description("Flow above this while off = unexpected flow (leak/stuck valve)")] double? FlowMaxWhenOff = null,
    [property: Description("Switch off on a flow stall to protect the pump (default true)")] bool? StopOnStall = null,
    Guid? ReservoirId = null);

public sealed record UpdateActuatorInput(
    Guid ActuatorId,
    string? Name = null,
    string? Kind = null,
    JsonObject? Settings = null,
    string? TargetType = null,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    double? FlowRateMlPerMin = null,
    string? FailsafeState = null,
    bool? Enabled = null,
    [property: Description("irrigation | circulation_pump | air_pump | dose_nutrient_a | dose_nutrient_b | dose_ph_up | dose_ph_down | dose_calmag | dose_additive | chiller | top_up_valve | drain")] string? Role = null,
    [property: Description("Keep it running 24/7 on the device (NFT/DWC/RDWC circulation and air pumps)")] bool? ContinuousRun = null,
    [property: Description("Flow sensor channel that proves this pump/valve is moving water (stall detection)")] Guid? FlowChannelId = null,
    [property: Description("Minimum L/min expected while on; below it after the grace period = flow stall")] double? FlowMinWhenOn = null,
    [property: Description("Seconds to wait after switching before judging flow (default 20)")] int? FlowGraceSec = null,
    [property: Description("Flow above this while off = unexpected flow (leak/stuck valve)")] double? FlowMaxWhenOff = null,
    [property: Description("Switch off on a flow stall to protect the pump (default true)")] bool? StopOnStall = null,
    Guid? ReservoirId = null);

public sealed record ActuatorIdInput(Guid ActuatorId);

public sealed record RunActuatorInput(
    Guid ActuatorId,
    [property: Description("pulse (on for durationSec) | on | off | dose (amountMl; needs a flow rate)")] string Action,
    double? DurationSec = null,
    double? AmountMl = null,
    [property: Description("0..1 for dimmable outputs")] double? Level = null);

public sealed record ActuatorHistoryInput(Guid ActuatorId, double? Hours = null);
public sealed record ActuatorHistory(IReadOnlyList<ActuatorRun> Runs, IReadOnlyList<ActuatorCommand> Commands, double RunSecondsToday, ActuatorSafety EffectiveSafety);

public sealed record CreateCameraInput(
    Guid DeviceId,
    string Name,
    [property: Description("Camera plugin id, e.g. emulator-camera")] string PluginId,
    JsonObject? Settings,
    [property: Description("garden | bed | plant")] string TargetType,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    int? CaptureWidth = null,
    int? CaptureHeight = null,
    [property: Description("Seconds between frames for the dashboard feed")] int? FrameIntervalSec = null,
    bool? Enabled = null,
    Guid? ReservoirId = null);

public sealed record UpdateCameraInput(
    Guid CameraId,
    string? Name = null,
    JsonObject? Settings = null,
    string? TargetType = null,
    Guid? GardenId = null,
    Guid? BedId = null,
    Guid? PlantId = null,
    int? CaptureWidth = null,
    int? CaptureHeight = null,
    int? FrameIntervalSec = null,
    bool? Enabled = null,
    Guid? ReservoirId = null);

public sealed record CameraIdInput(Guid CameraId);
public sealed record CaptureInput(Guid CameraId, [property: Description("Requested width in pixels (default 1024)")] int? Width = null);
public sealed record CaptureResult(Guid MediaId, string ImageUrl, int Width, int Height, DateTimeOffset CapturedAt, string Note);

public static class DeviceTools
{
    public const string Category = "Devices & hardware";

    public static void Register(ToolRegistry r)
    {
        RegisterDevices(r);
        RegisterSensors(r);
        RegisterActuators(r);
        RegisterCameras(r);
    }

    private static Task Changed(ToolContext ctx, params string[] areas) => ctx.Get<LiveNotifier>().Changed(areas);

    private sealed record Target(Guid GardenId, Guid? BedId, Guid? PlantId, Guid? ReservoirId, string TargetType);

    private static async Task<Target> ResolveTargetAsync(ToolContext ctx, string? targetType, Guid? gardenId, Guid? bedId, Guid? plantId, Guid? deviceGarden, Guid? reservoirId = null)
    {
        targetType ??= plantId is not null ? "plant" : bedId is not null ? "bed" : reservoirId is not null ? "reservoir" : "garden";
        Ensure.OneOf(targetType, TargetTypes.Component, "targetType");
        switch (targetType)
        {
            case "plant":
            {
                var pid = plantId ?? throw AppException.Bad("plantId is required when targetType=plant");
                var x = await ctx.Db.Plants.Where(p => p.Id == pid).Select(p => new { p.BedId, p.Bed!.GardenId }).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Plant");
                return new Target(x.GardenId, x.BedId, pid, null, targetType);
            }
            case "bed":
            {
                var bid = bedId ?? throw AppException.Bad("bedId is required when targetType=bed");
                var g = await ctx.Db.Beds.Where(b => b.Id == bid).Select(b => (Guid?)b.GardenId).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Bed");
                return new Target(g, bid, null, null, targetType);
            }
            case "reservoir":
            {
                var rid = reservoirId ?? throw AppException.Bad("reservoirId is required when targetType=reservoir");
                var g = await ctx.Db.Reservoirs.Where(r => r.Id == rid).Select(r => (Guid?)r.GardenId).FirstOrDefaultAsync(ctx.Ct) ?? throw AppException.NotFound("Reservoir");
                return new Target(g, null, null, rid, targetType);
            }
            default:
                return new Target((await GardenTools.ResolveGardenAsync(ctx.Db, gardenId ?? deviceGarden, ctx.Ct)).Id, null, null, null, targetType);
        }
    }

    private static void ApplyTarget(ITargeted t, Target target)
    {
        t.TargetType = target.TargetType;
        t.GardenId = target.GardenId;
        t.BedId = target.BedId;
        t.PlantId = target.PlantId;
        t.ReservoirId = target.ReservoirId;
    }

    private static async Task<Device> ApprovedDeviceAsync(ToolContext ctx, Guid id)
    {
        var d = Ensure.Found(await ctx.Db.Devices.FirstOrDefaultAsync(x => x.Id == id, ctx.Ct), "Device");
        if (d.Status != DeviceStatuses.Approved) throw AppException.Bad($"Device '{d.Name}' is {d.Status}; approve it first");
        return d;
    }

    private static async Task<string> TargetLabelAsync(ToolContext ctx, Guid? bedId, Guid? plantId, Guid? reservoirId = null)
    {
        if (reservoirId is { } r) return "reservoir " + await ctx.Db.Reservoirs.Where(x => x.Id == r).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct);
        if (plantId is { } p) return "plant " + await ctx.Db.Plants.Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct);
        if (bedId is { } b) return "bed " + await ctx.Db.Beds.Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ctx.Ct);
        return "garden";
    }

    // ------------------------------------------------------------------ devices
    private static void RegisterDevices(ToolRegistry r)
    {
        r.Read<ListDevicesInput, List<DeviceDto>>("list_devices", Category, "List SlopGarden client devices, including ones pending Admin approval, with online state and plugin trust warnings.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Devices.AsNoTracking();
                if (i.Status is { } s) q = q.Where(d => d.Status == s);
                var list = await q.OrderBy(d => d.Status).ThenBy(d => d.Name).ToListAsync(ctx.Ct);
                var trust = await ctx.Db.DevicePlugins.AsNoTracking().GroupBy(p => p.DeviceId)
                    .Select(g => new { g.Key, u = g.Count(p => p.Trust == PluginTrust.Unsigned), inv = g.Count(p => p.Trust == PluginTrust.Invalid) })
                    .ToDictionaryAsync(x => x.Key, ctx.Ct);
                var latest = (await ClientUpdateService.ReadAsync(ctx.Db, ctx.Ct)).Version;
                return list.Select(d => DeviceDto.From(d, trust.GetValueOrDefault(d.Id)?.u ?? 0, trust.GetValueOrDefault(d.Id)?.inv ?? 0, latest)).ToList();
            });

        r.Read<DeviceIdInput, DeviceDetail>("get_device", Category, "One device with its installed plugins (and their signature trust), discovered hardware, components and recent events.",
            async (ctx, i) =>
            {
                var db = ctx.Db;
                var d = Ensure.Found(await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.DeviceId, ctx.Ct), "Device");
                var plugins = await db.DevicePlugins.AsNoTracking().Where(p => p.DeviceId == d.Id).OrderBy(p => p.PluginId).ToListAsync(ctx.Ct);
                var discovered = await db.DiscoveredHardware.AsNoTracking().Where(x => x.DeviceId == d.Id).OrderBy(x => x.Kind).ThenBy(x => x.SuggestedName).ToListAsync(ctx.Ct);
                var sensors = await db.Sensors.AsNoTracking().Include(s => s.Channels).Where(s => s.DeviceId == d.Id).OrderBy(s => s.Name).ToListAsync(ctx.Ct);
                var actuators = await db.Actuators.AsNoTracking().Where(a => a.DeviceId == d.Id).OrderBy(a => a.Name).ToListAsync(ctx.Ct);
                var cameras = await db.Cameras.AsNoTracking().Where(c => c.DeviceId == d.Id).OrderBy(c => c.Name).ToListAsync(ctx.Ct);
                var events = await db.DeviceEvents.AsNoTracking().Where(e => e.DeviceId == d.Id).OrderByDescending(e => e.Time).Take(30).ToListAsync(ctx.Ct);
                var latest = (await ClientUpdateService.ReadAsync(db, ctx.Ct)).Version;
                return new DeviceDetail(DeviceDto.From(d, plugins.Count(p => p.Trust == PluginTrust.Unsigned), plugins.Count(p => p.Trust == PluginTrust.Invalid), latest),
                    plugins, discovered, sensors.Select(SensorDto.From).ToList(), actuators.Select(ActuatorDto.From).ToList(), cameras.Select(CameraDto.From).ToList(), events);
            });

        r.Mutate<ApproveDeviceInput, DeviceDto>("approve_device", Category, "Approve a pending device (Admin). Generates its credential.",
            async (ctx, i) => DeviceDto.From(await ctx.Get<DeviceRegistry>().ApproveAsync(i.DeviceId, i.GardenId, i.Name, ctx.Actor, ctx.Ct)),
            async (ctx, i) => new ProposalPreview($"Approve device {i.DeviceId}", "device", i.DeviceId, i.GardenId, "high", Patch.CreateChanges(i)),
            minRole: Roles.Admin, aiAllowed: false);

        r.Mutate<DeviceIdInput, DeviceDto>("reject_device", Category, "Reject a pending device (Admin).",
            async (ctx, i) => DeviceDto.From(await ctx.Get<DeviceRegistry>().SetStatusAsync(i.DeviceId, DeviceStatuses.Rejected, ctx.Actor, ctx.Ct)),
            (ctx, i) => Task.FromResult(new ProposalPreview($"Reject device {i.DeviceId}", "device", i.DeviceId, null, "medium", [])),
            minRole: Roles.Admin, aiAllowed: false);

        r.Mutate<DeviceIdInput, DeviceDto>("revoke_device", Category, "Revoke an approved device's credential (Admin). It must be approved again.",
            async (ctx, i) => DeviceDto.From(await ctx.Get<DeviceRegistry>().SetStatusAsync(i.DeviceId, DeviceStatuses.Revoked, ctx.Actor, ctx.Ct)),
            (ctx, i) => Task.FromResult(new ProposalPreview($"Revoke device {i.DeviceId}", "device", i.DeviceId, null, "high", [])),
            minRole: Roles.Admin, aiAllowed: false);

        r.Mutate<DeviceIdInput, DeviceRemoved>("remove_device", Category,
            "Delete a device and its sensors, cameras, actuators, readings and frames (Admin). Plants and beds stay. A client that is still running shows up again as pending.",
            async (ctx, i) => await ctx.Get<DeviceRegistry>().RemoveAsync(i.DeviceId, ctx.Actor, ctx.Ct),
            (ctx, i) => Task.FromResult(new ProposalPreview($"Remove device {i.DeviceId} and everything configured on it", "device", i.DeviceId, null, "high", [])),
            minRole: Roles.Admin, aiAllowed: false);

        r.Mutate<UpdateDeviceInput, DeviceDto>("update_device", Category, "Rename a device or change its default garden.",
            async (ctx, i) =>
            {
                var d = Ensure.Found(await ctx.Db.Devices.FirstOrDefaultAsync(x => x.Id == i.DeviceId, ctx.Ct), "Device");
                Patch.Apply(d, i, nameof(i.DeviceId));
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(d.Id);
                await Changed(ctx, "devices");
                return DeviceDto.From(d);
            },
            async (ctx, i) =>
            {
                var d = Ensure.Found(await ctx.Db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.DeviceId, ctx.Ct), "Device");
                return new ProposalPreview($"Update device '{d.Name}'", "device", d.Id, d.GardenId, "low", Patch.UpdateChanges(d, i, nameof(i.DeviceId)));
            });

        r.Mutate<AddDeviceByIpInput, AddDeviceByIpResult>("add_device_by_ip", Category,
            "Admin: contact a SlopGarden client at an IP address and tell it to register with this server. It then appears as pending.",
            async (ctx, i) =>
            {
                var serverUrl = ctx.Get<ServerUrlProvider>().Get();
                var address = i.Address.Trim();
                if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) address = "http://" + address;
                var uri = new UriBuilder(address);
                if (uri.Uri.IsDefaultPort && !i.Address.Contains(':')) uri.Port = Discovery.ClientLocalApiPort;
                uri.Path = "/pair";
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var res = await http.PostAsJsonAsync(uri.Uri, new ManualPairRequest(serverUrl), SlopJson.Options, ctx.Ct);
                    ctx.Db.Audit(ctx.Actor, "device.add_by_ip", "device", null, new() { ["address"] = i.Address, ["status"] = (int)res.StatusCode });
                    await ctx.Db.SaveChangesAsync(ctx.Ct);
                    return res.IsSuccessStatusCode
                        ? new AddDeviceByIpResult(true, "Client contacted; it will appear under pending devices within a few seconds.", serverUrl)
                        : new AddDeviceByIpResult(false, $"Client answered HTTP {(int)res.StatusCode}", serverUrl);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return new AddDeviceByIpResult(false, $"Could not reach {uri.Uri}: {ex.Message}", serverUrl);
                }
            },
            (ctx, i) => Task.FromResult(new ProposalPreview($"Add device at {i.Address}", "device", null, null, "medium", [])),
            minRole: Roles.Admin, aiAllowed: false);

        r.Read<DeviceIdInput, List<DiscoveredHardware>>("list_discovered_hardware", Category, "Hardware a device found (I²C scan, 1-Wire probes, emulator inventory) that can be adopted as sensors/actuators/cameras.",
            async (ctx, i) => await ctx.Db.DiscoveredHardware.AsNoTracking().Where(x => x.DeviceId == i.DeviceId).OrderBy(x => x.Kind).ThenBy(x => x.SuggestedName).ToListAsync(ctx.Ct));

        r.Mutate<AdoptHardwareInput, AdoptResult>("adopt_discovered_hardware", Category,
            "Turn a discovered hardware item into a configured sensor, actuator or camera targeting a garden, bed or plant.",
            async (ctx, i) =>
            {
                var h = Ensure.Found(await ctx.Db.DiscoveredHardware.FirstOrDefaultAsync(x => x.Id == i.DiscoveredId, ctx.Ct), "Discovered hardware");
                var device = await ApprovedDeviceAsync(ctx, h.DeviceId);
                var target = await ResolveTargetAsync(ctx, i.TargetType, i.GardenId, i.BedId, i.PlantId, device.GardenId, i.ReservoirId);
                var name = i.Name ?? h.SuggestedName;
                Guid id;
                switch (h.Kind)
                {
                    case PluginKinds.Sensor:
                    {
                        var s = new Sensor { DeviceId = device.Id, Name = name, PluginId = h.PluginId, Settings = h.SuggestedSettings.DeepClone().AsObject(), ReadIntervalSec = i.ReadIntervalSec ?? 30 };
                        ApplyTarget(s, target);
                        foreach (var ch in h.Channels.Deserialize<List<DiscoveredChannel>>(SlopJson.Options) ?? [])
                            s.Channels.Add(new SensorChannel { Key = ch.Key, Metric = ch.Metric, Unit = ch.Unit });
                        ctx.Db.Sensors.Add(s);
                        id = s.Id;
                        break;
                    }
                    case PluginKinds.Actuator:
                    {
                        var a = new Actuator { DeviceId = device.Id, Name = name, Kind = h.ActuatorKind ?? ActuatorKinds.Other, PluginId = h.PluginId, Settings = h.SuggestedSettings.DeepClone().AsObject() };
                        ApplyTarget(a, target);
                        ctx.Db.Actuators.Add(a);
                        id = a.Id;
                        break;
                    }
                    case PluginKinds.Camera:
                    {
                        var c = new Camera { DeviceId = device.Id, Name = name, PluginId = h.PluginId, Settings = h.SuggestedSettings.DeepClone().AsObject() };
                        ApplyTarget(c, target);
                        ctx.Db.Cameras.Add(c);
                        id = c.Id;
                        break;
                    }
                    default:
                        throw AppException.Bad($"Unknown hardware kind {h.Kind}");
                }
                h.AdoptedComponentId = id;
                ctx.Db.Audit(ctx.Actor, "hardware.adopt", h.Kind, id, new() { ["plugin"] = h.PluginId });
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(device.Id);
                await Changed(ctx, "devices", "sensors", "actuators", "cameras");
                return new AdoptResult(h.Kind, id, name);
            },
            async (ctx, i) =>
            {
                var h = Ensure.Found(await ctx.Db.DiscoveredHardware.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.DiscoveredId, ctx.Ct), "Discovered hardware");
                var label = await TargetLabelAsync(ctx, i.BedId, i.PlantId, i.ReservoirId);
                return new ProposalPreview($"Add {h.Kind} '{i.Name ?? h.SuggestedName}' for {label}", i.PlantId is not null ? "plant" : i.BedId is not null ? "bed" : i.ReservoirId is not null ? "reservoir" : "garden",
                    i.PlantId ?? i.BedId ?? i.ReservoirId ?? i.GardenId, i.GardenId, "low", [Patch.Change("plugin", null, h.PluginId), Patch.Change("target", null, label)]);
            });
    }

    // ------------------------------------------------------------------ sensors
    private static void RegisterSensors(ToolRegistry r)
    {
        r.Read<ListComponentsInput, List<SensorDto>>("list_sensors", Category, "List sensors with their channels, calibration and latest values.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Sensors.AsNoTracking().Include(s => s.Channels).Include(s => s.Device).AsQueryable();
                if (i.GardenId is { } g) q = q.Where(s => s.GardenId == g);
                if (i.DeviceId is { } d) q = q.Where(s => s.DeviceId == d);
                if (i.BedId is { } b) q = q.Where(s => s.BedId == b);
                if (i.PlantId is { } p) q = q.Where(s => s.PlantId == p);
                if (i.ReservoirId is { } r) q = q.Where(s => s.ReservoirId == r);
                return (await q.OrderBy(s => s.Name).ToListAsync(ctx.Ct)).Select(SensorDto.From).ToList();
            });

        r.Mutate<CreateSensorInput, SensorDto>("create_sensor", Category, "Configure a new sensor on a device, targeting a garden, bed or single plant.",
            async (ctx, i) =>
            {
                var device = await ApprovedDeviceAsync(ctx, i.DeviceId);
                var target = await ResolveTargetAsync(ctx, i.TargetType, i.GardenId, i.BedId, i.PlantId, device.GardenId, i.ReservoirId);
                if (i.Channels.Count == 0) throw AppException.Bad("At least one channel is required");
                var s = new Sensor
                {
                    DeviceId = device.Id, Name = i.Name, PluginId = i.PluginId, Settings = i.Settings ?? new JsonObject(), ReadIntervalSec = Math.Clamp(i.ReadIntervalSec ?? 60, 1, 86400),
                    Enabled = i.Enabled ?? true,
                };
                ApplyTarget(s, target);
                foreach (var c in i.Channels) s.Channels.Add(NewChannel(c));
                ctx.Db.Sensors.Add(s);
                ctx.Db.Audit(ctx.Actor, "sensor.create", "sensor", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(device.Id);
                await Changed(ctx, "sensors");
                return SensorDto.From(s);
            },
            async (ctx, i) => new ProposalPreview($"Add sensor '{i.Name}' for {await TargetLabelAsync(ctx, i.BedId, i.PlantId, i.ReservoirId)}", i.TargetType, i.PlantId ?? i.BedId ?? i.ReservoirId ?? i.GardenId, i.GardenId, "low", Patch.CreateChanges(i)));

        r.Mutate<UpdateSensorInput, SensorDto>("update_sensor", Category, "Change a sensor's name, settings, reading interval, enabled flag or target.",
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Sensors.Include(x => x.Channels).FirstOrDefaultAsync(x => x.Id == i.SensorId, ctx.Ct), "Sensor");
                if (i.TargetType is not null || i.BedId is not null || i.PlantId is not null || i.GardenId is not null || i.ReservoirId is not null)
                    ApplyTarget(s, await ResolveTargetAsync(ctx, i.TargetType ?? s.TargetType, i.GardenId ?? s.GardenId, i.BedId ?? s.BedId, i.PlantId ?? s.PlantId, null, i.ReservoirId ?? s.ReservoirId));
                Patch.Apply(s, i, nameof(i.SensorId), nameof(i.TargetType), nameof(i.GardenId), nameof(i.BedId), nameof(i.PlantId), nameof(i.ReservoirId));
                s.ReadIntervalSec = Math.Clamp(s.ReadIntervalSec, 1, 86400);
                ctx.Db.Audit(ctx.Actor, "sensor.update", "sensor", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(s.DeviceId);
                await Changed(ctx, "sensors");
                return SensorDto.From(s);
            },
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Sensors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.SensorId, ctx.Ct), "Sensor");
                return new ProposalPreview($"Update sensor '{s.Name}'", s.TargetType, s.PlantId ?? s.BedId ?? s.GardenId, s.GardenId, "low", Patch.UpdateChanges(s, i, nameof(i.SensorId)));
            });

        r.Mutate<SensorIdInput, bool>("delete_sensor", Category, "Delete a sensor and its reading history.",
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Sensors.FirstOrDefaultAsync(x => x.Id == i.SensorId, ctx.Ct), "Sensor");
                ctx.Db.Sensors.Remove(s);
                ctx.Db.Audit(ctx.Actor, "sensor.delete", "sensor", s.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(s.DeviceId);
                await Changed(ctx, "sensors");
                return true;
            },
            async (ctx, i) =>
            {
                var s = Ensure.Found(await ctx.Db.Sensors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.SensorId, ctx.Ct), "Sensor");
                return new ProposalPreview($"Delete sensor '{s.Name}' and its history", s.TargetType, s.PlantId ?? s.BedId ?? s.GardenId, s.GardenId, "high", [Patch.Change("sensor", s.Name, null)]);
            });

        r.Mutate<UpdateChannelInput, ChannelDto>("update_sensor_channel", Category, "Change a channel's metric/unit/display name, valid range or alert thresholds.",
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.SensorChannels.Include(x => x.Sensor).FirstOrDefaultAsync(x => x.Id == i.ChannelId, ctx.Ct), "Channel");
                Patch.Apply(c, i, nameof(i.ChannelId));
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(c.Sensor!.DeviceId);
                await Changed(ctx, "sensors");
                return ChannelDto.From(c);
            },
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.SensorChannels.AsNoTracking().Include(x => x.Sensor).FirstOrDefaultAsync(x => x.Id == i.ChannelId, ctx.Ct), "Channel");
                return new ProposalPreview($"Update channel {c.Metric} of '{c.Sensor!.Name}'", "sensor", c.SensorId, c.Sensor.GardenId, "low", Patch.UpdateChanges(c, i, nameof(i.ChannelId)));
            });

        r.Mutate<CalibrateChannelInput, ChannelDto>("calibrate_channel", Category, "Set a channel's calibration (raw → value mapping). Applied on the device.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Calibration.Type, ["none", "linear", "two_point", "polynomial"], "calibration.type");
                var c = Ensure.Found(await ctx.Db.SensorChannels.Include(x => x.Sensor).FirstOrDefaultAsync(x => x.Id == i.ChannelId, ctx.Ct), "Channel");
                c.Calibration = i.Calibration;
                c.CalibrationStatus = i.Calibration.Type == "none" ? "uncalibrated" : ctx.ChangeActorType == ActorTypes.Ai ? "ai_calibrated" : "calibrated";
                c.CalibratedAt = DateTimeOffset.UtcNow;
                ctx.Db.Audit(ctx.Actor, "channel.calibrate", "sensor", c.SensorId);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(c.Sensor!.DeviceId);
                await Changed(ctx, "sensors");
                return ChannelDto.From(c);
            },
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.SensorChannels.AsNoTracking().Include(x => x.Sensor).FirstOrDefaultAsync(x => x.Id == i.ChannelId, ctx.Ct), "Channel");
                return new ProposalPreview($"Calibrate {c.Metric} on '{c.Sensor!.Name}'", "sensor", c.SensorId, c.Sensor.GardenId, "medium", [Patch.Change("calibration", c.Calibration, i.Calibration)]);
            });

        r.Read<ReadingsInput, List<ChannelSeries>>("get_readings", Category, "Historical readings for a channel or all channels of a sensor, aggregated into time buckets (avg/min/max).",
            (ctx, i) => ctx.Get<ReadingsService>().SeriesAsync(i.ChannelId, i.SensorId, i.Hours ?? 24, i.BucketMinutes, ctx.Ct));
    }

    private static SensorChannel NewChannel(ChannelInput c) => new()
    {
        Key = c.Key,
        Metric = c.Metric,
        Unit = c.Unit ?? Metrics.DefaultUnits.GetValueOrDefault(c.Metric, ""),
        DisplayName = c.DisplayName,
        Calibration = c.Calibration,
        CalibrationStatus = c.Calibration is null or { Type: "none" } ? "uncalibrated" : "calibrated",
        CalibratedAt = c.Calibration is null ? null : DateTimeOffset.UtcNow,
        MinValid = c.MinValid,
        MaxValid = c.MaxValid,
        AlertLow = c.AlertLow,
        AlertHigh = c.AlertHigh,
    };

    // ------------------------------------------------------------------ actuators
    private static void RegisterActuators(ToolRegistry r)
    {
        r.Read<ListComponentsInput, List<ActuatorDto>>("list_actuators", Category, "List actuators (pumps, valves, lights, heaters, fans, dosers) with current on/off state.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Actuators.AsNoTracking().Include(a => a.Device).AsQueryable();
                if (i.GardenId is { } g) q = q.Where(a => a.GardenId == g);
                if (i.DeviceId is { } d) q = q.Where(a => a.DeviceId == d);
                if (i.BedId is { } b) q = q.Where(a => a.BedId == b);
                if (i.PlantId is { } p) q = q.Where(a => a.PlantId == p);
                if (i.ReservoirId is { } r) q = q.Where(a => a.ReservoirId == r);
                return (await q.OrderBy(a => a.Name).ToListAsync(ctx.Ct)).Select(ActuatorDto.From).ToList();
            });

        r.Mutate<CreateActuatorInput, ActuatorDto>("create_actuator", Category, "Configure a new actuator on a device.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Kind, ActuatorKinds.All, "kind");
                Ensure.OneOf(i.FailsafeState, ["off", "on"], "failsafeState");
                var device = await ApprovedDeviceAsync(ctx, i.DeviceId);
                Ensure.OneOf(i.Role, ActuatorRoles.All, "role");
                var target = await ResolveTargetAsync(ctx, i.TargetType, i.GardenId, i.BedId, i.PlantId, device.GardenId, i.ReservoirId);
                var a = new Actuator
                {
                    DeviceId = device.Id, Name = i.Name, Kind = i.Kind, PluginId = i.PluginId, Settings = i.Settings ?? new JsonObject(), Enabled = i.Enabled ?? true,
                    FailsafeState = i.FailsafeState ?? "off", FlowRateMlPerMin = i.FlowRateMlPerMin, Role = i.Role, ContinuousRun = i.ContinuousRun ?? ActuatorRoles.IsContinuous(i.Role),
                    FlowChannelId = i.FlowChannelId, FlowMinWhenOn = i.FlowMinWhenOn, FlowGraceSec = i.FlowGraceSec, FlowMaxWhenOff = i.FlowMaxWhenOff, StopOnStall = i.StopOnStall ?? true,
                };
                ApplyTarget(a, target);
                ctx.Db.Actuators.Add(a);
                ctx.Db.Audit(ctx.Actor, "actuator.create", "actuator", a.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(device.Id);
                await Changed(ctx, "actuators");
                return ActuatorDto.From(a);
            },
            async (ctx, i) => new ProposalPreview($"Add {i.Kind} '{i.Name}' for {await TargetLabelAsync(ctx, i.BedId, i.PlantId, i.ReservoirId)}", i.TargetType, i.PlantId ?? i.BedId ?? i.ReservoirId ?? i.GardenId, i.GardenId, "medium", Patch.CreateChanges(i)));

        r.Mutate<UpdateActuatorInput, ActuatorDto>("update_actuator", Category, "Change an actuator's name, kind, settings, flow rate, failsafe state, enabled flag or target.",
            async (ctx, i) =>
            {
                Ensure.OneOf(i.Kind, ActuatorKinds.All, "kind");
                Ensure.OneOf(i.FailsafeState, ["off", "on"], "failsafeState");
                Ensure.OneOf(i.Role, ActuatorRoles.All, "role");
                var a = Ensure.Found(await ctx.Db.Actuators.FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                if (i.TargetType is not null || i.BedId is not null || i.PlantId is not null || i.GardenId is not null || i.ReservoirId is not null)
                    ApplyTarget(a, await ResolveTargetAsync(ctx, i.TargetType ?? a.TargetType, i.GardenId ?? a.GardenId, i.BedId ?? a.BedId, i.PlantId ?? a.PlantId, null, i.ReservoirId ?? a.ReservoirId));
                if (i.FlowChannelId is { } fc && !await ctx.Db.SensorChannels.AnyAsync(c => c.Id == fc, ctx.Ct)) throw AppException.NotFound("Flow channel");
                Patch.Apply(a, i, nameof(i.ActuatorId), nameof(i.TargetType), nameof(i.GardenId), nameof(i.BedId), nameof(i.PlantId), nameof(i.ReservoirId));
                ctx.Db.Audit(ctx.Actor, "actuator.update", "actuator", a.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(a.DeviceId);
                await Changed(ctx, "actuators");
                return ActuatorDto.From(a);
            },
            async (ctx, i) =>
            {
                var a = Ensure.Found(await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                return new ProposalPreview($"Update actuator '{a.Name}'", "actuator", a.Id, a.GardenId, "medium", Patch.UpdateChanges(a, i, nameof(i.ActuatorId)));
            });

        r.Mutate<ActuatorIdInput, bool>("delete_actuator", Category, "Delete an actuator (its schedules are deleted too).",
            async (ctx, i) =>
            {
                var a = Ensure.Found(await ctx.Db.Actuators.FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                ctx.Db.Actuators.Remove(a);
                ctx.Db.Audit(ctx.Actor, "actuator.delete", "actuator", a.Id);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(a.DeviceId);
                await Changed(ctx, "actuators", "schedules");
                return true;
            },
            async (ctx, i) =>
            {
                var a = Ensure.Found(await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                return new ProposalPreview($"Delete actuator '{a.Name}'", "actuator", a.Id, a.GardenId, "high", [Patch.Change("actuator", a.Name, null)]);
            });

        r.Mutate<RunActuatorInput, CommandResult>("run_actuator", Category,
            "Run an actuator now: pulse for N seconds (watering), switch on/off, or dose N ml. Always subject to safety limits and emergency stops. A routine action: after the user approves once with 'always allow', the AI may repeat it within the same limits.",
            async (ctx, i) =>
            {
                var actor = ctx.Mode == ToolMode.Approved ? ctx.Actor with { Type = ActorTypes.Ai, Label = $"AI (approved by {ctx.Actor.Label})" } : ctx.Actor;
                var result = await ctx.Get<CommandService>().IssueAsync(i.ActuatorId, i.Action, i.DurationSec, i.AmountMl, i.Level, ctx.Reason ?? "", actor, ctx.ProposalId, null, ctx.Ct);
                if (ctx.ActivityId is { } activityId && result.Status != CommandStatuses.Blocked && i.Action != ActuatorActions.Off)
                {
                    var activity = await ctx.Db.AiActivities.FirstOrDefaultAsync(a => a.Id == activityId, ctx.Ct);
                    if (activity is not null)
                    {
                        activity.CommandId = result.CommandId;
                        await ctx.Db.SaveChangesAsync(ctx.Ct);
                        ctx.ActivityHandedOff = true;
                    }
                }
                if (result.Status == CommandStatuses.Blocked) throw AppException.Bad($"Blocked by safety: {result.BlockedReason}");
                return result;
            },
            async (ctx, i) =>
            {
                var a = Ensure.Found(await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                var what = i.Action switch
                {
                    ActuatorActions.Pulse => $"Run '{a.Name}' for {i.DurationSec:0} s",
                    ActuatorActions.Dose => $"Dose {i.AmountMl:0.#} ml with '{a.Name}'",
                    ActuatorActions.On => $"Switch '{a.Name}' on",
                    _ => $"Switch '{a.Name}' off",
                };
                var changes = new JsonArray(Patch.Change("state", a.IsOn ? "on" : "off", i.Action == ActuatorActions.Off ? "off" : "on"));
                if (i.DurationSec is not null) changes.Add(Patch.Change("durationSec", null, i.DurationSec));
                if (i.AmountMl is not null) changes.Add(Patch.Change("amountMl", null, i.AmountMl));
                if (ActuatorRoles.IsDosing(a.Role) || a.Kind == ActuatorKinds.FertilizerDoser)
                {
                    var ml = i.AmountMl ?? (i.DurationSec is { } d && a.FlowRateMlPerMin is { } fr ? d * fr / 60.0 : null);
                    var label = a.Role is { } role ? role.Replace("dose_", "").Replace('_', ' ') : "fertilizer";
                    return new ProposalPreview(ml is { } m ? $"Dose {m:0.#} ml {label} with '{a.Name}'" : what, "actuator", a.Id, a.GardenId, "medium", changes, AllowStanding: false);
                }
                return new ProposalPreview(what, "actuator", a.Id, a.GardenId, a.Kind == ActuatorKinds.Heater ? "medium" : "low", changes);
            },
            routine: true);

        r.Read<ActuatorHistoryInput, ActuatorHistory>("get_actuator_history", Category, "Recent runs (incl. offline schedule runs), commands (incl. blocked ones with reasons) and the effective safety limits of an actuator.",
            async (ctx, i) =>
            {
                var a = Ensure.Found(await ctx.Db.Actuators.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.ActuatorId, ctx.Ct), "Actuator");
                var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(i.Hours ?? 48, 1, 24 * 90));
                var runs = await ctx.Db.ActuatorRuns.AsNoTracking().Where(x => x.ActuatorId == a.Id && x.StartedAt >= since).OrderByDescending(x => x.StartedAt).Take(200).ToListAsync(ctx.Ct);
                var cmds = await ctx.Db.ActuatorCommands.AsNoTracking().Where(x => x.ActuatorId == a.Id && x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ctx.Ct);
                var tz = await ctx.Get<SafetyService>().GardenTimeZoneAsync(a.GardenId, ctx.Ct);
                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
                var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
                var today = await ctx.Db.ActuatorRuns.Where(x => x.ActuatorId == a.Id && x.EndedAt >= dayStart).SumAsync(x => x.DurationSec, ctx.Ct);
                return new ActuatorHistory(runs, cmds, today, await ctx.Get<SafetyService>().ResolveAsync(a, ctx.Ct));
            });
    }

    // ------------------------------------------------------------------ cameras
    private static void RegisterCameras(ToolRegistry r)
    {
        r.Read<ListComponentsInput, List<CameraDto>>("list_cameras", Category, "List cameras and what they look at.",
            async (ctx, i) =>
            {
                var q = ctx.Db.Cameras.AsNoTracking().Include(c => c.Device).AsQueryable();
                if (i.GardenId is { } g) q = q.Where(c => c.GardenId == g);
                if (i.DeviceId is { } d) q = q.Where(c => c.DeviceId == d);
                if (i.BedId is { } b) q = q.Where(c => c.BedId == b);
                if (i.PlantId is { } p) q = q.Where(c => c.PlantId == p);
                if (i.ReservoirId is { } r) q = q.Where(c => c.ReservoirId == r);
                return (await q.OrderBy(c => c.Name).ToListAsync(ctx.Ct)).Select(CameraDto.From).ToList();
            });

        r.Mutate<CreateCameraInput, CameraDto>("create_camera", Category, "Configure a camera on a device for a plant, bed or the whole garden.",
            async (ctx, i) =>
            {
                var device = await ApprovedDeviceAsync(ctx, i.DeviceId);
                var target = await ResolveTargetAsync(ctx, i.TargetType, i.GardenId, i.BedId, i.PlantId, device.GardenId, i.ReservoirId);
                var c = new Camera { DeviceId = device.Id, Name = i.Name, PluginId = i.PluginId, Settings = i.Settings ?? new JsonObject() };
                ApplyTarget(c, target);
                Patch.Apply(c, i, nameof(i.DeviceId), nameof(i.Settings), nameof(i.TargetType), nameof(i.GardenId), nameof(i.BedId), nameof(i.PlantId), nameof(i.ReservoirId));
                ctx.Db.Cameras.Add(c);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(device.Id);
                await Changed(ctx, "cameras");
                return CameraDto.From(c);
            },
            async (ctx, i) => new ProposalPreview($"Add camera '{i.Name}' for {await TargetLabelAsync(ctx, i.BedId, i.PlantId, i.ReservoirId)}", i.TargetType, i.PlantId ?? i.BedId ?? i.ReservoirId ?? i.GardenId, i.GardenId, "low", Patch.CreateChanges(i)));

        r.Mutate<UpdateCameraInput, CameraDto>("update_camera", Category, "Change camera name, settings, capture resolution, frame interval or target.",
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.Cameras.FirstOrDefaultAsync(x => x.Id == i.CameraId, ctx.Ct), "Camera");
                if (i.TargetType is not null || i.BedId is not null || i.PlantId is not null || i.GardenId is not null || i.ReservoirId is not null)
                    ApplyTarget(c, await ResolveTargetAsync(ctx, i.TargetType ?? c.TargetType, i.GardenId ?? c.GardenId, i.BedId ?? c.BedId, i.PlantId ?? c.PlantId, null, i.ReservoirId ?? c.ReservoirId));
                Patch.Apply(c, i, nameof(i.CameraId), nameof(i.TargetType), nameof(i.GardenId), nameof(i.BedId), nameof(i.PlantId), nameof(i.ReservoirId));
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(c.DeviceId);
                await Changed(ctx, "cameras");
                return CameraDto.From(c);
            },
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.Cameras.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.CameraId, ctx.Ct), "Camera");
                return new ProposalPreview($"Update camera '{c.Name}'", "camera", c.Id, c.GardenId, "low", Patch.UpdateChanges(c, i, nameof(i.CameraId)));
            });

        r.Mutate<CameraIdInput, bool>("delete_camera", Category, "Delete a camera.",
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.Cameras.FirstOrDefaultAsync(x => x.Id == i.CameraId, ctx.Ct), "Camera");
                ctx.Db.Cameras.Remove(c);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Get<ConfigPushQueue>().Dirty(c.DeviceId);
                await Changed(ctx, "cameras");
                return true;
            },
            async (ctx, i) =>
            {
                var c = Ensure.Found(await ctx.Db.Cameras.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.CameraId, ctx.Ct), "Camera");
                return new ProposalPreview($"Delete camera '{c.Name}'", "camera", c.Id, c.GardenId, "medium", [Patch.Change("camera", c.Name, null)]);
            });

        r.Read<CaptureInput, CaptureResult>("capture_camera_image", Category,
            "Capture a still image from a camera right now (the AI never gets continuous video). The image is attached for vision-capable models and saved to media.",
            async (ctx, i) =>
            {
                var cam = Ensure.Found(await ctx.Db.Cameras.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.CameraId, ctx.Ct), "Camera");
                var store = ctx.Get<CameraFrameStore>();
                var width = Math.Clamp(i.Width ?? 1024, 160, 4096);
                StoredFrame? frame = null;
                if (ctx.Get<DeviceConnections>().Get(cam.DeviceId) is { } conn)
                {
                    var requestId = Guid.NewGuid();
                    var wait = store.WaitForAsync(requestId, TimeSpan.FromSeconds(15), ctx.Ct);
                    await ctx.Get<IHubContext<DeviceHub>>().Clients.Client(conn).SendAsync(DeviceHubMethods.CaptureFrame, new CaptureFrameRequest(requestId, cam.Id, width, null), ctx.Ct);
                    try
                    {
                        frame = await wait;
                    }
                    catch (TaskCanceledException)
                    {
                        frame = null;
                    }
                }
                var note = "Fresh capture";
                if (frame is null)
                {
                    frame = store.Latest(cam.Id);
                    note = frame is null ? "" : $"Device did not answer; using last frame from {frame.CapturedAt:u}";
                }
                if (frame is null) throw AppException.Bad("Camera has no frame yet and the device is offline");
                if (frame.Width > width) frame = CameraFrameStore.Resize(frame, width);
                var media = new Media
                {
                    Kind = "ai_capture", CameraId = cam.Id, GardenId = cam.GardenId, BedId = cam.BedId, PlantId = cam.PlantId, ContentType = frame.ContentType,
                    Width = frame.Width, Height = frame.Height, SizeBytes = frame.Data.Length, Data = frame.Data, CapturedAt = frame.CapturedAt,
                };
                ctx.Db.Media.Add(media);
                await ctx.Db.SaveChangesAsync(ctx.Ct);
                ctx.Images.Add($"data:{frame.ContentType};base64,{Convert.ToBase64String(frame.Data)}");
                return new CaptureResult(media.Id, $"/api/media/{media.Id}", frame.Width, frame.Height, frame.CapturedAt, note);
            });
    }
}
