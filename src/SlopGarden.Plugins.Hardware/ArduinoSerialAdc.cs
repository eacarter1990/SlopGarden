using System.Globalization;
using System.IO.Ports;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>Line protocol of the Arduino ADC bridge: <c>A0=512,A1=300</c> (see docs/hardware/arduino-adc-bridge.md).</summary>
public static class ArduinoAdcProtocol
{
    public static IReadOnlyDictionary<string, double> Parse(string line)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') return result;
        foreach (var token in trimmed.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            if (double.TryParse(token[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                result[token[..eq].Trim()] = value;
        }
        return result;
    }
}

/// <summary>Shared reader per serial port so several sensors can use different channels of one Arduino.</summary>
internal sealed class SerialAdcPort : IDisposable
{
    private static readonly Dictionary<string, (SerialAdcPort Port, int Refs)> Ports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock PortsGate = new();

    private readonly Dictionary<string, (double Value, DateTimeOffset Time)> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _portName;
    private readonly int _baud;
    private readonly ILogger _logger;
    private string? _lastError;

    private SerialAdcPort(string portName, int baud, ILogger logger)
    {
        _portName = portName;
        _baud = baud;
        _logger = logger;
        _ = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
    }

    public static SerialAdcPort Acquire(string portName, int baud, ILogger logger)
    {
        lock (PortsGate)
        {
            if (Ports.TryGetValue(portName, out var entry))
            {
                Ports[portName] = (entry.Port, entry.Refs + 1);
                return entry.Port;
            }
            var port = new SerialAdcPort(portName, baud, logger);
            Ports[portName] = (port, 1);
            return port;
        }
    }

    public static void Release(SerialAdcPort port)
    {
        lock (PortsGate)
        {
            if (!Ports.TryGetValue(port._portName, out var entry) || entry.Port != port) return;
            if (entry.Refs > 1)
            {
                Ports[port._portName] = (port, entry.Refs - 1);
                return;
            }
            Ports.Remove(port._portName);
        }
        port.Dispose();
    }

    public (double Value, DateTimeOffset Time)? Latest(string channel)
    {
        lock (_gate) return _latest.TryGetValue(channel, out var v) ? v : null;
    }

    public string? LastError => _lastError;

    private void ReadLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(_portName, _baud) { ReadTimeout = 3000, NewLine = "\n", DtrEnable = true };
                port.Open();
                _lastError = null;
                _logger.LogInformation("Arduino ADC bridge opened on {SerialPort} at {Baud} baud", _portName, _baud);
                while (!_cts.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        line = port.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    var values = ArduinoAdcProtocol.Parse(line);
                    if (values.Count == 0) continue;
                    var now = DateTimeOffset.UtcNow;
                    lock (_gate)
                    {
                        foreach (var (k, v) in values) _latest[k] = (v, now);
                    }
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                if (_lastError != ex.Message) _logger.LogWarning(ex, "Arduino ADC bridge on {SerialPort} failed; retrying", _portName);
                _lastError = ex.Message;
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}

public sealed class ArduinoSerialAdcPlugin : ISensorPlugin
{
    public const string Id = "arduino-serial-adc";
    public const string ChannelKey = "raw";

    public string PluginId => Id;
    public string DisplayName => "Arduino analog bridge (USB serial)";
    public string Description =>
        "Reads one analog input of an Arduino running the SlopGarden ADC bridge sketch (lines like 'A0=512,A1=300'). Calibrate 'raw' with two_point.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("port", "string", "Serial port, e.g. /dev/ttyUSB0, /dev/ttyACM0 or COM3.", required: true)
        .Prop("baud", "integer", "Baud rate.", 115200)
        .Prop("channel", "string", "Analog input name as printed by the sketch.", "A0", required: true)
        .Prop("maxAgeSec", "integer", "Fail the read when the newest value is older than this.", 10)
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) => [new(ChannelKey, "adc_raw", "counts")];

    public ISensor Create(PluginContext context)
    {
        var s = context.Settings;
        var port = Settings.String(s, "port");
        if (string.IsNullOrWhiteSpace(port)) throw new ArgumentException("Setting 'port' is required.");
        var reader = SerialAdcPort.Acquire(port, Settings.Int(s, "baud", 115200), context.Logger);
        return new Channel(reader, Settings.String(s, "channel", "A0"), TimeSpan.FromSeconds(Math.Max(1, Settings.Int(s, "maxAgeSec", 10))));
    }

    private sealed class Channel(SerialAdcPort port, string channel, TimeSpan maxAge) : ISensor
    {
        public ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            var latest = port.Latest(channel);
            if (latest is null || DateTimeOffset.UtcNow - latest.Value.Time > maxAge)
                throw new IOException($"No recent value for {channel}" + (port.LastError is { } e ? $": {e}" : "."));
            IReadOnlyList<RawReading> result = [new(ChannelKey, latest.Value.Value)];
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            SerialAdcPort.Release(port);
            return ValueTask.CompletedTask;
        }
    }
}
