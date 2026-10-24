using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SlopGarden.Server.Services;

/// <summary>
/// Replaces the server container with the image from a chosen release. The host must mount the Docker socket
/// and set SLOPGARDEN_CONTAINER to this server's container name. A short-lived client-image helper does the swap.
/// </summary>
public sealed class ServerImageUpdater(ILogger<ServerImageUpdater> log)
{
    public static string? ContainerName => Environment.GetEnvironmentVariable("SLOPGARDEN_CONTAINER");
    public static string SocketPath =>
        Environment.GetEnvironmentVariable("SLOPGARDEN_DOCKER_SOCKET") is { Length: > 0 } path ? path : "/var/run/docker.sock";
    public static bool CanUpdate => !string.IsNullOrWhiteSpace(ContainerName) && File.Exists(SocketPath);

    public static string RunningVersion =>
        Environment.GetEnvironmentVariable("SLOPGARDEN_RELEASE") is { Length: > 0 } version
            ? version
            : typeof(ServerImageUpdater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task ApplyAsync(string serverImage, string helperImage, CancellationToken ct)
    {
        if (!CanUpdate)
            throw new InvalidOperationException("This server cannot update itself. Mount /var/run/docker.sock and set SLOPGARDEN_CONTAINER to the server container name.");
        using var docker = Connect();
        await PullAsync(docker, serverImage, ct);
        await PullAsync(docker, helperImage, ct);
        var create = new JsonObject
        {
            ["Image"] = helperImage,
            ["Cmd"] = new JsonArray("update-helper", ContainerName!, serverImage),
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray($"{SocketPath}:{SocketPath}"),
                ["AutoRemove"] = true,
            },
        };
        using var created = await docker.PostAsync($"/v1.41/containers/create?name=slopgarden-server-update-{Guid.NewGuid():N}"[..40],
            new StringContent(create.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var createdBody = await created.Content.ReadAsStringAsync(ct);
        if (!created.IsSuccessStatusCode) throw new InvalidOperationException("Could not start the server updater: " + Trim(createdBody));
        var id = JsonNode.Parse(createdBody)?["Id"]?.ToString() ?? throw new InvalidOperationException("Docker did not return an updater id.");
        using var start = await docker.PostAsync($"/v1.41/containers/{id}/start", new StringContent(""), ct);
        if (!start.IsSuccessStatusCode)
        {
            var err = await start.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException("Could not start the server updater: " + Trim(err));
        }
        log.LogInformation("Server update helper started for {Image}", serverImage);
    }

    private static async Task PullAsync(HttpClient docker, string image, CancellationToken ct)
    {
        var (name, tag) = Split(image);
        using var res = await docker.PostAsync($"/v1.41/images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}", new StringContent(""), ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"docker pull failed for {image}: {Trim(body)}");
        }
    }

    private static HttpClient Connect()
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
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromMinutes(10) };
    }

    private static (string Name, string Tag) Split(string image)
    {
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        if (colon > slash) return (image[..colon], image[(colon + 1)..]);
        return (image, "latest");
    }

    private static string Trim(string text) => text.Length > 400 ? text[..400] : text;
}
