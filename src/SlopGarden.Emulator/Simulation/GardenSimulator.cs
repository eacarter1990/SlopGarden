using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SlopGarden.Emulator.Simulation;

public sealed record OverrideView(double Value, DateTimeOffset? Until);

public sealed record SensorView(
    string Id, string Name, string Metric, string Unit, SignalModel Model, double Noise, int Decimals, double OffsetDecaySec,
    double Value, OverrideView? Override, DateTimeOffset Time);

public sealed record SensorValue(string Id, double Value, string Metric, string Unit, DateTimeOffset Time);

public sealed record ActuatorHistoryEntry(DateTimeOffset Time, bool On, double? Level, string? Source);

/// <param name="Stalled">Fault injection: the actuator reports on but has no physical effect (dead pump, clog).</param>
public sealed record ActuatorView(string Id, string Name, string Kind, bool On, double? Level, DateTimeOffset Since, bool Stalled, IReadOnlyList<ActuatorHistoryEntry> History);

public sealed record CameraView(string Id, string Name, string Image, bool CustomImage, string SnapshotUrl);

public sealed record ScenarioView(
    string Name, double TimeScale, DateTimeOffset SimulatedTime,
    IReadOnlyList<SensorView> Sensors, IReadOnlyList<ActuatorView> Actuators, IReadOnlyList<CameraView> Cameras,
    IReadOnlyList<CouplingDefinition> Couplings);

public sealed record SensorUpdate(string? Name, string? Metric, string? Unit, SignalModel? Model, double? Noise, int? Decimals, double? OffsetDecaySec);

/// <summary>Thread-safe in-memory physics of the emulated garden.</summary>
public sealed class GardenSimulator
{
    private const int MaxHistory = 200;
    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _timeZone;
    private readonly Random _random;
    private readonly Dictionary<string, SensorRuntime> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActuatorRuntime> _actuators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CameraRuntime> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CouplingDefinition> _couplings = [];
    private string _name = "";
    private double _timeScale = 1;
    private DateTimeOffset _simTime;

    public GardenSimulator(ScenarioDocument scenario, TimeProvider? time = null, TimeZoneInfo? timeZone = null, Random? random = null)
    {
        _time = time ?? TimeProvider.System;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _random = random ?? new Random();
        Load(scenario);
    }

    /// <summary>Raised (outside the lock) when definitions change and the scenario file should be saved.</summary>
    public event Action? DefinitionsChanged;

    public double TimeScale { get { lock (_gate) return _timeScale; } }

    public DateTimeOffset SimulatedTime { get { lock (_gate) return _simTime; } }

    public void Load(ScenarioDocument scenario)
    {
        var copy = Clone(scenario);
        lock (_gate)
        {
            _name = copy.Name;
            _timeScale = copy.TimeScale > 0 ? copy.TimeScale : 1;
            _simTime = _time.GetUtcNow();
            _sensors.Clear();
            _actuators.Clear();
            _cameras.Clear();
            _couplings.Clear();
            foreach (var s in copy.Sensors) _sensors[s.Id] = new SensorRuntime(s);
            foreach (var a in copy.Actuators) _actuators[a.Id] = new ActuatorRuntime(a, _time.GetUtcNow());
            foreach (var c in copy.Cameras) _cameras[c.Id] = new CameraRuntime(c);
            _couplings.AddRange(copy.Couplings);
        }
        DefinitionsChanged?.Invoke();
    }

    public ScenarioDocument Export()
    {
        lock (_gate)
        {
            return Clone(new ScenarioDocument
            {
                Name = _name,
                TimeScale = _timeScale,
                Sensors = _sensors.Values.Select(s => s.Definition).ToList(),
                Actuators = _actuators.Values.Select(a => a.Definition).ToList(),
                Cameras = _cameras.Values.Select(c => c.Definition).ToList(),
                Couplings = _couplings.ToList(),
            });
        }
    }

    public void SetTimeScale(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor > 100_000)
            throw new ArgumentException("factor must be between 0 (exclusive) and 100000.");
        lock (_gate) _timeScale = factor;
        DefinitionsChanged?.Invoke();
    }

    /// <summary>Advances physics by a real-time interval (multiplied by the time scale).</summary>
    public void Advance(TimeSpan realElapsed)
    {
        lock (_gate) AdvanceCore(realElapsed.TotalSeconds * _timeScale);
    }

    /// <summary>Advances physics by simulated seconds regardless of the time scale.</summary>
    public void AdvanceSimulated(double simSeconds)
    {
        lock (_gate) AdvanceCore(simSeconds);
    }

    private void AdvanceCore(double dt)
    {
        if (dt <= 0) return;
        _simTime = _simTime.AddSeconds(dt);
        foreach (var s in _sensors.Values)
        {
            var m = s.Definition.Model;
            switch (m.Type)
            {
                case SignalModelTypes.Drying:
                    s.State -= (m.RatePerHour ?? 0) * dt / 3600;
                    break;
                case SignalModelTypes.RandomWalk:
                    s.State += Gaussian() * (m.Step ?? 0.1) * Math.Sqrt(dt);
                    break;
                case SignalModelTypes.Sine:
                    s.Offset *= Math.Exp(-dt / Math.Max(1, s.Definition.OffsetDecaySec));
                    break;
            }
        }
        foreach (var c in _couplings)
        {
            if (c.Mode == CouplingModes.WhileOn || !IsActive(c, out var a) || !_sensors.TryGetValue(c.SensorId, out var s)) continue;
            var delta = c.RatePerMinute * dt / 60 * Math.Clamp(a.Level ?? 1, 0, 1);
            if (s.Definition.Model.Type == SignalModelTypes.Sine) s.Offset += delta;
            else s.State += delta;
        }
        foreach (var s in _sensors.Values) s.State = ClampState(s.Definition.Model, s.State);
    }

    private static double ClampState(SignalModel m, double v)
    {
        var lo = m.Floor ?? double.NegativeInfinity;
        var hi = m.Ceiling ?? double.PositiveInfinity;
        if (m.Type == SignalModelTypes.RandomWalk)
        {
            lo = Math.Max(lo, m.Min ?? double.NegativeInfinity);
            hi = Math.Min(hi, m.Max ?? double.PositiveInfinity);
        }
        return Math.Clamp(v, lo, Math.Max(lo, hi));
    }

    private double BaseValue(SensorRuntime s)
    {
        var m = s.Definition.Model;
        double v;
        if (m.Type == SignalModelTypes.Sine)
        {
            var min = m.Min ?? 0;
            var max = m.Max ?? 1;
            var period = m.PeriodSec is > 0 ? m.PeriodSec.Value : 86_400;
            var t = _simTime.ToUnixTimeMilliseconds() / 1000.0 + _timeZone.GetUtcOffset(_simTime).TotalSeconds;
            v = (min + max) / 2 + (max - min) / 2 * Math.Sin(2 * Math.PI * (t - (m.Phase ?? 0)) / period) + s.Offset;
        }
        else
        {
            v = s.State;
        }
        if (WhileOnValue(s) is { } driven) v = driven;
        return Math.Clamp(v, m.Floor ?? double.NegativeInfinity, Math.Max(m.Floor ?? double.NegativeInfinity, m.Ceiling ?? double.PositiveInfinity));
    }

    private bool IsActive(CouplingDefinition c, [NotNullWhen(true)] out ActuatorRuntime? actuator) =>
        _actuators.TryGetValue(c.ActuatorId, out actuator) && actuator.On && !actuator.Stalled;

    /// <summary>Sum of the values of the sensor's active <c>while_on</c> couplings, or null when none is active.</summary>
    private double? WhileOnValue(SensorRuntime s)
    {
        double? sum = null;
        foreach (var c in _couplings)
        {
            if (c.Mode != CouplingModes.WhileOn || !string.Equals(c.SensorId, s.Definition.Id, StringComparison.OrdinalIgnoreCase) || !IsActive(c, out var a)) continue;
            sum = (sum ?? 0) + (c.Value ?? 0) * Math.Clamp(a.Level ?? 1, 0, 1);
        }
        return sum;
    }

    private double CurrentValue(SensorRuntime s, DateTimeOffset now)
    {
        if (s.Override is { } o)
        {
            if (o.Until is null || o.Until > now) return o.Value;
            s.Override = null;
        }
        var m = s.Definition.Model;
        var v = BaseValue(s) + Gaussian() * s.Definition.Noise;
        v = Math.Clamp(v, m.Floor ?? double.NegativeInfinity, Math.Max(m.Floor ?? double.NegativeInfinity, m.Ceiling ?? double.PositiveInfinity));
        return Math.Round(v, Math.Clamp(s.Definition.Decimals, 0, 10));
    }

    /// <summary>Noise-free model value (with couplings), for tests and diagnostics.</summary>
    public double TrueValue(string sensorId)
    {
        lock (_gate) return BaseValue(GetSensor(sensorId));
    }

    public SensorValue ReadValue(string sensorId)
    {
        lock (_gate)
        {
            var s = GetSensor(sensorId);
            var now = _time.GetUtcNow();
            return new SensorValue(s.Definition.Id, CurrentValue(s, now), s.Definition.Metric, s.Definition.Unit, now);
        }
    }

    public IReadOnlyList<SensorView> ListSensors()
    {
        lock (_gate) return _sensors.Values.Select(ViewOf).ToList();
    }

    public SensorView GetSensorView(string id)
    {
        lock (_gate) return ViewOf(GetSensor(id));
    }

    private SensorView ViewOf(SensorRuntime s)
    {
        var now = _time.GetUtcNow();
        var d = s.Definition;
        var value = CurrentValue(s, now);
        return new SensorView(d.Id, d.Name, d.Metric, d.Unit, Clone(d.Model), d.Noise, d.Decimals, d.OffsetDecaySec, value,
            s.Override is { } o ? new OverrideView(o.Value, o.Until) : null, now);
    }

    public SensorView CreateSensor(SensorDefinition definition)
    {
        Validate(definition);
        lock (_gate)
        {
            if (_sensors.ContainsKey(definition.Id)) throw new InvalidOperationException($"Sensor '{definition.Id}' already exists.");
            _sensors[definition.Id] = new SensorRuntime(Clone(definition));
        }
        DefinitionsChanged?.Invoke();
        return GetSensorView(definition.Id);
    }

    public SensorView UpdateSensor(string id, SensorUpdate update)
    {
        lock (_gate)
        {
            var s = GetSensor(id);
            var d = Clone(s.Definition);
            if (update.Name is not null) d.Name = update.Name;
            if (update.Metric is not null) d.Metric = update.Metric;
            if (update.Unit is not null) d.Unit = update.Unit;
            if (update.Noise is not null) d.Noise = Math.Max(0, update.Noise.Value);
            if (update.Decimals is not null) d.Decimals = update.Decimals.Value;
            if (update.OffsetDecaySec is not null) d.OffsetDecaySec = update.OffsetDecaySec.Value;
            if (update.Model is not null) d.Model = Clone(update.Model);
            Validate(d);
            var modelChanged = update.Model is not null;
            s.Definition = d;
            if (modelChanged) s.ResetState();
        }
        DefinitionsChanged?.Invoke();
        return GetSensorView(id);
    }

    public void DeleteSensor(string id)
    {
        lock (_gate)
        {
            if (!_sensors.Remove(id)) throw new KeyNotFoundException($"Sensor '{id}' not found.");
            _couplings.RemoveAll(c => string.Equals(c.SensorId, id, StringComparison.OrdinalIgnoreCase));
        }
        DefinitionsChanged?.Invoke();
    }

    public SensorView SetOverride(string id, double value, double? durationSec)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("value must be a finite number.");
        lock (_gate)
        {
            var s = GetSensor(id);
            s.Override = new OverrideState(value, durationSec is > 0 ? _time.GetUtcNow().AddSeconds(durationSec.Value) : null);
            return ViewOf(s);
        }
    }

    public SensorView ClearOverride(string id)
    {
        lock (_gate)
        {
            var s = GetSensor(id);
            s.Override = null;
            return ViewOf(s);
        }
    }

    public IReadOnlyList<ActuatorView> ListActuators()
    {
        lock (_gate) return _actuators.Values.Select(a => ViewOf(a, includeHistory: false)).ToList();
    }

    public ActuatorView GetActuator(string id)
    {
        lock (_gate) return ViewOf(GetActuatorRuntime(id), includeHistory: true);
    }

    public ActuatorView SetActuatorState(string id, bool on, double? level, string? source)
    {
        if (level is not null && (!double.IsFinite(level.Value) || level < 0 || level > 1))
            throw new ArgumentException("level must be between 0 and 1.");
        lock (_gate)
        {
            var a = GetActuatorRuntime(id);
            var now = _time.GetUtcNow();
            if (a.On != on || a.Level != level) a.Since = now;
            a.On = on;
            a.Level = on ? level : null;
            a.History.Add(new ActuatorHistoryEntry(now, on, a.Level, source));
            if (a.History.Count > MaxHistory) a.History.RemoveRange(0, a.History.Count - MaxHistory);
            return ViewOf(a, includeHistory: false);
        }
    }

    /// <summary>Injects or clears a stall: the actuator keeps reporting its state, but its couplings stop working.</summary>
    public ActuatorView SetActuatorFault(string id, bool stalled)
    {
        lock (_gate)
        {
            var a = GetActuatorRuntime(id);
            if (a.Stalled != stalled)
            {
                a.Stalled = stalled;
                a.History.Add(new ActuatorHistoryEntry(_time.GetUtcNow(), a.On, a.Level, stalled ? "fault: stalled" : "fault cleared"));
                if (a.History.Count > MaxHistory) a.History.RemoveRange(0, a.History.Count - MaxHistory);
            }
            return ViewOf(a, includeHistory: false);
        }
    }

    private static ActuatorView ViewOf(ActuatorRuntime a, bool includeHistory) =>
        new(a.Definition.Id, a.Definition.Name, a.Definition.Kind, a.On, a.Level, a.Since, a.Stalled, includeHistory ? a.History.ToList() : []);

    public IReadOnlyList<CameraView> ListCameras()
    {
        lock (_gate) return _cameras.Values.Select(ViewOf).ToList();
    }

    public CameraView GetCamera(string id)
    {
        lock (_gate) return ViewOf(GetCameraRuntime(id));
    }

    private static CameraView ViewOf(CameraRuntime c) =>
        new(c.Definition.Id, c.Definition.Name, c.Definition.Image, c.CustomImage is not null, $"/api/cameras/{Uri.EscapeDataString(c.Definition.Id)}/snapshot");

    /// <summary>Returns the camera definition and its replacement image, if one was uploaded.</summary>
    public (CameraDefinition Definition, byte[]? CustomImage) GetCameraSource(string id)
    {
        lock (_gate)
        {
            var c = GetCameraRuntime(id);
            return (c.Definition, c.CustomImage);
        }
    }

    public void SetCameraImage(string id, byte[] image)
    {
        lock (_gate) GetCameraRuntime(id).CustomImage = image;
    }

    public ScenarioView View()
    {
        lock (_gate)
        {
            return new ScenarioView(_name, _timeScale, _simTime,
                _sensors.Values.Select(ViewOf).ToList(),
                _actuators.Values.Select(a => ViewOf(a, includeHistory: false)).ToList(),
                _cameras.Values.Select(ViewOf).ToList(),
                _couplings.Select(Clone).ToList());
        }
    }

    private SensorRuntime GetSensor(string id) =>
        _sensors.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"Sensor '{id}' not found.");

    private ActuatorRuntime GetActuatorRuntime(string id) =>
        _actuators.TryGetValue(id, out var a) ? a : throw new KeyNotFoundException($"Actuator '{id}' not found.");

    private CameraRuntime GetCameraRuntime(string id) =>
        _cameras.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"Camera '{id}' not found.");

    private static void Validate(SensorDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("id is required and may only contain letters, digits, '-', '_' and '.'.");
        if (string.IsNullOrWhiteSpace(d.Metric)) throw new ArgumentException("metric is required.");
        if (!SignalModelTypes.All.Contains(d.Model.Type))
            throw new ArgumentException($"model.type must be one of: {string.Join(", ", SignalModelTypes.All)}.");
        if (d.Model.Type == SignalModelTypes.Sine && d.Model.PeriodSec is <= 0) throw new ArgumentException("model.periodSec must be positive.");
        if (string.IsNullOrWhiteSpace(d.Name)) d.Name = d.Id;
    }

    private double Gaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CloneOptions), CloneOptions)!;

    private sealed record OverrideState(double Value, DateTimeOffset? Until);

    private sealed class SensorRuntime
    {
        public SensorRuntime(SensorDefinition definition)
        {
            Definition = definition;
            ResetState();
        }

        public SensorDefinition Definition { get; set; }
        public double State { get; set; }
        public double Offset { get; set; }
        public OverrideState? Override { get; set; }

        public void ResetState()
        {
            var m = Definition.Model;
            Offset = 0;
            State = ClampState(m, m.Type switch
            {
                SignalModelTypes.Drying => m.Start ?? 50,
                SignalModelTypes.RandomWalk => m.Start ?? ((m.Min ?? 0) + (m.Max ?? 100)) / 2,
                _ => m.Value ?? 0,
            });
        }
    }

    private sealed class ActuatorRuntime(ActuatorDefinition definition, DateTimeOffset since)
    {
        public ActuatorDefinition Definition { get; } = definition;
        public bool On { get; set; }
        public double? Level { get; set; }
        public DateTimeOffset Since { get; set; } = since;
        public bool Stalled { get; set; }
        public List<ActuatorHistoryEntry> History { get; } = [];
    }

    private sealed class CameraRuntime(CameraDefinition definition)
    {
        public CameraDefinition Definition { get; } = definition;
        public byte[]? CustomImage { get; set; }
    }
}
