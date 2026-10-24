using Microsoft.Extensions.Options;
using SlopGarden.Client.Actuators;
using SlopGarden.Client.Cameras;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Logging;
using SlopGarden.Client.Plugins;
using SlopGarden.Client.Runtime;
using SlopGarden.Client.Scheduling;
using SlopGarden.Client.Sensors;
using SlopGarden.Client.Server;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Updates;

namespace SlopGarden.Client;

public static class ClientHost
{
    public const string EnvironmentPrefix = "SLOPGARDEN_";
    public const string ServiceName = "SlopGarden Client";

    public static HostApplicationBuilder CreateBuilder(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        AddConfigurationSources(builder.Configuration, args);
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        AddClientServices(builder.Services, builder.Configuration);
        configure?.Invoke(builder);
        return builder;
    }

    /// <summary>appsettings.json &lt; /etc/slopgarden/client.json &lt; SLOPGARDEN_* env vars &lt; command line.</summary>
    public static void AddConfigurationSources(IConfigurationBuilder configuration, string[] args)
    {
        foreach (var file in SystemConfigFiles()) configuration.AddJsonFile(file, optional: true, reloadOnChange: false);
        configuration.AddEnvironmentVariables(EnvironmentPrefix);
        configuration.AddCommandLine(args);
    }

    public static IEnumerable<string> SystemConfigFiles()
    {
        var configDir = Environment.GetEnvironmentVariable("CONFIGURATION_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configDir)) yield return Path.Combine(configDir.Split(':')[0], "client.json");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) yield return "/etc/slopgarden/client.json";
        if (OperatingSystem.IsMacOS()) yield return "/Library/Application Support/SlopGarden/client.json";
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SlopGarden", "client.json");
    }

    public static void AddClientServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClientOptions>(configuration.GetSection(ClientOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => ClientPaths.Resolve(sp.GetRequiredService<IOptions<ClientOptions>>().Value));
        services.AddSingleton<IIdentityStore, IdentityStore>();
        services.AddSingleton(sp => new ClientDatabase(sp.GetRequiredService<ClientPaths>().DatabaseFile));
        services.AddSingleton<Outbox>();
        services.AddSingleton<IRunLedger, SqliteRunLedger>();
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<IChannelValues, ChannelValueStore>();
        services.AddSingleton<ServerLink>();
        services.AddSingleton<IServerLink>(sp => sp.GetRequiredService<ServerLink>());
        services.AddSingleton<ServerHttp>();
        services.AddSingleton<IDeviceReporter, DeviceReporter>();
        services.AddSingleton<SafetyGuard>();
        services.AddSingleton<ActuatorController>();
        services.AddSingleton<SensorManager>();
        services.AddSingleton<CameraManager>();
        services.AddSingleton<LocalScheduler>();
        services.AddSingleton<ActuatorSupervisor>();
        services.AddSingleton<DeviceRuntime>();
        services.AddSingleton<HardwareDiscoveryRunner>();
        services.AddSingleton<ServerDiscovery>();
        services.AddSingleton<MdnsAgent>();
        services.AddSingleton<RegistrationClient>();
        services.AddSingleton<ContainerUpdater>();
        services.AddSingleton<DeviceConnection>();
        services.AddSingleton<OutboxUploader>();
        services.AddSingleton<LocalApiServer>();
        services.AddSingleton<ClientLogFile>();
        services.AddSingleton<ILoggerProvider, ClientFileLogProvider>();
        services.AddHostedService<ClientLogShipper>();
        services.AddHostedService(sp => sp.GetRequiredService<MdnsAgent>());

        // Start order matters: cached config (and failsafe states) first; stop runs in reverse, so actuators
        // are switched to their failsafe state after the connection and uploader have stopped.
        services.AddHostedService(sp => sp.GetRequiredService<DeviceRuntime>());
        services.AddHostedService(sp => sp.GetRequiredService<LocalScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<ActuatorSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<OutboxUploader>());
        services.AddHostedService(sp => sp.GetRequiredService<DeviceConnection>());
        services.AddHostedService(sp => sp.GetRequiredService<LocalApiServer>());
    }
}
