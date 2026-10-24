using System.Device.Gpio;
using System.Text.Json.Nodes;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>Hall-effect pulse flow meter (YF-S201 and similar) on a GPIO pin. Counts falling edges and reports the
/// flow over a rolling window.</summary>
public sealed class GpioFlowMeterPlugin : ISensorPlugin
{
    public const string Id = "gpio-flow-meter";
    public const string ChannelKey = "flow";
    public const double DefaultPulsesPerLiter = 450;
    public const double DefaultWindowSec = 2;

    public string PluginId => Id;
    public string DisplayName => "GPIO pulse flow meter";
    public string Description => "Hall-effect flow meter (e.g. YF-S201) on a GPIO pin; counts pulses and reports L/min. Used to verify that pumps really move water.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("pin", "integer", "BCM GPIO number the sensor's signal wire is connected to, e.g. 27.", required: true)
        .Prop("pulsesPerLiter", "number", "Pulses per litre from the sensor's datasheet. YF-S201: 450 (F[Hz] = 7.5 × Q[L/min]).", DefaultPulsesPerLiter)
        .Prop("pullUp", "boolean", "Enable the internal pull-up resistor (the sensor output is open-collector).", true)
        .Prop("windowSec", "number", "Pulses are counted over this rolling window (seconds). Longer is smoother, shorter reacts faster.", DefaultWindowSec)
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings) =>
        [new(ChannelKey, Metrics.FlowLitersPerMin, Metrics.DefaultUnits[Metrics.FlowLitersPerMin])];

    /// <summary>Flow in L/min from <paramref name="pulses"/> counted over <paramref name="windowSec"/> seconds.</summary>
    public static double LitersPerMinute(long pulses, double windowSec, double pulsesPerLiter) =>
        windowSec <= 0 || pulsesPerLiter <= 0 ? 0 : pulses / windowSec / pulsesPerLiter * 60;

    public ISensor Create(PluginContext context)
    {
        var pin = Settings.Int(context.Settings, "pin", -1);
        if (pin < 0) throw new ArgumentException("Setting 'pin' is required.");
        var pulsesPerLiter = Settings.Double(context.Settings, "pulsesPerLiter", DefaultPulsesPerLiter);
        if (!(pulsesPerLiter > 0)) throw new ArgumentException("Setting 'pulsesPerLiter' must be positive.");
        var windowSec = Math.Clamp(Settings.Double(context.Settings, "windowSec", DefaultWindowSec), 0.2, 60);
        var mode = Settings.Bool(context.Settings, "pullUp", true) ? PinMode.InputPullUp : PinMode.Input;
        var time = context.Services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
        var meter = new FlowMeter(pin, pulsesPerLiter, new PulseWindow(TimeSpan.FromSeconds(windowSec), time));
        GpioShared.Use(c =>
        {
            if (!c.IsPinModeSupported(pin, mode)) mode = PinMode.Input;
            c.OpenPin(pin, mode);
            c.RegisterCallbackForPinValueChangedEvent(pin, PinEventTypes.Falling, meter.OnPulse);
        });
        return meter;
    }

    private sealed class FlowMeter(int pin, double pulsesPerLiter, PulseWindow window) : ISensor
    {
        public void OnPulse(object sender, PinValueChangedEventArgs args) => window.Add();

        public ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            var (pulses, seconds) = window.Count();
            IReadOnlyList<RawReading> result = [new(ChannelKey, Math.Round(LitersPerMinute(pulses, seconds, pulsesPerLiter), 3))];
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            GpioShared.Use(c =>
            {
                if (!c.IsPinOpen(pin)) return;
                c.UnregisterCallbackForPinValueChangedEvent(pin, OnPulse);
                c.ClosePin(pin);
            });
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Timestamps of recent pulses; counts those inside a rolling window. Thread-safe (pulses arrive on the GPIO event thread).</summary>
public sealed class PulseWindow(TimeSpan window, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Queue<long> _pulses = new();
    private readonly long _started = time.GetTimestamp();

    public void Add()
    {
        var now = time.GetTimestamp();
        lock (_gate)
        {
            _pulses.Enqueue(now);
            Prune(now);
        }
    }

    /// <summary>Pulses in the window and the window length in seconds (shorter right after start).</summary>
    public (long Pulses, double Seconds) Count()
    {
        var now = time.GetTimestamp();
        lock (_gate)
        {
            Prune(now);
            var seconds = Math.Min(window.TotalSeconds, time.GetElapsedTime(_started, now).TotalSeconds);
            return (_pulses.Count, seconds);
        }
    }

    private void Prune(long now)
    {
        while (_pulses.Count > 0 && time.GetElapsedTime(_pulses.Peek(), now) > window) _pulses.Dequeue();
    }
}
