namespace SlopGarden.Contracts;

/// <summary>Compares the version a device reported with the release the server is publishing.</summary>
public static class ClientVersions
{
    public static bool IsSame(string? running, string? latest) =>
        string.Equals(Normalize(running), Normalize(latest), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a release is configured and the device is not already on it (and is not newer).</summary>
    public static bool IsOutdated(string? running, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest) || IsSame(running, latest)) return false;
        if (TryParts(running, out var current) && TryParts(latest, out var published)) return Compare(current, published) < 0;
        return true;
    }

    public static string Normalize(string? version) => (version ?? "").Trim().TrimStart('v', 'V');

    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static bool TryParts(string? version, out int[] parts)
    {
        parts = [];
        var core = Normalize(version).Split('+', '-')[0];
        if (core.Length == 0) return false;
        var raw = core.Split('.');
        var parsed = new int[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            if (!int.TryParse(raw[i], out parsed[i])) return false;
        }
        parts = parsed;
        return true;
    }
}
