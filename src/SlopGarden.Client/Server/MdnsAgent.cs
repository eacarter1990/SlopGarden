using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Identity;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Server;

/// <summary>
/// Advertises this client as <c>_slopgarden._tcp</c> and watches for a <c>_slopgarden-server._tcp</c> announcement.
/// Discovered devices still register as pending; mDNS only finds the peer.
/// </summary>
public sealed class MdnsAgent(IOptions<ClientOptions> options, IIdentityStore identity, ILogger<MdnsAgent> logger) : BackgroundService
{
    private readonly TaskCompletionSource<Uri> _server = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _port;

    public Task<Uri> WaitForServerAsync(CancellationToken ct) => _server.Task.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.LocalApiPort <= 0) return;
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Discovery.MdnsPort));
            udp.JoinMulticastGroup(IPAddress.Parse(Discovery.MdnsGroup));
            udp.MulticastLoopback = true;
        }
        catch (SocketException ex)
        {
            logger.LogWarning("mDNS disabled: {Error}", ex.Message);
            udp?.Dispose();
            return;
        }

        logger.LogInformation("Advertising this client over mDNS ({Service})", Discovery.MdnsClientService);
        using (udp)
        {
            var group = new IPEndPoint(IPAddress.Parse(Discovery.MdnsGroup), Discovery.MdnsPort);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            await SendAsync(udp, Announce(), group, stoppingToken);
            await SendAsync(udp, MdnsCodec.Query(Discovery.MdnsServerService), group, stoppingToken);
            var next = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                var receive = udp.ReceiveAsync(stoppingToken).AsTask();
                var done = await Task.WhenAny(receive, next);
                if (done == next)
                {
                    if (!await next) break;
                    await SendAsync(udp, Announce(), group, stoppingToken);
                    await SendAsync(udp, MdnsCodec.Query(Discovery.MdnsServerService), group, stoppingToken);
                    next = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                    continue;
                }
                try
                {
                    var res = await receive;
                    OnPacket(res.Buffer, res.RemoteEndPoint, udp, group);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                }
            }
        }
    }

    private void OnPacket(byte[] packet, IPEndPoint remote, UdpClient udp, IPEndPoint group)
    {
        if (MdnsCodec.IsQueryFor(packet, Discovery.MdnsClientService))
        {
            _ = SendAsync(udp, Announce(), group, CancellationToken.None);
            return;
        }
        if (!MdnsCodec.TryRead(packet, out var message)) return;
        foreach (var svc in message.Services)
        {
            if (string.IsNullOrWhiteSpace(svc.Url)) continue;
            if (!svc.ServiceType.Contains("slopgarden-server", StringComparison.OrdinalIgnoreCase)) continue;
            var uri = ServerUrls.TryNormalize(svc.Url);
            if (uri is null) continue;
            if (uri.Host is "0.0.0.0" or "localhost" or "127.0.0.1")
                uri = new UriBuilder(uri) { Host = (svc.Address ?? remote.Address).ToString() }.Uri;
            logger.LogInformation("mDNS server at {Url}", uri);
            _server.TrySetResult(uri);
        }
    }

    private byte[] Announce()
    {
        var name = ClientInfo.Hostname(options.Value);
        var ip = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
        _port = options.Value.LocalApiPort;
        return MdnsCodec.AnnounceClient(name, _port, ip, identity.Current.HardwareId, ClientInfo.Version, name);
    }

    private static async Task SendAsync(UdpClient udp, byte[] bytes, IPEndPoint group, CancellationToken ct)
    {
        try { await udp.SendAsync(bytes, group, ct); }
        catch (SocketException) { }
    }
}
