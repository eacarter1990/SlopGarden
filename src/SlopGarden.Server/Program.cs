using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Contracts;
using SlopGarden.Server.Ai;
using SlopGarden.Server.Ai.Tools;
using SlopGarden.Server.Background;
using SlopGarden.Server.Data;
using SlopGarden.Server.Endpoints;
using SlopGarden.Server.Hubs;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Mcp;
using SlopGarden.Server.Services;

var builder = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()
    ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory })
    : WebApplication.CreateBuilder(args);
MachineHost.Configure(builder.Host);

var dataDir = ServerOptions.ResolveDataDirectory(builder.Configuration);
Directory.CreateDirectory(dataDir);
var overridesPath = ServerOptions.OverridesFile(dataDir);
// Environment supplies the bootstrap connection (Compose). The overrides file — written by the first-run
// wizard and by Settings → Server & database — wins, so a saved remote database is actually used.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddJsonFile(overridesPath, optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
builder.Services.PostConfigure<ServerOptions>(o => o.DataDirectory = dataDir);
builder.Services.AddSingleton(new ServerOverridesFile(overridesPath));

var dbOptions = builder.Configuration.GetSection($"{ServerOptions.Section}:Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
var connectionString = builder.Configuration.GetConnectionString("SlopGarden") is { Length: > 0 } cs ? cs : dbOptions.ToConnectionString();
builder.Services.AddDbContext<SlopDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddDataProtection()
    .SetApplicationName("SlopGarden")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddAuthentication(AuthSchemes.Smart)
    .AddPolicyScheme(AuthSchemes.Smart, "Cookie or bearer token", o =>
        o.ForwardDefaultSelector = ctx => TokenAuthHandler.ExtractToken(ctx.Request) is not null ? AuthSchemes.Token : AuthSchemes.Cookie)
    .AddCookie(AuthSchemes.Cookie, o =>
    {
        o.Cookie.Name = "slopgarden.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(AuthSchemes.Token, null);

static bool HasRole(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext ctx, string min, bool needsWrite)
{
    var actor = Actor.FromPrincipal(ctx.User);
    if (actor.Type == ActorTypes.Device || ctx.User.Identity?.IsAuthenticated != true) return false;
    if (!Roles.AtLeast(actor.Role, min)) return false;
    return !needsWrite || actor.Type != ActorTypes.Pat || actor.Scopes.Contains(PatScopes.Write);
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.ReadOnly, p => p.RequireAssertion(ctx => HasRole(ctx, Roles.ReadOnly, false)))
    .AddPolicy(Policies.ReadWrite, p => p.RequireAssertion(ctx => HasRole(ctx, Roles.ReadWrite, true)))
    .AddPolicy(Policies.Admin, p => p.RequireAssertion(ctx => HasRole(ctx, Roles.Admin, true)))
    .AddPolicy(Policies.Device, p => p.RequireClaim(Claims.Kind, ActorTypes.Device));

builder.Services.ConfigureHttpJsonOptions(o => SlopJson.Configure(o.SerializerOptions));
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 1024 * 1024)
    .AddJsonProtocol(o => SlopJson.Configure(o.PayloadSerializerOptions));
builder.Services.AddOpenApi();
builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("discord", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("search");
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SlopGarden");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});

builder.Services.AddSingleton<DeviceConnections>();
builder.Services.AddSingleton<ConfigPushQueue>();
builder.Services.AddSingleton<CameraFrameStore>();
builder.Services.AddSingleton<LiveNotifier>();
builder.Services.AddSingleton<AiActivityTracker>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<EnvironmentService>();
builder.Services.AddSingleton<ServerUrlProvider>();
builder.Services.AddSingleton(_ =>
{
    var r = new ToolRegistry();
    GardenTools.Register(r);
    DeviceTools.Register(r);
    AutomationTools.Register(r);
    MiscTools.Register(r);
    ReservoirTools.Register(r);
    return r;
});
builder.Services.AddSingleton<OpenAiClient>();
builder.Services.AddScoped<ToolExecutor>();
builder.Services.AddScoped<SecretStore>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<WebSearchService>();
builder.Services.AddSingleton<WebSearchExecutor>();
builder.Services.AddScoped<SafetyService>();
builder.Services.AddScoped<DeviceConfigService>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddScoped<DeviceRegistry>();
builder.Services.AddScoped<OverviewService>();
builder.Services.AddScoped<ReadingsService>();
builder.Services.AddScoped<EmergencyStopService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<INotificationSender, BrowserNotificationSender>();
builder.Services.AddScoped<INotificationSender, DiscordNotificationSender>();
builder.Services.AddScoped<Seeder>();
builder.Services.AddHostedService<ConfigPushWorker>();
builder.Services.AddHostedService<UdpDiscoveryService>();
builder.Services.AddHostedService<ServerScheduler>();
builder.Services.AddHostedService<HousekeepingService>();
builder.Services.AddSingleton<HydroWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HydroWatchdog>());
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddSingleton<ReleaseCatalog>();
builder.Services.AddSingleton<ServerImageUpdater>();
builder.Services.AddSingleton<ClientUpdateService>();
builder.Services.AddSingleton<RemoteSetupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientUpdateService>());
builder.Services.AddHostedService<MdnsDiscoveryService>();
builder.Logging.AddProvider(new FileLogProvider(Path.Combine(dataDir, "logs", "server.log")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync(CancellationToken.None);
}

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (AppException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapGet("/api/timezones", () => TimeZones.Options).RequireAuthorization(Policies.ReadOnly).WithTags("Garden");
CoreEndpoints.Map(app);
AdminEndpoints.Map(app);
MilestoneEndpoints.Map(app);
RemoteSetupEndpoints.Map(app);
McpEndpoint.Map(app);
app.MapHub<LiveHub>("/hubs/live");
app.MapHub<DeviceHub>("/hubs/device");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
