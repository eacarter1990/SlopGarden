using SlopGarden.Client.Sensors;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Tests.Sensors;

public sealed class CalibrationTests
{
    private static ChannelConfig Channel(CalibrationConfig? calibration, double? min = null, double? max = null) =>
        new(Guid.NewGuid(), "value", Metrics.SoilMoisturePct, "%", calibration, min, max);

    [Fact]
    public void No_calibration_passes_raw_values_through()
    {
        Assert.Equal(12.5, Calibration.Apply(null, 12.5));
        Assert.Equal(12.5, Calibration.Apply(new CalibrationConfig(CalibrationTypes.None), 12.5));
        Assert.True(Calibration.IsIdentity(new CalibrationConfig("")));
    }

    [Fact]
    public void Linear_applies_scale_then_offset()
    {
        Assert.Equal(21.5, Calibration.Apply(new CalibrationConfig(CalibrationTypes.Linear, Scale: 0.5, Offset: 1.5), 40), 9);
        Assert.Equal(40, Calibration.Apply(new CalibrationConfig(CalibrationTypes.Linear), 40), 9);
    }

    [Fact]
    public void Two_point_maps_a_capacitive_probe_from_dry_to_wet()
    {
        // Typical capacitive probe: 2.8 V in air (0 %), 1.2 V in water (100 %); the mapping is inverted.
        var c = new CalibrationConfig(CalibrationTypes.TwoPoint, RawLow: 2.8, ValueLow: 0, RawHigh: 1.2, ValueHigh: 100);
        Assert.Equal(0, Calibration.Apply(c, 2.8), 9);
        Assert.Equal(100, Calibration.Apply(c, 1.2), 9);
        Assert.Equal(50, Calibration.Apply(c, 2.0), 9);
        Assert.Equal(-12.5, Calibration.Apply(c, 3.0), 9);
    }

    [Fact]
    public void Two_point_clamp_limits_to_the_value_range()
    {
        var c = new CalibrationConfig(CalibrationTypes.TwoPoint, RawLow: 2.8, ValueLow: 0, RawHigh: 1.2, ValueHigh: 100, Clamp: true);
        Assert.Equal(0, Calibration.Apply(c, 3.3));
        Assert.Equal(100, Calibration.Apply(c, 0.5));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, null)]
    public void Two_point_with_missing_or_equal_points_is_an_error(double rawLow, double? rawHigh)
    {
        var c = new CalibrationConfig(CalibrationTypes.TwoPoint, RawLow: rawLow, ValueLow: 0, RawHigh: rawHigh, ValueHigh: 100);
        Assert.Throws<InvalidOperationException>(() => Calibration.Apply(c, 1));
    }

    [Fact]
    public void Polynomial_uses_ascending_coefficients()
    {
        // 1 + 2x + 3x² at x = 2 → 17
        Assert.Equal(17, Calibration.Apply(new CalibrationConfig(CalibrationTypes.Polynomial, Coefficients: [1, 2, 3]), 2), 9);
        Assert.Equal(0, Calibration.Apply(new CalibrationConfig(CalibrationTypes.Polynomial), 5));
    }

    [Fact]
    public void Unknown_type_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => Calibration.Apply(new CalibrationConfig("cubic_spline"), 1));
    }

    [Fact]
    public void Process_flags_values_outside_the_valid_range()
    {
        var channel = Channel(new CalibrationConfig(CalibrationTypes.Linear, Scale: 100), min: 0, max: 100);
        Assert.Equal((50, ReadingQuality.Good), Calibration.Process(channel, 0.5, ReadingQuality.Good));
        Assert.Equal((120, ReadingQuality.OutOfRange), Calibration.Process(channel, 1.2, ReadingQuality.Good));
        Assert.Equal((-10, ReadingQuality.OutOfRange), Calibration.Process(channel, -0.1, ReadingQuality.Good));
    }

    [Fact]
    public void Process_keeps_the_plugin_quality_and_flags_non_finite_values()
    {
        var channel = Channel(null, 0, 100);
        Assert.Equal(ReadingQuality.Suspect, Calibration.Process(channel, 50, ReadingQuality.Suspect).Quality);
        Assert.Equal(ReadingQuality.Error, Calibration.Process(channel, double.NaN, ReadingQuality.Good).Quality);
        var divide = Channel(new CalibrationConfig(CalibrationTypes.Linear, Scale: double.PositiveInfinity));
        Assert.Equal(ReadingQuality.Error, Calibration.Process(divide, 1, ReadingQuality.Good).Quality);
    }

    [Fact]
    public void Process_without_bounds_never_flags_out_of_range()
    {
        Assert.Equal(ReadingQuality.Good, Calibration.Process(Channel(null), 1e9, ReadingQuality.Good).Quality);
    }
}
