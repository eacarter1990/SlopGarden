using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Background;

/// <summary>
/// Announces this server over mDNS and browses for SlopGarden clients. A discovered client is asked to register
/// (POST /pair) and stays pending until an Admin accepts it. UDP broadcast and add-by-IP keep working beside this.
/// </summary>
public sealed class MdnsDiscoveryService(IOptions<ServerOptions> options, IServiceProvider services, ILogger<MdnsDiscoveryService> log) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _paired = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DiscoveryEnabled) return;
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
            log.LogWarning(ex, "mDNS discovery disabled: cannot listen on UDP {Port}", Discovery.MdnsPort);
            udp?.Dispose();
            return;
        }

        log.LogInformation("mDNS discovery listening for {Service}", Discovery.MdnsClientService);
        using (udp)
        {
            var group = new IPEndPoint(IPAddress.Parse(Discovery.MdnsGroup), Discovery.MdnsPort);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var announce = Announce(services);
            await SendAsync(udp, announce, group, stoppingToken);
            await SendAsync(udp, MdnsCodec.Query(Discovery.MdnsClientService), group, stoppingToken);
            var next = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                var receive = udp.ReceiveAsync(stoppingToken).AsTask();
                var done = await Task.WhenAny(receive, next);
                if (done == next)
                {
                    if (!await next) break;
                    announce = Announce(services);
                    await SendAsync(udp, announce, group, stoppingToken);
                    await SendAsync(udp, MdnsCodec.Query(Discovery.MdnsClientService), group, stoppingToken);
                    next = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                    continue;
                }
                try
                {
                    var res = await receive;
                    Handle(res.Buffer, res.RemoteEndPoint, udp, group);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                }
            }
        }
    }

    private void Handle(byte[] packet, IPEndPoint remote, UdpClient udp, IPEndPoint group)
    {
        if (!MdnsCodec.TryRead(packet, out var message)) return;
        if (message.IsQuery && MdnsCodec.IsQueryFor(packet, Discovery.MdnsServerService))
        {
            _ = SendAsync(udp, Announce(services), group, CancellationToken.None);
            return;
        }
        foreach (var svc in message.Services.Where(s => s.ServiceType.Contains("_slopgarden._tcp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s.HardwareId)))
        {
            var address = svc.Address ?? remote.Address;
            if (IPAddress.IsLoopback(address) && remote.Address is { } from && !IPAddress.IsLoopback(from)) address = from;
            var key = svc.HardwareId!;
            if (_paired.TryGetValue(key, out var when) && DateTimeOffset.UtcNow - when < TimeSpan.FromMinutes(2)) continue;
            _paired[key] = DateTimeOffset.UtcNow;
            var port = svc.Port > 0 ? svc.Port : Discovery.ClientLocalApiPort;
            _ = PairAsync(address, port, key);
        }
    }

    private async Task PairAsync(IPAddress address, int port, string hardwareId)
    {
        try
        {
            var url = services.GetRequiredService<ServerUrlProvider>().Get();
            if (IPAddress.IsLoopback(address)) url = new UriBuilder(url) { Host = "localhost" }.Uri.ToString().TrimEnd('/');
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var res = await http.PostAsJsonAsync($"http://{address}:{port}/pair", new ManualPairRequest(url, "mdns"), SlopJson.Options);
            log.LogInformation("mDNS client {HardwareId} at {Address}:{Port} answered {Status}", hardwareId, address, port, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            log.LogDebug(ex, "mDNS pair of {HardwareId} at {Address} failed", hardwareId, address);
        }
    }

    private static byte[] Announce(IServiceProvider services)
    {
        var url = services.GetRequiredService<ServerUrlProvider>().Get();
        var host = "slopgarden";
        try { host = new Uri(url).Host; } catch (UriFormatException) { }
        var ip = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
        var port = 5080;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0) port = uri.Port;
        return MdnsCodec.AnnounceServer(host, port, ip, url);
    }

    private static async Task SendAsync(UdpClient udp, byte[] bytes, IPEndPoint group, CancellationToken ct)
    {
        try { await udp.SendAsync(bytes, group, ct); }
        catch (SocketException) { }
    }
}
