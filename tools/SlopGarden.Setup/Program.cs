using System.Text.Json;
using SlopGarden.Contracts;
using SlopGarden.Setup;

// Command-line installer helper. The downloadable server installers are the MSI, DMG, and shell script.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
try
{
    object result = command switch
    {
        "docker" => new { docker = Docker.Available() },
        "discover" => await DiscoverAsync(),
        "server" => await InstallServiceAsync(ServiceRole.Server, args),
        "client" => await InstallServiceAsync(ServiceRole.Client, args),
        "remove" => await RemoveServiceAsync(args),
        "server-docker" => await ServerAsync(args),
        "client-docker" => await ClientAsync(args),
        _ => throw new ArgumentException("Commands: docker, discover, server, client, remove, server-docker, client-docker"),
    };
    Console.WriteLine(JsonSerializer.Serialize(result, json));
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, json));
    Environment.ExitCode = 1;
}

static async Task<object> DiscoverAsync()
{
    var url = await LanServerSearch.FindAsync(LanServerSearch.DefaultTimeout, CancellationToken.None);
    return new { found = url is not null, serverUrl = url };
}

static async Task<object> InstallServiceAsync(ServiceRole role, string[] args)
{
    var platform = ServiceInstaller.CurrentPlatform;
    var dir = DirectoryFor(Arg(args, "--directory"));
    var serverUrl = Arg(args, "--server-url");
    if (role == ServiceRole.Client && string.IsNullOrWhiteSpace(Arg(args, "--name")))
        throw new ArgumentException("A display name is required.");
    if (role == ServiceRole.Client && string.IsNullOrWhiteSpace(serverUrl))
        serverUrl = await LanServerSearch.FindAsync(LanServerSearch.DefaultTimeout, CancellationToken.None);
    var password = Arg(args, "--password");
    var generated = false;
    if (role == ServiceRole.Server && string.IsNullOrWhiteSpace(password))
    {
        password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        generated = true;
    }
    var plan = MachineServices.Install(role, platform, Executable(role, platform, dir), serverUrl, Arg(args, "--name"), Arg(args, "--public-url"), password ?? "");
    var log = await ServiceInstaller.InstallAsync(plan, CancellationToken.None);
    return new
    {
        directory = dir,
        service = plan.Name,
        manager = plan.Manager,
        url = role == ServiceRole.Server ? "http://127.0.0.1:5080" : null,
        serverUrl,
        databasePassword = generated ? password : null,
        log,
        started = true,
        pending = role == ServiceRole.Client,
    };
}

static async Task<object> RemoveServiceAsync(string[] args)
{
    var role = (args.ElementAtOrDefault(1) ?? Arg(args, "--role"))?.ToLowerInvariant() switch
    {
        "server" => ServiceRole.Server,
        "client" => ServiceRole.Client,
        _ => throw new ArgumentException("remove needs server or client."),
    };
    var platform = ServiceInstaller.CurrentPlatform;
    var dir = DirectoryFor(Arg(args, "--directory"));
    var plan = MachineServices.Install(role, platform, Executable(role, platform, dir), role == ServiceRole.Client ? "http://127.0.0.1:5080" : null, null, null, "");
    var log = await ServiceInstaller.RemoveAsync(plan, CancellationToken.None);
    return new { service = plan.Name, manager = plan.Manager, log, removed = true };
}

static string Executable(ServiceRole role, ServicePlatform platform, string directory)
{
    var name = role == ServiceRole.Server
        ? platform == ServicePlatform.Windows ? "SlopGarden.Server.exe" : "SlopGarden.Server"
        : platform == ServicePlatform.Windows ? "slopgarden-client.exe" : "slopgarden-client";
    return Path.Combine(directory, name);
}

static async Task<object> ServerAsync(string[] args)
{
    var compose = MachineSetup.ServerCompose(Arg(args, "--password") ?? "", Arg(args, "--public-url"));
    var dir = DirectoryFor(Arg(args, "--directory"));
    var file = Path.Combine(dir, "docker-compose.yml");
    Docker.Require();
    await File.WriteAllTextAsync(file, compose);
    var log = await Docker.ComposeUpAsync(dir, CancellationToken.None);
    return new { directory = dir, url = "http://127.0.0.1:5080", log, started = true };
}

static async Task<object> ClientAsync(string[] args)
{
    var serverUrl = Arg(args, "--server-url");
    if (string.IsNullOrWhiteSpace(serverUrl))
        serverUrl = await LanServerSearch.FindAsync(LanServerSearch.DefaultTimeout, CancellationToken.None);
    if (string.IsNullOrWhiteSpace(serverUrl))
        throw new InvalidOperationException("No SlopGarden server was found on this network. Enter its address.");
    var name = Arg(args, "--name") ?? throw new ArgumentException("A display name is required.");
    var compose = MachineSetup.ClientCompose(serverUrl, name, null);
    var dir = DirectoryFor(Arg(args, "--directory"));
    var file = Path.Combine(dir, "docker-compose.yml");
    Docker.Require();
    await File.WriteAllTextAsync(file, compose);
    var log = await Docker.ComposeUpAsync(dir, CancellationToken.None);
    return new { directory = dir, serverUrl, log, started = true, pending = true };
}

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string DirectoryFor(string? requested)
{
    var dir = string.IsNullOrWhiteSpace(requested)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "slopgarden")
        : requested;
    Directory.CreateDirectory(dir);
    return Path.GetFullPath(dir);
}
