using System.Device.Gpio;
using System.Text.Json.Nodes;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>One process-wide GPIO controller; opening several controllers on the same chip conflicts on some drivers.</summary>
internal static class GpioShared
{
    private static readonly Lock Gate = new();
    private static GpioController? _controller;

    public static T Use<T>(Func<GpioController, T> action)
    {
        lock (Gate)
        {
            _controller ??= new GpioController();
            return action(_controller);
        }
    }

    public static void Use(Action<GpioController> action) => Use(c => { action(c); return true; });
}

public sealed class GpioDigitalInputPlugin : ISensorPlugin
{
    public const string Id = "gpio-digital-input";
    public const string ChannelKey = "value";

    public string PluginId => Id;
    public string DisplayName => "GPIO digital input";
    public string Description => "Reads a digital GPIO pin (rain sensor, float switch, door contact). Reports 1 when active, 0 otherwise.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("pin", "integer", "BCM GPIO number, e.g. 17.", required: true)
        .Prop("pullUp", "boolean", "Enable the internal pull-up resistor (otherwise pull-down).", false)
        .Prop("activeLow", "boolean", "Treat a low level as active (1).", false)
        .Prop("metric", "string", "Metric reported for the channel.", Metrics.Digital)
        .Build();

    public IReadOnlyList<ChannelDescriptor> DescribeChannels(JsonObject settings)
    {
        var metric = Settings.String(settings, "metric", Metrics.Digital);
        return [new(ChannelKey, metric, Metrics.DefaultUnits.GetValueOrDefault(metric, "bool"))];
    }

    public ISensor Create(PluginContext context)
    {
        var pin = Settings.Int(context.Settings, "pin", -1);
        if (pin < 0) throw new ArgumentException("Setting 'pin' is required.");
        var mode = Settings.Bool(context.Settings, "pullUp") ? PinMode.InputPullUp : PinMode.InputPullDown;
        GpioShared.Use(c =>
        {
            if (!c.IsPinModeSupported(pin, mode)) mode = PinMode.Input;
            c.OpenPin(pin, mode);
        });
        return new DigitalInput(pin, Settings.Bool(context.Settings, "activeLow"));
    }

    private sealed class DigitalInput(int pin, bool activeLow) : ISensor
    {
        public ValueTask<IReadOnlyList<RawReading>> ReadAsync(CancellationToken cancellationToken)
        {
            var high = GpioShared.Use(c => c.Read(pin) == PinValue.High);
            IReadOnlyList<RawReading> result = [new(ChannelKey, high ^ activeLow ? 1 : 0)];
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            GpioShared.Use(c => { if (c.IsPinOpen(pin)) c.ClosePin(pin); });
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class GpioRelayPlugin : IActuatorPlugin
{
    public const string Id = "gpio-relay";

    public string PluginId => Id;
    public string DisplayName => "GPIO relay";
    public string Description => "Drives a relay or MOSFET module from a GPIO pin (pumps, valves, lights, heaters).";
    public IReadOnlyList<string> SupportedKinds => ActuatorKinds.All;

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("pin", "integer", "BCM GPIO number, e.g. 23.", required: true)
        .Prop("activeLow", "boolean", "True for relay boards that switch on when the input is pulled LOW (most opto-isolated boards). Must be correct, otherwise the relay is on at startup.", required: true)
        .Build();

    public IActuator Create(PluginContext context)
    {
        var pin = Settings.Int(context.Settings, "pin", -1);
        if (pin < 0) throw new ArgumentException("Setting 'pin' is required.");
        var activeLow = Settings.Bool(context.Settings, "activeLow");
        GpioShared.Use(c => c.OpenPin(pin, PinMode.Output, RelayLevel.For(false, activeLow)));
        return new Relay(pin, activeLow);
    }

    private sealed class Relay(int pin, bool activeLow) : IActuator
    {
        public ValueTask SetStateAsync(bool on, double? level, CancellationToken cancellationToken)
        {
            GpioShared.Use(c => c.Write(pin, RelayLevel.For(on, activeLow)));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            GpioShared.Use(c =>
            {
                if (!c.IsPinOpen(pin)) return;
                c.Write(pin, RelayLevel.For(false, activeLow));
                c.ClosePin(pin);
            });
            return ValueTask.CompletedTask;
        }
    }
}

public static class RelayLevel
{
    public static PinValue For(bool on, bool activeLow) => on ^ activeLow ? PinValue.High : PinValue.Low;
}
