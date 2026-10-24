using System.Device.I2c;
using System.Text.Json.Nodes;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>Probes Linux I²C buses for ADS1115 and BME280 chips using read-only register reads.</summary>
public sealed class I2cDiscovery : IHardwareDiscovery
{
    private const byte Bme280ChipIdRegister = 0xD0;
    private const byte Bme280ChipId = 0x60;
    private const byte Ads1115ConfigRegister = 0x01;

    public string PluginId => "i2c";

    public ValueTask<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredDevice>();
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev")) return ValueTask.FromResult<IReadOnlyList<DiscoveredDevice>>(found);

        foreach (var bus in Directory.EnumerateFiles("/dev", "i2c-*").Select(f => int.TryParse(Path.GetFileName(f)[4..], out var b) ? b : -1).Where(b => b >= 0).Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var address in new[] { 0x76, 0x77 })
            {
                if (TryReadRegister(bus, address, Bme280ChipIdRegister, 1) is [Bme280ChipId])
                {
                    found.Add(new DiscoveredDevice(Bme280Plugin.Id, PluginKinds.Sensor, $"i2c-{bus}:0x{address:x2}", "BME280 environment sensor",
                        new JsonObject { ["i2cBus"] = bus, ["address"] = $"0x{address:x2}" },
                        new Bme280Plugin().DescribeChannels(new JsonObject())));
                }
            }
            for (var address = 0x48; address <= 0x4B; address++)
            {
                // ADS1115 config register has the OS bit (0x80 in the high byte) set while idle.
                if (TryReadRegister(bus, address, Ads1115ConfigRegister, 2) is [var hi, _] && (hi & 0x80) != 0)
                {
                    for (var channel = 0; channel < 4; channel++)
                    {
                        found.Add(new DiscoveredDevice(Ads1115Plugin.Id, PluginKinds.Sensor, $"i2c-{bus}:0x{address:x2}/A{channel}",
                            $"ADS1115 0x{address:x2} input A{channel}",
                            new JsonObject { ["i2cBus"] = bus, ["address"] = $"0x{address:x2}", ["channel"] = channel, ["gain"] = "4.096" },
                            [new ChannelDescriptor(Ads1115Plugin.ChannelKey, Metrics.Voltage, "V")]));
                    }
                }
            }
        }
        return ValueTask.FromResult<IReadOnlyList<DiscoveredDevice>>(found);
    }

    private static byte[]? TryReadRegister(int bus, int address, byte register, int length)
    {
        try
        {
            using var device = I2cDevice.Create(new I2cConnectionSettings(bus, address));
            var buffer = new byte[length];
            device.WriteRead([register], buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }
}
