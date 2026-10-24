using SlopGarden.Client.Actuators;
using SlopGarden.Client.Server;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using Xunit.Abstractions;

namespace SlopGarden.Client.Tests.Integration;

/// <summary>Emulator + fake server + real client host, talking over HTTP and SignalR on loopback.</summary>
public sealed class ClientEndToEndTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static ActuatorCommandMessage Command(Guid actuatorId, string action, double? duration = null) =>
        new(Guid.NewGuid(), actuatorId, action, duration, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "integration-test", null);

    [Fact]
    public async Task Client_registers_reports_readings_obeys_commands_and_uploads_frames()
    {
        await using var emulator = await EmulatorHost.StartAsync(output);
        var garden = new GardenConfig();
        var state = new FakeServerState { Config = () => garden.Build() };
        await using var server = await FakeDeviceServer.StartAsync(output, state);
        await using var client = await ClientUnderTest.StartAsync(output, new Dictionary<string, string?>
        {
            ["Client:ServerUrl"] = server.Url.ToString(),
            ["Plugins:Emulator:BaseUrl"] = emulator.Url.ToString(),
        });
        var sim = emulator.Simulator;
        // 1 real second = 2 simulated minutes, so a short pulse visibly wets the soil.
        sim.SetTimeScale(120);

        // Registration, approval, hub connection and configuration.
        var (hello, token) = await Wait.ForAsync(() => state.Hellos.TryPeek(out var h) ? new Tuple<DeviceHello, string>(h.Hello, h.Token) : null, Timeout, "the client says hello");
        Assert.Equal(state.ValidToken, token);
        var registration = Assert.Single(state.Registrations);
        Assert.Equal(client.HardwareId, registration.HardwareId);
        Assert.Equal(DiscoveredVia.Configured, registration.DiscoveredVia);
        Assert.Equal("integration-client", registration.Hostname);
        var emulatorSensorPlugin = Assert.Single(registration.Plugins, p => p.PluginId == "emulator-sensor");
        Assert.NotNull(Assert.Single(emulatorSensorPlugin.Components!).SettingsSchema["properties"]?["sensorId"]);
        Assert.Contains(hello.Plugins, p => p.PluginId == "gpio-relay" && p.Trust == PluginTrust.Builtin);

        var applied = await Wait.ForAsync(() => state.ConfigsApplied.FirstOrDefault(), Timeout, "the configuration is applied");
        Assert.Equal(1, applied.Version);
        Assert.True(applied.Success, string.Join("; ", applied.Errors));
        Assert.True(File.Exists(Path.Combine(client.DataDirectory, "config.json")), "config is cached for offline restarts");

        // Readings flow; a broken sensor only produces events.
        await Wait.UntilAsync(() => state.Readings.Count(r => r.ChannelId == garden.MoistureChannel) >= 2 && state.Readings.Any(r => r.ChannelId == garden.TankChannel),
            Timeout, "moisture and tank readings arrive");
        var firstMoisture = state.Readings.First(r => r.ChannelId == garden.MoistureChannel);
        Assert.Equal(ReadingQuality.Good, firstMoisture.Quality);
        Assert.InRange(firstMoisture.Value, 5, 95);
        await Wait.UntilAsync(() => state.Events.Any(e => e.Category == EventCategories.Sensor && e.SensorId == garden.BrokenSensor), Timeout,
            "the unplugged sensor is reported");

        // Pulse: valve on, then off on its own; soil moisture rises.
        var moistureBefore = sim.TrueValue("tomato-soil-moisture");
        var pulse = Command(garden.Valve, ActuatorActions.Pulse, 3);
        await server.SendAsync(DeviceHubMethods.ExecuteCommand, pulse);
        await Wait.UntilAsync(() => sim.GetActuator("tomato-valve").On, Timeout, "the emulated valve opens");
        await Wait.UntilAsync(() => state.AckStatuses(pulse.CommandId).Contains(CommandStatuses.Completed), Timeout, "the pulse completes");
        Assert.Equal([CommandStatuses.Received, CommandStatuses.Running, CommandStatuses.Completed], state.AckStatuses(pulse.CommandId));
        Assert.False(sim.GetActuator("tomato-valve").On);
        var history = sim.GetActuator("tomato-valve").History.Where(h => h.Source == "slopgarden-client").Select(h => h.On).ToList();
        Assert.Equal([false, true, false], history);

        var run = await Wait.ForAsync(() => state.Runs.FirstOrDefault(r => r.CommandId == pulse.CommandId), Timeout, "the run is reported");
        Assert.InRange(run.DurationSec, 2.5, 5);
        Assert.Equal(StopReasons.Completed, run.StopReason);
        Assert.InRange(run.AmountMl!.Value, 20, 42);
        await Wait.UntilAsync(() => state.States.Any(s => s.ActuatorId == garden.Valve && s.On) && state.States.Any(s => s.ActuatorId == garden.Valve && !s.On && s.CommandId == pulse.CommandId),
            Timeout, "live on/off states are pushed");

        Assert.True(sim.TrueValue("tomato-soil-moisture") > moistureBefore + 5, $"moisture should rise from {moistureBefore}");
        var readingsAfterPulse = run.EndedAt;
        await Wait.UntilAsync(() => state.Readings.Any(r => r.ChannelId == garden.MoistureChannel && r.Time > readingsAfterPulse && r.Value > moistureBefore + 5),
            Timeout, "the client reports the wetter soil");

        // Safety: 3 s of the 20 s daily budget are used; a 100 s pulse is capped to 30 s max run and still exceeds the rest.
        var greedy = Command(garden.Valve, ActuatorActions.Pulse, 100);
        await server.SendAsync(DeviceHubMethods.ExecuteCommand, greedy);
        await Wait.UntilAsync(() => state.AckStatuses(greedy.CommandId).Contains(CommandStatuses.Rejected), Timeout, "the over-limit pulse is rejected");
        Assert.Contains("daily run limit", state.Acks.Last(a => a.CommandId == greedy.CommandId).Reason);
        Assert.Equal(3, sim.GetActuator("tomato-valve").History.Count(h => h.Source == "slopgarden-client"));

        // Interlock: an (emulated) empty tank blocks watering.
        sim.SetOverride("water-tank-level", 5, durationSec: null);
        var lowSince = DateTimeOffset.UtcNow;
        await Wait.UntilAsync(() => state.Readings.Any(r => r.ChannelId == garden.TankChannel && r.Time > lowSince && r.Value < 10), Timeout, "the low tank level is read");
        var blocked = Command(garden.Valve, ActuatorActions.Pulse, 2);
        await server.SendAsync(DeviceHubMethods.ExecuteCommand, blocked);
        await Wait.UntilAsync(() => state.AckStatuses(blocked.CommandId).Contains(CommandStatuses.Rejected), Timeout, "the interlocked pulse is rejected");
        Assert.Contains("water tank below minimum", state.Acks.Last(a => a.CommandId == blocked.CommandId).Reason);
        sim.ClearOverride("water-tank-level");
        var refilledSince = DateTimeOffset.UtcNow;
        await Wait.UntilAsync(() => state.Readings.Any(r => r.ChannelId == garden.TankChannel && r.Time > refilledSince && r.Value > 50), Timeout, "the tank level recovers");

        // Cameras: periodic frames while connected, plus on-demand frames.
        var periodic = await Wait.ForAsync(() => state.Frames.FirstOrDefault(f => f.CameraId == garden.Camera && f.RequestId is null), Timeout, "a periodic frame is uploaded");
        Assert.Equal("image/jpeg", periodic.ContentType);
        Assert.True(periodic.Data is [0xFF, 0xD8, ..], "frame is a JPEG");
        Assert.Equal(320, periodic.Width);
        var request = new CaptureFrameRequest(Guid.NewGuid(), garden.Camera, 160, 90);
        await server.SendAsync(DeviceHubMethods.CaptureFrame, request);
        var requested = await Wait.ForAsync(() => state.Frames.FirstOrDefault(f => f.RequestId == request.RequestId), Timeout, "the requested frame is uploaded");
        Assert.Equal(160, requested.Width);
        Assert.Equal(90, requested.Height);

        // Hardware discovery lists the emulator inventory.
        var discovered = await Wait.ForAsync(() => state.Discoveries.FirstOrDefault(), Timeout, "discovered hardware is reported");
        Assert.Contains(discovered.Items, i => i.PluginId == "emulator-sensor" && i.SuggestedSettings["sensorId"]?.GetValue<string>() == "tomato-soil-moisture");
        Assert.Contains(discovered.Items, i => i.PluginId == "emulator-actuator" && i.ActuatorKind == ActuatorKinds.Valve);

        // Emergency stop switches a running valve off at once.
        var longPulse = Command(garden.Valve, ActuatorActions.Pulse, 10);
        await server.SendAsync(DeviceHubMethods.ExecuteCommand, longPulse);
        await Wait.UntilAsync(() => sim.GetActuator("tomato-valve").On, Timeout, "the valve opens again");
        await server.SendAsync(DeviceHubMethods.EmergencyStop, new EmergencyStopState(true, [], "integration test"));
        await Wait.UntilAsync(() => !sim.GetActuator("tomato-valve").On, TimeSpan.FromSeconds(3), "the emergency stop closes the valve");
        await Wait.UntilAsync(() => state.AckStatuses(longPulse.CommandId).Contains(CommandStatuses.Cancelled), Timeout, "the stopped pulse is acked cancelled");
        Assert.Equal(StopReasons.EmergencyStop, (await Wait.ForAsync(() => state.Runs.FirstOrDefault(r => r.CommandId == longPulse.CommandId), Timeout, "run reported")).StopReason);
        Assert.Contains(state.Events, e => e.Severity == EventSeverities.Critical && e.Message.Contains("integration test"));

        var status = client.Get<LocalApiServer>().Status();
        Assert.True(status.Connected);
        Assert.Equal(ClientStates.Connected, status.State);
        Assert.Equal(registration.HardwareId, status.HardwareId);
        Assert.NotNull(status.DeviceId);
        Assert.Equal(1, status.AppliedConfigVersion);
    }
}
