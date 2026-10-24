using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlopGarden.Contracts;
using SlopGarden.Server.Infrastructure;
using SlopGarden.Server.Services;

namespace SlopGarden.Server.Background;

/// <summary>Answers LAN broadcast probes from unpaired clients with this server's URL.</summary>
public sealed class UdpDiscoveryService(IOptions<ServerOptions> options, IServiceProvider services, ILogger<UdpDiscoveryService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DiscoveryEnabled) return;
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Discovery.UdpPort));
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex, "LAN discovery disabled: cannot listen on UDP {Port}", Discovery.UdpPort);
            return;
        }

        log.LogInformation("LAN discovery listening on UDP {Port}", Discovery.UdpPort);
        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var res = await udp.ReceiveAsync(stoppingToken);
                    var probe = JsonSerializer.Deserialize<DiscoveryProbe>(res.Buffer, SlopJson.Options);
                    if (probe?.Type != Discovery.DiscoverMessageType) continue;
                    var url = services.GetRequiredService<ServerUrlProvider>().Get();
                    if (IPAddress.IsLoopback(res.RemoteEndPoint.Address))
                        url = new UriBuilder(url) { Host = "localhost" }.Uri.ToString().TrimEnd('/');
                    var reply = new DiscoveryAnnouncement(Discovery.ServerMessageType, url, options.Value.ServerName, typeof(UdpDiscoveryService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, SlopJson.Options));
                    await udp.SendAsync(bytes, res.RemoteEndPoint, stoppingToken);
                    log.LogInformation("Discovery probe from {Host} ({Endpoint}); answered {Url}", probe.Hostname, res.RemoteEndPoint, url);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is JsonException or SocketException)
                {
                    log.LogDebug(ex, "Ignoring malformed discovery packet");
                }
            }
        }
    }
}
