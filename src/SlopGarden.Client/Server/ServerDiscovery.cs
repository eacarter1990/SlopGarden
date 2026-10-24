using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

/// <summary>Finds the server on the LAN: broadcasts a <see cref="DiscoveryProbe"/> on UDP 47474 every 10 s and
/// waits for a unicast <see cref="DiscoveryAnnouncement"/>.</summary>
public sealed class ServerDiscovery(IOptions<ClientOptions> options, IIdentityStore identity, MdnsAgent mdns, ILogger<ServerDiscovery> logger)
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);

    /// <summary>UDP broadcast and mDNS run together. The first one to find a server wins.</summary>
    public async Task<(Uri Server, string Via)> DiscoverAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var udp = DiscoverUdpAsync(linked.Token);
        var mdnsTask = mdns.WaitForServerAsync(linked.Token);
        var winner = await Task.WhenAny(udp, mdnsTask);
        if (!winner.IsCompletedSuccessfully)
        {
            var other = ReferenceEquals(winner, udp) ? mdnsTask : udp;
            winner = other;
            await winner;
        }
        await linked.CancelAsync();
        return ReferenceEquals(winner, mdnsTask)
            ? (winner.Result, DiscoveredVia.Mdns)
            : (winner.Result, DiscoveredVia.Broadcast);
    }

    private async Task<Uri> DiscoverUdpAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var port = options.Value.DiscoveryPort;
        var probe = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new DiscoveryProbe(Discovery.DiscoverMessageType, identity.Current.HardwareId, ClientInfo.Hostname(options.Value), ClientInfo.Version),
            ClientJson.Options));
        logger.LogInformation("No server configured; searching the LAN (UDP {Port})", port);
        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var target in Targets(port))
            {
                try
                {
                    await udp.SendAsync(probe, target, ct);
                }
                catch (SocketException ex)
                {
                    logger.LogDebug("Discovery probe to {Target} failed: {Error}", target, ex.Message);
                }
            }
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(ProbeInterval);
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(window.Token);
                    if (TryParse(result.Buffer, result.RemoteEndPoint, out var server))
                    {
                        logger.LogInformation("Discovered server at {ServerUrl}", server);
                        return server;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (SocketException ex)
            {
                logger.LogDebug("Discovery receive failed: {Error}", ex.Message);
                await Task.Delay(ProbeInterval, ct);
            }
            if (++attempts % 6 == 0) logger.LogInformation("Still searching for a SlopGarden server; set Client:ServerUrl or pair by IP from the server UI");
        }
    }

    public static bool TryParse(byte[] payload, IPEndPoint sender, out Uri server)
    {
        server = null!;
        try
        {
            var announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(payload, ClientJson.Options);
            if (announcement is null || announcement.Type != Discovery.ServerMessageType) return false;
            var uri = ServerUrls.TryNormalize(announcement.ServerUrl);
            if (uri is null) return false;
            // Servers bound to a wildcard address announce it verbatim; use the address the reply came from.
            if (uri.Host is "0.0.0.0" or "+" or "*" or "[::]" or "::" || (uri.IsLoopback && !IPAddress.IsLoopback(sender.Address)))
                uri = new UriBuilder(uri) { Host = sender.Address.ToString() }.Uri;
            server = uri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<IPEndPoint> Targets(int port)
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address) || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return targets.Select(a => new IPEndPoint(a, port));
    }
}
