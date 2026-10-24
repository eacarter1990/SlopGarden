using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SlopGarden.Contracts;

namespace SlopGarden.Setup;

/// <summary>Looks for a SlopGarden server on the LAN. mDNS and UDP run together; a typed address is only used when both are silent.</summary>
public static class LanServerSearch
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    public static async Task<string?> FindAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var mdns = ListenMdnsAsync(linked.Token);
        var udp = ListenUdpAsync(linked.Token);
        var winner = await Task.WhenAny(mdns, udp);
        var other = ReferenceEquals(winner, mdns) ? udp : mdns;
        if (winner.IsCompletedSuccessfully && winner.Result is { Length: > 0 } url)
        {
            await linked.CancelAsync();
            try { await other; } catch (OperationCanceledException) { }
            return url;
        }
        try
        {
            return await other;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> ListenMdnsAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Discovery.MdnsPort));
            udp.JoinMulticastGroup(IPAddress.Parse(Discovery.MdnsGroup));
            udp.MulticastLoopback = true;
            var group = new IPEndPoint(IPAddress.Parse(Discovery.MdnsGroup), Discovery.MdnsPort);
            var query = MdnsCodec.Query(Discovery.MdnsServerService);
            await udp.SendAsync(query, group, ct);
            while (!ct.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(ct);
                var url = MachineSetup.ServerUrlFromMdns(packet.Buffer);
                if (url is not null) return RewriteUnreachable(url, packet.RemoteEndPoint.Address);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            udp?.Dispose();
        }
        return null;
    }

    private static async Task<string?> ListenUdpAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var probe = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new DiscoveryProbe(Discovery.DiscoverMessageType, "slopgarden-setup", Environment.MachineName, "setup"),
            SlopJson.Options));
        try
        {
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, Discovery.UdpPort), ct);
        }
        catch (SocketException)
        {
            return null;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(ct);
                var url = MachineSetup.ServerUrlFromUdp(packet.Buffer);
                if (url is not null) return RewriteUnreachable(url, packet.RemoteEndPoint.Address);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return null;
        }
        return null;
    }

    /// <summary>A server bound to every interface announces 0.0.0.0. Use the address the reply came from.</summary>
    private static string RewriteUnreachable(string url, IPAddress sender)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Host is not ("0.0.0.0" or "+" or "*" or "::" or "[::]")) return url;
        return new UriBuilder(uri) { Host = sender.ToString() }.Uri.ToString().TrimEnd('/');
    }
}
