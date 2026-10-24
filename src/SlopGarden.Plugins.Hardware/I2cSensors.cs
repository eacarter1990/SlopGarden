using System.Device.I2c;
using System.Globalization;
using System.Text.Json.Nodes;
using Iot.Device.Ads1115;
using Iot.Device.Bmxx80;
using Iot.Device.Bmxx80.PowerMode;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

public sealed class Ads1115Plugin : ISensorPlugin
{
    public const string Id = "ads1115";
    public const string ChannelKey = "raw";
    public const int DefaultAddress = 0x48;

    private static readonly string[] Gains = ["6.144", "4.096", "2.048", "1.024", "0.512", "0.256"];

    public string PluginId => Id;
    public string DisplayName => "ADS1115 analog input";
    public string Description =>
        "16-bit I²C ADC. Reports the input voltage on channel 'raw'; calibrate it with two_point (dry/wet) to get soil moisture %.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("i2cBus", "integer", "I²C bus number.", 1)
        .Prop("address", "string", "I²C address (0x48–0x4B).", "0x48")
        .Prop("channel", "integer", "Single-ended input channel 0–3.", 0, required: true)
        .Prop("gain", "string", "Full-scale range in volts.", "4.096", @enum: Gains)
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) => [new(ChannelKey, Metrics.Voltage, "V")];

    public ISensor Create(PluginContext context)
    {
        var s = context.Settings;
        var channel = Settings.Int(s, "channel");
        if (channel is < 0 or > 3) throw new ArgumentException("Setting 'channel' must be 0–3.");
        var device = I2cDevice.Create(new I2cConnectionSettings(Settings.Int(s, "i2cBus", 1), Settings.Int(s, "address", DefaultAddress)));
        try
        {
            var adc = new Ads1115(device, Multiplexer(channel), Range(Settings.String(s, "gain", "4.096")));
            return new Ads1115Sensor(adc);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public static InputMultiplexer Multiplexer(int channel) => channel switch
    {
        0 => InputMultiplexer.AIN0,
        1 => InputMultiplexer.AIN1,
        2 => InputMultiplexer.AIN2,
        3 => InputMultiplexer.AIN3,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public static MeasuringRange Range(string gain) =>
        double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v switch
        {
            >= 6 => MeasuringRange.FS6144,
            >= 4 => MeasuringRange.FS4096,
            >= 2 => MeasuringRange.FS2048,
            >= 1 => MeasuringRange.FS1024,
            >= 0.5 => MeasuringRange.FS0512,
            _ => MeasuringRange.FS0256,
        } : MeasuringRange.FS4096;

    private sealed class Ads1115Sensor(Ads1115 adc) : ISensor
    {
        public ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<RawReading> result = [new(ChannelKey, adc.ReadVoltage().Volts)];
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            adc.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class Bme280Plugin : ISensorPlugin
{
    public const string Id = "bme280";
    public const int DefaultAddress = 0x76;

    public string PluginId => Id;
    public string DisplayName => "BME280 environment sensor";
    public string Description => "Bosch BME280 on I²C: air temperature, relative humidity and barometric pressure.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("i2cBus", "integer", "I²C bus number.", 1)
        .Prop("address", "string", "I²C address (0x76 or 0x77).", "0x76")
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) =>
    [
        new("temperature", Metrics.AirTemperatureC, "°C"),
        new("humidity", Metrics.AirHumidityPct, "%"),
        new("pressure", Metrics.AirPressureHpa, "hPa"),
    ];

    public ISensor Create(PluginContext context)
    {
        var device = I2cDevice.Create(new I2cConnectionSettings(Settings.Int(context.Settings, "i2cBus", 1), Settings.Int(context.Settings, "address", DefaultAddress)));
        try
        {
            var bme = new Bme280(device)
            {
                TemperatureSampling = Sampling.LowPower,
                PressureSampling = Sampling.UltraLowPower,
                HumiditySampling = Sampling.Standard,
            };
            return new Bme280Sensor(bme);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private sealed class Bme280Sensor(Bme280 bme) : ISensor
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                bme.SetPowerMode(Bmx280PowerMode.Forced);
                await Task.Delay(bme.GetMeasurementDuration(), cancellationToken);
                var r = bme.Read();
                var readings = new List<RawReading>(3);
                if (r.Temperature is { } t) readings.Add(new("temperature", t.DegreesCelsius));
                if (r.Humidity is { } h) readings.Add(new("humidity", h.Percent));
                if (r.Pressure is { } p) readings.Add(new("pressure", p.Hectopascals));
                return readings;
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            bme.Dispose();
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
