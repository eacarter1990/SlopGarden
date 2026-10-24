using Microsoft.AspNetCore.Http.HttpResults;
using SlopGarden.Emulator.Imaging;
using SlopGarden.Emulator.Simulation;

namespace SlopGarden.Emulator;

public sealed record OverrideRequest(double Value, double? DurationSec);
public sealed record ActuatorStateRequest(bool On, double? Level, string? Source);
public sealed record TimeScaleRequest(double Factor);

public static class EmulatorApp
{
    public const string DefaultUrl = "http://0.0.0.0:5090";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = ResolveContentRoot(),
        });
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["Urls"]))
            builder.WebHost.UseUrls(DefaultUrl);

        builder.Services.Configure<EmulatorOptions>(builder.Configuration.GetSection("Emulator"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp =>
        {
            var file = builder.Configuration["Emulator:ScenarioFile"];
            var logger = sp.GetRequiredService<ILogger<GardenSimulator>>();
            ScenarioDocument? scenario = null;
            try
            {
                scenario = ScenarioFile.TryLoad(file);
                if (scenario is not null) logger.LogInformation("Loaded scenario {Scenario} from {ScenarioFile}", scenario.Name, file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scenario file {ScenarioFile} is invalid; using the default scenario", file);
            }
            return new GardenSimulator(scenario ?? DefaultScenario.Create(), sp.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton<SnapshotRenderer>();
        builder.Services.AddHostedService<SimulationLoop>();
        builder.Services.AddOpenApi();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapOpenApi();
        MapApi(app);
        return app;
    }

    private static string ResolveContentRoot()
    {
        var cwd = Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(cwd, "wwwroot")) ? cwd : AppContext.BaseDirectory;
    }

    private static void MapApi(WebApplication app)
    {
        var sensors = app.MapGroup("/api/sensors").WithTags("Sensors");
        sensors.MapGet("/", (GardenSimulator sim) => sim.ListSensors());
        sensors.MapGet("/{id}", (string id, GardenSimulator sim) => Try(() => sim.GetSensorView(id)));
        sensors.MapGet("/{id}/value", (string id, GardenSimulator sim) => Try(() => sim.ReadValue(id)));
        sensors.MapPost("/", (SensorDefinition definition, GardenSimulator sim) =>
            Try(() => sim.CreateSensor(definition), v => TypedResults.Created($"/api/sensors/{v.Id}", v)));
        sensors.MapPut("/{id}", (string id, SensorUpdate update, GardenSimulator sim) => Try(() => sim.UpdateSensor(id, update)));
        sensors.MapPost("/{id}/override", (string id, OverrideRequest req, GardenSimulator sim) => Try(() => sim.SetOverride(id, req.Value, req.DurationSec)));
        sensors.MapDelete("/{id}/override", (string id, GardenSimulator sim) => Try(() => sim.ClearOverride(id)));
        sensors.MapDelete("/{id}", (string id, GardenSimulator sim) => Try(() => { sim.DeleteSensor(id); return true; }, _ => TypedResults.NoContent()));

        var actuators = app.MapGroup("/api/actuators").WithTags("Actuators");
        actuators.MapGet("/", (GardenSimulator sim) => sim.ListActuators());
        actuators.MapGet("/{id}", (string id, GardenSimulator sim) => Try(() => sim.GetActuator(id)));
        actuators.MapPut("/{id}/state", (string id, ActuatorStateRequest req, GardenSimulator sim) =>
            Try(() => sim.SetActuatorState(id, req.On, req.Level, req.Source)));

        var cameras = app.MapGroup("/api/cameras").WithTags("Cameras");
        cameras.MapGet("/", (GardenSimulator sim) => sim.ListCameras());
        cameras.MapGet("/{id}", (string id, GardenSimulator sim) => Try(() => sim.GetCamera(id)));
        cameras.MapGet("/{id}/snapshot", Results<FileContentHttpResult, NotFound<ProblemDetailsLite>> (string id, int? width, int? height, GardenSimulator sim, SnapshotRenderer renderer) =>
        {
            CameraDefinition camera;
            byte[]? custom;
            try
            {
                (camera, custom) = sim.GetCameraSource(id);
            }
            catch (KeyNotFoundException ex)
            {
                return TypedResults.NotFound(new ProblemDetailsLite(ex.Message));
            }
            var local = TimeZoneInfo.ConvertTime(sim.SimulatedTime, TimeZoneInfo.Local);
            var snap = renderer.Render(camera, custom, width, height, local);
            return TypedResults.File(snap.Data, snap.ContentType);
        });
        cameras.MapPut("/{id}/image", async (string id, HttpRequest request, GardenSimulator sim, SnapshotRenderer renderer) =>
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (!renderer.IsValidImage(bytes)) return Results.BadRequest(new ProblemDetailsLite("Body must be a JPEG, PNG or WebP image."));
            return Try(() => { sim.SetCameraImage(id, bytes); return sim.GetCamera(id); });
        }).Accepts<byte[]>("image/jpeg", "image/png", "image/webp");

        var scenario = app.MapGroup("/api/scenario").WithTags("Scenario");
        scenario.MapGet("/", (GardenSimulator sim) => sim.View());
        scenario.MapGet("/export", (GardenSimulator sim) => sim.Export());
        scenario.MapPut("/", (ScenarioDocument doc, GardenSimulator sim) => { sim.Load(doc); return sim.View(); });
        scenario.MapPost("/reset", (GardenSimulator sim) => { sim.Load(DefaultScenario.Create()); return sim.View(); });
        scenario.MapPost("/time-scale", (TimeScaleRequest req, GardenSimulator sim) => Try(() => { sim.SetTimeScale(req.Factor); return new { timeScale = sim.TimeScale }; }));
    }

    public sealed record ProblemDetailsLite(string Error);

    private static IResult Try<T>(Func<T> action, Func<T, IResult>? onSuccess = null)
    {
        try
        {
            var value = action();
            return onSuccess is null ? TypedResults.Ok(value) : onSuccess(value);
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ProblemDetailsLite(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ProblemDetailsLite(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(new ProblemDetailsLite(ex.Message));
        }
    }
}
