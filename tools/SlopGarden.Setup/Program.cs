using System.Text.Json;
using SlopGarden.Contracts;
using SlopGarden.Setup;

// JSON helper used by the SlopGarden Setup desktop app. People download that app; this process stays behind it.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
try
{
    object result = command switch
    {
        "docker" => new { docker = Docker.Available() },
        "discover" => await DiscoverAsync(),
        "server" => await ServerAsync(args),
        "client" => await ClientAsync(args),
        _ => throw new ArgumentException("Commands: docker, discover, server, client"),
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
