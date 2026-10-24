using Microsoft.AspNetCore.SignalR.Client;
using SlopGarden.Client.Identity;

namespace SlopGarden.Client.Server;

public static class ClientStates
{
    public const string Starting = "starting";
    public const string Discovering = "discovering";
    public const string Registering = "registering";
    public const string PendingApproval = "pending_approval";
    public const string Rejected = "rejected";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Reconnecting = "reconnecting";
}

public static class DiscoveredVia
{
    public const string Broadcast = "broadcast";
    public const string ManualIp = "manual_ip";
    public const string Configured = "configured";
}

/// <summary>The live channel to the server, as seen by the rest of the client.</summary>
public interface IServerLink
{
    bool IsConnected { get; }
    Uri? ServerUrl { get; }
    string? DeviceToken { get; }
    string State { get; }
    Task InvokeAsync(string method, object? arg, CancellationToken ct);
    Task<T> InvokeAsync<T>(string method, object? arg, CancellationToken ct);

    /// <summary>Fire-and-forget send; dropped when not connected.</summary>
    void Post(string method, object? arg);
}

public sealed class ServerLink(IIdentityStore identity, ILogger<ServerLink> logger) : IServerLink
{
    private volatile HubConnection? _hub;
    private volatile bool _ready;
    private volatile string _state = ClientStates.Starting;

    public bool IsConnected => _ready && _hub?.State == HubConnectionState.Connected;

    public Uri? ServerUrl => ServerUrls.TryNormalize(identity.Current.ServerUrl);

    public string? DeviceToken => identity.Current.DeviceToken;

    public string State
    {
        get => _state;
        set => _state = value;
    }

    internal void Attach(HubConnection? hub)
    {
        _ready = false;
        _hub = hub;
    }

    internal void SetReady(bool ready) => _ready = ready;

    public Task InvokeAsync(string method, object? arg, CancellationToken ct) =>
        Hub().InvokeCoreAsync(method, [arg], ct);

    public Task<T> InvokeAsync<T>(string method, object? arg, CancellationToken ct) =>
        Hub().InvokeCoreAsync<T>(method, [arg], ct);

    public void Post(string method, object? arg)
    {
        if (!IsConnected) return;
        _ = SendAsync(method, arg);
    }

    private async Task SendAsync(string method, object? arg)
    {
        try
        {
            await Hub().SendCoreAsync(method, [arg]);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sending {HubMethod} failed", method);
        }
    }

    private HubConnection Hub() =>
        _hub is { State: HubConnectionState.Connected } hub ? hub : throw new InvalidOperationException("Not connected to the server.");
}

public static class ServerUrls
{
    /// <summary>Absolute http(s) URL with a trailing slash, so relative API paths resolve below it.</summary>
    public static Uri? TryNormalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri;
    }
}
