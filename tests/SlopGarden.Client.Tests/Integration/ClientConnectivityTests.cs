using System.Net.Http.Json;
using SlopGarden.Client.Server;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using Xunit.Abstractions;

namespace SlopGarden.Client.Tests.Integration;

/// <summary>Finding, pairing with and reconnecting to the server.</summary>
public sealed class ClientConnectivityTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Client_reregisters_when_its_token_is_revoked_and_reconnects()
    {
        var state = new FakeServerState();
        await using var server = await FakeDeviceServer.StartAsync(output, state);
        await using var client = await ClientUnderTest.StartAsync(output, new Dictionary<string, string?> { ["Client:ServerUrl"] = server.Url.ToString() });

        await Wait.UntilAsync(() => state.Hellos.Count == 1, Timeout, "the first connection");
        var firstToken = state.Hellos.Single().Token;

        state.RotateToken();

        await Wait.UntilAsync(() => state.Hellos.Count == 2, Timeout, "the client reconnects with a new token");
        var secondToken = state.Hellos.Last().Token;
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(state.ValidToken, secondToken);
        Assert.Single(state.Registrations);
        Assert.Equal(secondToken, client.Get<Identity.IIdentityStore>().Current.DeviceToken);
    }

    [Fact]
    public async Task Readings_taken_while_the_server_is_down_are_delivered_after_reconnecting()
    {
        await using var emulator = await EmulatorHost.StartAsync(output);
        var garden = new GardenConfig();
        var state = new FakeServerState { Config = () => garden.Build() };
        var port = Ports.FreeTcp();
        var server = await FakeDeviceServer.StartAsync(output, state, port);
        await using var client = await ClientUnderTest.StartAsync(output, new Dictionary<string, string?>
        {
            ["Client:ServerUrl"] = server.Url.ToString(),
            ["Plugins:Emulator:BaseUrl"] = emulator.Url.ToString(),
        });
        try
        {
            await Wait.UntilAsync(() => state.Readings.Any(r => r.ChannelId == garden.MoistureChannel), Timeout, "readings arrive");
        }
        finally
        {
            await server.DisposeAsync();
        }

        var downSince = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var downUntil = DateTimeOffset.UtcNow;
        await using var restarted = await FakeDeviceServer.StartAsync(output, state, port);

        await Wait.UntilAsync(() => state.Hellos.Count >= 2, TimeSpan.FromSeconds(20), "the client reconnects to the restarted server");
        await Wait.UntilAsync(() => state.Readings.Any(r => r.ChannelId == garden.MoistureChannel && r.Time > downSince && r.Time < downUntil),
            Timeout, "readings buffered while offline are uploaded");
        Assert.Single(state.Registrations);
    }

    [Fact]
    public async Task Server_can_pair_a_client_by_ip_through_the_local_api()
    {
        var state = new FakeServerState();
        await using var server = await FakeDeviceServer.StartAsync(output, state);
        var localPort = Ports.FreeTcp();
        await using var client = await ClientUnderTest.StartAsync(output, new Dictionary<string, string?>
        {
            ["Client:LocalApiPort"] = localPort.ToString(),
            // Nobody answers on this port, so the client keeps searching until it is paired.
            ["Client:DiscoveryPort"] = Ports.FreeUdp().ToString(),
        });
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}/") };
        await Wait.UntilAsync(() => client.Get<IServerLink>().State == ClientStates.Discovering, Timeout, "the client searches for a server");
        ClientLocalStatus? before = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (before is null)
        {
            try
            {
                before = await http.GetFromJsonAsync<ClientLocalStatus>("status", SlopJson.Options);
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        Assert.Equal(client.HardwareId, before.HardwareId);
        Assert.False(before.Connected);

        var invalid = await http.PostAsJsonAsync("pair", new ManualPairRequest("not a url"), SlopJson.Options);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);

        var response = await http.PostAsJsonAsync("pair", new ManualPairRequest(server.Url.ToString()), SlopJson.Options);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await Wait.UntilAsync(() => state.Hellos.Count == 1, Timeout, "the paired client connects");
        Assert.Equal(DiscoveredVia.ManualIp, Assert.Single(state.Registrations).DiscoveredVia);
        var after = await http.GetFromJsonAsync<ClientLocalStatus>("status", SlopJson.Options);
        Assert.True(after!.Connected);

        var hijack = await http.PostAsJsonAsync("pair", new ManualPairRequest("http://192.0.2.10:5080"), SlopJson.Options);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, hijack.StatusCode);
        Assert.Single(state.Hellos);
    }

    [Fact]
    public async Task Client_finds_the_server_by_udp_discovery()
    {
        var state = new FakeServerState();
        await using var server = await FakeDeviceServer.StartAsync(output, state);
        var discoveryPort = Ports.FreeUdp();
        using var responder = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, discoveryPort));
        using var cts = new CancellationTokenSource();
        var probes = new List<DiscoveryProbe>();
        var responding = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var received = await responder.ReceiveAsync(cts.Token);
                var probe = System.Text.Json.JsonSerializer.Deserialize<DiscoveryProbe>(received.Buffer, SlopJson.Options);
                if (probe?.Type != Discovery.DiscoverMessageType) continue;
                lock (probes) probes.Add(probe);
                // Only answer the loopback probe: the fake server listens on 127.0.0.1 only.
                if (!System.Net.IPAddress.IsLoopback(received.RemoteEndPoint.Address)) continue;
                var announcement = new DiscoveryAnnouncement(Discovery.ServerMessageType, $"http://0.0.0.0:{server.Url.Port}", "Test server", "0.1.0");
                await responder.SendAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(announcement, SlopJson.Options), received.RemoteEndPoint, cts.Token);
            }
        });

        await using var client = await ClientUnderTest.StartAsync(output, new Dictionary<string, string?> { ["Client:DiscoveryPort"] = discoveryPort.ToString() });

        await Wait.UntilAsync(() => state.Hellos.Count == 1, Timeout, "the discovered server is connected");
        await cts.CancelAsync();
        Assert.Equal(DiscoveredVia.Broadcast, Assert.Single(state.Registrations).DiscoveredVia);
        lock (probes) Assert.Contains(probes, p => p.HardwareId == client.HardwareId);
        Assert.Equal(server.Url, new Uri(client.Get<Identity.IIdentityStore>().Current.ServerUrl!));
    }
}
