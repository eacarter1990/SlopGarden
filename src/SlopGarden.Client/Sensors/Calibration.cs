using SlopGarden.Contracts;

namespace SlopGarden.Client.Sensors;

public static class CalibrationTypes
{
    public const string None = "none";
    public const string Linear = "linear";
    public const string TwoPoint = "two_point";
    public const string Polynomial = "polynomial";
}

public static class Calibration
{
    public static bool IsIdentity(CalibrationConfig? c) => c is null || c.Type is CalibrationTypes.None or "";

    /// <summary>Applies the calibration. <see cref="CalibrationConfig.Clamp"/> limits two_point results to
    /// [ValueLow, ValueHigh]; it has no effect on the unbounded linear and polynomial mappings.</summary>
    public static double Apply(CalibrationConfig? c, double raw)
    {
        if (IsIdentity(c)) return raw;
        switch (c!.Type)
        {
            case CalibrationTypes.Linear:
                return raw * (c.Scale ?? 1) + (c.Offset ?? 0);
            case CalibrationTypes.TwoPoint:
                if (c.RawLow is not { } rl || c.RawHigh is not { } rh || c.ValueLow is not { } vl || c.ValueHigh is not { } vh || rh == rl)
                    throw new InvalidOperationException("two_point calibration needs rawLow, rawHigh, valueLow and valueHigh with rawLow != rawHigh.");
                var v = vl + (raw - rl) * (vh - vl) / (rh - rl);
                return c.Clamp ? Math.Clamp(v, Math.Min(vl, vh), Math.Max(vl, vh)) : v;
            case CalibrationTypes.Polynomial:
                var coefficients = c.Coefficients ?? [];
                var result = 0.0;
                for (var i = coefficients.Count - 1; i >= 0; i--) result = result * raw + coefficients[i];
                return result;
            default:
                throw new InvalidOperationException($"Unknown calibration type '{c.Type}'.");
        }
    }

    /// <summary>Calibrates a raw value and classifies it against the channel's valid range.</summary>
    public static (double Value, short Quality) Process(ChannelConfig channel, double raw, short rawQuality)
    {
        var value = Apply(channel.Calibration, raw);
        if (!double.IsFinite(value)) return (value, ReadingQuality.Error);
        if (rawQuality != ReadingQuality.Good) return (value, rawQuality);
        if (value < channel.MinValid || value > channel.MaxValid) return (value, ReadingQuality.OutOfRange);
        return (value, ReadingQuality.Good);
    }
}
