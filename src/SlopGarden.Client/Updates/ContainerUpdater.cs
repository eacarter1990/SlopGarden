using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Updates;

/// <summary>
/// Replaces this process when it is a container. The host must mount the Docker socket and set
/// <c>SLOPGARDEN_CONTAINER</c> to this container's name. No shell on the device is required:
/// the device list sends <see cref="ApplyUpdateCommand"/> and a short-lived helper container does the swap.
/// </summary>
public sealed class ContainerUpdater(IServerLink link, ILogger<ContainerUpdater> logger)
{
    public async Task ApplyAsync(ApplyUpdateCommand command, CancellationToken ct)
    {
        if (!DockerEngine.CanUpdate)
        {
            const string message = "This client cannot update itself. Mount /var/run/docker.sock and set SLOPGARDEN_CONTAINER to the container name (see the README).";
            logger.LogWarning(message);
            await ReportAsync(command.Version, "failed", message, ct);
            return;
        }
        try
        {
            using var docker = DockerEngine.Connect();
            await docker.PullAsync(command.Image, ct);
            await docker.StartHelperAsync(command.Image, DockerEngine.ContainerName!, ct);
            logger.LogInformation("Update helper started for {Image}", command.Image);
            await ReportAsync(command.Version, "updating", null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Container update failed");
            await ReportAsync(command.Version, "failed", ex.Message, ct);
        }
    }

    private async Task ReportAsync(string version, string status, string? error, CancellationToken ct)
    {
        try
        {
            if (link.IsConnected) await link.InvokeAsync(DeviceHubMethods.ReportUpdate, new UpdateResultReport(version, status, error), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not report update status");
        }
    }
}

public static class ContainerSpec
{
    /// <summary>Docker create body that recreates <paramref name="inspect"/> (GET /containers/{id}/json) with a new image.</summary>
    public static JsonObject Replacement(JsonObject inspect, string image)
    {
        var config = inspect["Config"]?.AsObject();
        var host = inspect["HostConfig"]?.AsObject()?.DeepClone()?.AsObject() ?? new JsonObject();
        host.Remove("ContainerIDFile");
        var body = new JsonObject { ["Image"] = image, ["HostConfig"] = host };
        Copy(body, "Env", config?["Env"]);
        Copy(body, "Cmd", config?["Cmd"]);
        Copy(body, "Entrypoint", config?["Entrypoint"]);
        Copy(body, "WorkingDir", config?["WorkingDir"]);
        Copy(body, "User", config?["User"]);
        Copy(body, "Labels", config?["Labels"]);
        if (inspect["NetworkSettings"]?["Networks"] is JsonObject networks && networks.Count > 0)
        {
            var endpoints = new JsonObject();
            foreach (var (name, node) in networks)
            {
                endpoints[name] = new JsonObject
                {
                    ["IPAMConfig"] = node?["IPAMConfig"]?.DeepClone(),
                    ["Aliases"] = node?["Aliases"]?.DeepClone(),
                };
            }
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        }
        return body;
    }

    private static void Copy(JsonObject body, string name, JsonNode? value)
    {
        if (value is null || value.ToJsonString() is "null" or "\"\"") return;
        body[name] = value.DeepClone();
    }
}

public sealed class DockerEngine : IDisposable
{
    public const string Api = "/v1.41";
    private readonly HttpClient _http;

    private DockerEngine(HttpClient http) => _http = http;

    public static string SocketPath =>
        Environment.GetEnvironmentVariable("SLOPGARDEN_DOCKER_SOCKET") is { Length: > 0 } path ? path : "/var/run/docker.sock";

    public static string? ContainerName => Environment.GetEnvironmentVariable("SLOPGARDEN_CONTAINER");

    public static bool CanUpdate => !string.IsNullOrWhiteSpace(ContainerName) && File.Exists(SocketPath);

    public static DockerEngine Connect()
    {
        var socket = SocketPath;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await unix.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                return new NetworkStream(unix, ownsSocket: true);
            },
        };
        return new DockerEngine(new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromMinutes(10) });
    }

    public async Task PullAsync(string image, CancellationToken ct)
    {
        var (name, tag) = Split(image);
        using var res = await _http.PostAsync($"{Api}/images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}", new StringContent(""), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"docker pull failed ({(int)res.StatusCode}): {Trim(body)}");
    }

    public async Task StartHelperAsync(string image, string containerName, CancellationToken ct)
    {
        var create = new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray("update-helper", containerName, image),
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray($"{SocketPath}:{SocketPath}"),
                ["AutoRemove"] = true,
            },
        };
        var id = await CreateAsync($"slopgarden-updater-{Guid.NewGuid():N}"[..28], create, ct);
        using var start = await _http.PostAsync($"{Api}/containers/{id}/start", new StringContent(""), ct);
        if (!start.IsSuccessStatusCode)
        {
            var err = await start.Content.ReadAsStringAsync(ct);
            throw new IOException($"Could not start the updater ({(int)start.StatusCode}): {Trim(err)}");
        }
    }

    public async Task<JsonObject> InspectAsync(string name, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/json", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"docker inspect failed ({(int)res.StatusCode}): {Trim(body)}");
        return JsonNode.Parse(body)?.AsObject() ?? throw new IOException("docker inspect returned no JSON");
    }

    public async Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"{Api}/containers/create?name={Uri.EscapeDataString(name)}",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new IOException($"docker create failed ({(int)res.StatusCode}): {Trim(text)}");
        return JsonNode.Parse(text)?["Id"]?.ToString() ?? throw new IOException("docker create returned no id");
    }

    public async Task StopRenameStartAsync(string name, string image, CancellationToken ct)
    {
        var inspect = await InspectAsync(name, ct);
        var replacement = ContainerSpec.Replacement(inspect, image);
        var previous = name + "-previous";
        using var _ = await _http.DeleteAsync($"{Api}/containers/{Uri.EscapeDataString(previous)}?force=true", ct);
        using var stop = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/stop?t=8", new StringContent(""), ct);
        if (!stop.IsSuccessStatusCode && stop.StatusCode != HttpStatusCode.NotModified)
            throw new IOException($"docker stop failed ({(int)stop.StatusCode})");
        using var rename = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/rename?name={Uri.EscapeDataString(previous)}", new StringContent(""), ct);
        if (!rename.IsSuccessStatusCode) throw new IOException($"docker rename failed ({(int)rename.StatusCode})");
        try
        {
            var id = await CreateAsync(name, replacement, ct);
            using var start = await _http.PostAsync($"{Api}/containers/{id}/start", new StringContent(""), ct);
            if (!start.IsSuccessStatusCode) throw new IOException($"docker start failed ({(int)start.StatusCode})");
        }
        catch
        {
            using var back = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(previous)}/rename?name={Uri.EscapeDataString(name)}", new StringContent(""), ct);
            using var restart = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/start", new StringContent(""), ct);
            throw;
        }
    }

    public static (string Name, string Tag) Split(string image)
    {
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        if (colon > slash) return (image[..colon], image[(colon + 1)..]);
        return (image, "latest");
    }

    public void Dispose() => _http.Dispose();

    private static string Trim(string text) => text.Length > 400 ? text[..400] : text;
}

public static class UpdateHelper
{
    public static async Task<int> RunAsync(string[] args)
    {
        var name = args.ElementAtOrDefault(0);
        var image = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(image))
        {
            Console.Error.WriteLine("usage: slopgarden-client update-helper <container-name> <image>");
            return 2;
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        using var docker = DockerEngine.Connect();
        await docker.StopRenameStartAsync(name, image, CancellationToken.None);
        return 0;
    }
}
