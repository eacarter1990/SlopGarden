using Microsoft.EntityFrameworkCore;
using Npgsql;
using SlopGarden.Server.Ai.Tools;
using SlopGarden.Server.Data;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

public sealed class ReadingsService(SlopDbContext db)
{
    private static readonly int[] NiceBuckets = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];

    public static int AutoBucket(double hours) => NiceBuckets.FirstOrDefault(b => hours * 60 / b <= 240, 1440);

    public async Task<List<ChannelSeries>> SeriesAsync(Guid? channelId, Guid? sensorId, double hours, int? bucketMinutes, CancellationToken ct)
    {
        hours = Math.Clamp(hours, 0.05, 24 * 90);
        var q = db.SensorChannels.AsNoTracking().Include(c => c.Sensor).AsQueryable();
        if (channelId is { } cid) q = q.Where(c => c.Id == cid);
        else if (sensorId is { } sid) q = q.Where(c => c.SensorId == sid);
        else throw AppException.Bad("channelId or sensorId is required");
        var channels = await q.ToListAsync(ct);
        if (channels.Count == 0) throw AppException.NotFound("Channel");

        var bucket = Math.Clamp(bucketMinutes ?? AutoBucket(hours), 1, 1440);
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var result = new List<ChannelSeries>();
        foreach (var c in channels)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT date_bin(make_interval(mins => @b), time, TIMESTAMPTZ '2000-01-01') AS t, avg(value), min(value), max(value), count(*)::int " +
                "FROM readings WHERE channel_id = @c AND time >= @s GROUP BY 1 ORDER BY 1", conn);
            cmd.Parameters.AddWithValue("b", bucket);
            cmd.Parameters.AddWithValue("c", c.Id);
            cmd.Parameters.AddWithValue("s", since.UtcDateTime);
            var points = new List<ReadingPoint>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new ReadingPoint(new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero), Math.Round(reader.GetDouble(1), 3),
                    reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4)));
            }
            result.Add(new ChannelSeries(c.Id, c.SensorId, c.Sensor!.Name, c.Metric, c.Unit, bucket, points));
        }
        return result;
    }
}

/// <summary>The URL clients should use to reach this server (configured, or derived from the LAN address).</summary>
public sealed class ServerUrlProvider(IConfiguration config, Microsoft.AspNetCore.Hosting.Server.IServer server)
{
    public string Get()
    {
        var configured = config[$"{ServerOptions.Section}:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
        var first = addresses.FirstOrDefault(a => a.StartsWith("http://")) ?? addresses.FirstOrDefault() ?? "http://localhost:5080";
        var uri = new Uri(first.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"));
        var host = uri.Host is "localhost" or "127.0.0.1" ? LanAddress() ?? uri.Host : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }

    public static string? LanAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var addr = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (addr is not null) return addr.Address.ToString();
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        return null;
    }
}
