using System.Device.Gpio;
using System.Text.Json.Nodes;
using Iot.Device.Ads1115;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;
using SlopGarden.Plugins.Hardware;

namespace SlopGarden.Client.Tests.Hardware;

public sealed class Ds18b20Tests : IDisposable
{
    private readonly string _root = Wait.TempDir("w1");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string AddProbe(string id, string? temperature = null, string? w1Slave = null)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        if (temperature is not null) File.WriteAllText(Path.Combine(dir, "temperature"), temperature);
        if (w1Slave is not null) File.WriteAllText(Path.Combine(dir, "w1_slave"), w1Slave);
        return id;
    }

    private IServiceProvider Services() =>
        new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [Ds18b20Sysfs.RootConfigKey] = _root }).Build())
            .BuildServiceProvider();

    private ISensor Create(JsonObject settings) =>
        new Ds18b20Plugin().Create(new PluginContext(Guid.NewGuid(), "probe", settings, Services(), NullLogger.Instance));

    [Theory]
    [InlineData("21562\n", 21.562)]
    [InlineData("-1250", -1.25)]
    [InlineData("0", 0)]
    public void Parses_the_temperature_attribute(string content, double expected)
    {
        Assert.Equal(expected, Ds18b20Sysfs.ParseTemperatureFile(content)!.Value, 6);
    }

    [Fact]
    public void Rejects_garbage_temperature_attribute()
    {
        Assert.Null(Ds18b20Sysfs.ParseTemperatureFile("n/a"));
    }

    [Fact]
    public void Parses_w1_slave_with_valid_crc()
    {
        const string content = "72 01 4b 46 7f ff 0e 10 57 : crc=57 YES\n72 01 4b 46 7f ff 0e 10 57 t=23125\n";
        Assert.Equal(23.125, Ds18b20Sysfs.ParseW1Slave(content)!.Value, 6);
    }

    [Fact]
    public void Rejects_w1_slave_with_failed_crc()
    {
        const string content = "72 01 4b 46 7f ff 0e 10 57 : crc=a1 NO\n72 01 4b 46 7f ff 0e 10 57 t=23125\n";
        Assert.Null(Ds18b20Sysfs.ParseW1Slave(content));
        Assert.Null(Ds18b20Sysfs.ParseW1Slave(""));
    }

    [Fact]
    public void Lists_only_ds18b20_family_devices()
    {
        AddProbe("28-0316a2795cff", "20000");
        AddProbe("28-000005e2fdc3", "21000");
        Directory.CreateDirectory(Path.Combine(_root, "w1_bus_master1"));
        Directory.CreateDirectory(Path.Combine(_root, "10-000802b4d1a3"));
        Assert.Equal(["28-000005e2fdc3", "28-0316a2795cff"], Ds18b20Sysfs.ListProbes(_root));
        Assert.Empty(Ds18b20Sysfs.ListProbes(Path.Combine(_root, "nope")));
    }

    [Fact]
    public async Task Reads_the_temperature_attribute_first_then_w1_slave()
    {
        var modern = AddProbe("28-aaaa", "19875", "garbage NO\n t=1");
        var legacy = AddProbe("28-bbbb", w1Slave: "aa : crc=57 YES\naa t=17500");
        Assert.Equal(19.875, await Ds18b20Sysfs.ReadCelsiusAsync(_root, modern, CancellationToken.None), 6);
        Assert.Equal(17.5, await Ds18b20Sysfs.ReadCelsiusAsync(_root, legacy, CancellationToken.None), 6);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Ds18b20Sysfs.ReadCelsiusAsync(_root, "28-missing", CancellationToken.None));
    }

    [Fact]
    public async Task Plugin_picks_the_only_probe_and_reads_via_configured_root()
    {
        AddProbe("28-0316a2795cff", "22500");
        await using var sensor = Create(new JsonObject());
        var reading = Assert.Single(await sensor.ReadAsync(CancellationToken.None));
        Assert.Equal(Ds18b20Plugin.ChannelKey, reading.ChannelKey);
        Assert.Equal(22.5, reading.Value, 6);
        Assert.Equal(ReadingQuality.Good, reading.Quality);
    }

    [Fact]
    public async Task Power_on_reset_value_is_marked_suspect()
    {
        AddProbe("28-0316a2795cff", "85000");
        await using var sensor = Create(new JsonObject { ["deviceId"] = "28-0316a2795cff" });
        Assert.Equal(ReadingQuality.Suspect, Assert.Single(await sensor.ReadAsync(CancellationToken.None)).Quality);
    }

    [Fact]
    public void Plugin_requires_device_id_when_several_probes_exist()
    {
        AddProbe("28-a", "1");
        AddProbe("28-b", "1");
        var ex = Assert.Throws<ArgumentException>(() => Create(new JsonObject()));
        Assert.Contains("2 probes", ex.Message);
    }

    [Fact]
    public async Task Settings_root_path_overrides_configuration()
    {
        var other = Wait.TempDir("w1-other");
        try
        {
            Directory.CreateDirectory(Path.Combine(other, "28-cafe"));
            File.WriteAllText(Path.Combine(other, "28-cafe", "temperature"), "5000");
            await using var sensor = Create(new JsonObject { ["rootPath"] = other });
            Assert.Equal(5, Assert.Single(await sensor.ReadAsync(CancellationToken.None)).Value, 6);
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public async Task Discovery_suggests_each_probe()
    {
        AddProbe("28-0316a2795cff", "20000");
        AddProbe("28-000005e2fdc3", "21000");
        var found = await new Ds18b20Discovery().DiscoverAsync(Services(), CancellationToken.None);
        Assert.Equal(2, found.Count);
        Assert.All(found, d => Assert.Equal(Ds18b20Plugin.Id, d.PluginId));
        Assert.Contains(found, d => d.SuggestedSettings["deviceId"]!.GetValue<string>() == "28-0316a2795cff" && d.Address == "w1:28-0316a2795cff");
    }
}

public sealed class V4l2DiscoveryTests
{
    [Fact]
    public void Usb_webcam_capture_node_is_suggested_and_metadata_is_not()
    {
        var root = Wait.TempDir("v4l");
        try
        {
            WriteNode(root, "video0", "uvcvideo", "0", "HD Webcam: HD Webcam");
            WriteNode(root, "video1", "uvcvideo", "1", "HD Webcam: HD Webcam");
            WriteNode(root, "video2", "unicam", "0", "unicam");
            var found = V4l2Devices.List(root);
            var one = Assert.Single(found);
            Assert.Equal(V4l2CameraPlugin.Id, one.PluginId);
            Assert.Equal(PluginKinds.Camera, one.Kind);
            Assert.Equal("/dev/video0", one.Address);
            Assert.Equal("HD Webcam", one.SuggestedName);
            Assert.Equal("/dev/video0", one.SuggestedSettings["device"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void WriteNode(string root, string node, string driver, string index, string name)
    {
        var dir = Path.Combine(root, node);
        var device = Path.Combine(dir, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(dir, "index"), index);
        File.WriteAllText(Path.Combine(dir, "name"), name);
        Directory.CreateSymbolicLink(Path.Combine(device, "driver"), driver);
    }
}

public sealed class OtherHardwareTests
{
    [Theory]
    [InlineData("A0=512,A1=300", "A0", 512)]
    [InlineData("A0=512;A1=300", "A1", 300)]
    [InlineData("  a2=1023  A3=0 \r", "A2", 1023)]
    [InlineData("A0=3.3", "A0", 3.3)]
    public void Arduino_protocol_parses_channel_values(string line, string channel, double expected)
    {
        Assert.Equal(expected, ArduinoAdcProtocol.Parse(line)[channel], 6);
    }

    [Theory]
    [InlineData("# SlopGarden ADC bridge v1")]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("A0=nan,A1=")]
    public void Arduino_protocol_ignores_comments_and_garbage(string line)
    {
        Assert.Empty(ArduinoAdcProtocol.Parse(line));
    }

    [Fact]
    public void Arduino_protocol_keeps_valid_tokens_next_to_broken_ones()
    {
        var values = ArduinoAdcProtocol.Parse("A0=12,A1=oops,=5,A2=7");
        Assert.Equal(2, values.Count);
        Assert.Equal(7, values["A2"]);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void Relay_level_honours_active_low_boards(bool on, bool activeLow, bool high)
    {
        Assert.Equal(high ? PinValue.High : PinValue.Low, RelayLevel.For(on, activeLow));
    }

    [Theory]
    [InlineData("6.144", MeasuringRange.FS6144)]
    [InlineData("4.096", MeasuringRange.FS4096)]
    [InlineData("2", MeasuringRange.FS2048)]
    [InlineData("0.256", MeasuringRange.FS0256)]
    [InlineData("bogus", MeasuringRange.FS4096)]
    public void Ads1115_gain_maps_to_measuring_range(string gain, MeasuringRange expected)
    {
        Assert.Equal(expected, Ads1115Plugin.Range(gain));
    }

    [Fact]
    public void Ads1115_rejects_invalid_channels()
    {
        Assert.Equal(InputMultiplexer.AIN3, Ads1115Plugin.Multiplexer(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ads1115Plugin.Multiplexer(4));
    }

    [Theory]
    [InlineData(15, 2, 450, 1.0)]
    [InlineData(48, 2, 450, 3.2)]
    [InlineData(0, 2, 450, 0)]
    [InlineData(75, 1, 450, 10)]
    [InlineData(10, 0, 450, 0)]
    [InlineData(10, 2, 0, 0)]
    public void Flow_meter_converts_pulses_to_liters_per_minute(long pulses, double windowSec, double pulsesPerLiter, double expected)
    {
        Assert.Equal(expected, GpioFlowMeterPlugin.LitersPerMinute(pulses, windowSec, pulsesPerLiter), 6);
    }

    [Fact]
    public void Flow_meter_default_matches_yf_s201_frequency_formula()
    {
        // F[Hz] = 7.5 × Q[L/min]: 7.5 Hz for one second is 1 L/min.
        Assert.Equal(1.0, GpioFlowMeterPlugin.LitersPerMinute(15, 2, GpioFlowMeterPlugin.DefaultPulsesPerLiter), 6);
    }

    [Fact]
    public void Pulse_window_counts_only_recent_pulses()
    {
        var time = new FakeTimeProvider();
        var window = new PulseWindow(TimeSpan.FromSeconds(2), time);
        time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++) window.Add();
        Assert.Equal((10L, 1.0), window.Count());

        time.Advance(TimeSpan.FromSeconds(1.5));
        for (var i = 0; i < 5; i++) window.Add();
        var (pulses, seconds) = window.Count();
        Assert.Equal(15, pulses);
        Assert.Equal(2, seconds, 6);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, window.Count().Pulses);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, window.Count().Pulses);
    }

    [Fact]
    public void Flow_meter_schema_requires_a_pin_and_reports_flow()
    {
        var plugin = new GpioFlowMeterPlugin();
        var schema = plugin.SettingsSchema;
        Assert.Equal(["pin"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(450, schema["properties"]!["pulsesPerLiter"]!["default"]!.GetValue<double>());
        Assert.True(schema["properties"]!["pullUp"]!["default"]!.GetValue<bool>());
        Assert.Equal(2, schema["properties"]!["windowSec"]!["default"]!.GetValue<double>());
        var channel = Assert.Single(plugin.DescribeChannels(new JsonObject()));
        Assert.Equal(("flow", Metrics.FlowLitersPerMin, "L/min"), (channel.Key, channel.Metric, channel.Unit));
    }

    [Fact]
    public void Gpio_relay_requires_an_explicit_active_low_setting()
    {
        var schema = new GpioRelayPlugin().SettingsSchema;
        Assert.Contains("activeLow", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}
