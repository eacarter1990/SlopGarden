using SlopGarden.Server.Data;

namespace SlopGarden.Server.Services;

public sealed record EnvironmentContext(
    DateTimeOffset UtcNow,
    string LocalTime,
    string LocalDate,
    string DayOfWeek,
    string TimezoneId,
    string Environment,
    double? Latitude,
    double? Longitude,
    string? LocationLabel,
    string LocationSource,
    string Hemisphere,
    string Season,
    double? DaylightHours,
    string? Sunrise,
    string? Sunset,
    bool? IsDaylight);

/// <summary>Date/time/season/daylight context for a garden. The AI uses this for crop-aware decisions.</summary>
public sealed class EnvironmentService
{
    public EnvironmentContext For(Garden g, DateTimeOffset? at = null)
    {
        var utc = at ?? DateTimeOffset.UtcNow;
        var tz = TimeZones.Find(g.TimezoneId);
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        var south = g.Latitude is < 0;
        var season = Season(local.Month, south);
        double? daylight = null;
        string? sunrise = null, sunset = null;
        bool? isDay = null;
        if (g.Latitude is { } lat)
        {
            var (dl, noonUtcHours) = Daylight(lat, g.Longitude ?? 0, local.DayOfYear);
            daylight = Math.Round(dl, 2);
            if (dl > 0 && dl < 24)
            {
                var dayStartUtc = new DateTimeOffset(local.Date, TimeSpan.Zero);
                var rise = TimeZoneInfo.ConvertTime(dayStartUtc.AddHours(noonUtcHours - dl / 2), tz);
                var set = TimeZoneInfo.ConvertTime(dayStartUtc.AddHours(noonUtcHours + dl / 2), tz);
                sunrise = rise.ToString("HH:mm");
                sunset = set.ToString("HH:mm");
                isDay = local.TimeOfDay >= rise.TimeOfDay && local.TimeOfDay <= set.TimeOfDay;
            }
            else
            {
                isDay = dl >= 24;
            }
        }
        return new EnvironmentContext(utc, local.ToString("yyyy-MM-dd HH:mm zzz"), local.ToString("yyyy-MM-dd"), local.DayOfWeek.ToString(), tz.Id,
            g.Environment, g.Latitude, g.Longitude, g.LocationLabel, g.LocationSource, g.Latitude is null ? "unknown" : south ? "southern" : "northern",
            season, daylight, sunrise, sunset, isDay);
    }

    public static string Season(int month, bool southern)
    {
        var s = month switch
        {
            12 or 1 or 2 => "winter",
            3 or 4 or 5 => "spring",
            6 or 7 or 8 => "summer",
            _ => "autumn",
        };
        if (!southern) return s;
        return s switch { "winter" => "summer", "summer" => "winter", "spring" => "autumn", _ => "spring" };
    }

    /// <summary>Approximate day length (hours) and solar noon (UTC hours) using the NOAA declination approximation.</summary>
    public static (double Hours, double SolarNoonUtc) Daylight(double latitude, double longitude, int dayOfYear)
    {
        var decl = 23.44 * Math.Sin(2 * Math.PI / 365 * (dayOfYear - 81)) * Math.PI / 180;
        var latRad = latitude * Math.PI / 180;
        var cosH = (Math.Sin(-0.833 * Math.PI / 180) - Math.Sin(latRad) * Math.Sin(decl)) / (Math.Cos(latRad) * Math.Cos(decl));
        double hours = cosH switch
        {
            <= -1 => 24,
            >= 1 => 0,
            _ => 2 * Math.Acos(cosH) * 180 / Math.PI / 15,
        };
        var b = 2 * Math.PI * (dayOfYear - 81) / 364.0;
        var eot = 9.87 * Math.Sin(2 * b) - 7.53 * Math.Cos(b) - 1.5 * Math.Sin(b);
        var noonUtc = 12 - longitude / 15 - eot / 60;
        return (hours, noonUtc);
    }
}
