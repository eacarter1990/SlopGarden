using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Infrastructure;

/// <summary>An auto-reset wake-up signal for background loops.</summary>
public sealed class AsyncSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Waits for <see cref="Set"/> or the timeout; returns true when signalled.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

public static class TimeZones
{
    public static TimeZoneInfo Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>The UTC interval of the local calendar day containing <paramref name="utc"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) LocalDay(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var localDate = TimeZoneInfo.ConvertTime(utc, zone).Date;
        return (LocalMidnightUtc(localDate, zone), LocalMidnightUtc(localDate.AddDays(1), zone));
    }

    private static DateTimeOffset LocalMidnightUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        // Some zones switch DST at midnight, which makes 00:00 nonexistent.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static string LocalIanaId()
    {
        var id = TimeZoneInfo.Local.Id;
        if (OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return iana;
        return id == "Local" ? "UTC" : id;
    }
}

public static class ClientInfo
{
    public static string Version { get; } =
        typeof(ClientInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ClientInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static string Os => RuntimeInformation.OSDescription;

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static string Hostname(ClientOptions options) =>
        string.IsNullOrWhiteSpace(options.Name) ? Environment.MachineName : options.Name;

    public static DeviceEnvironmentInfo DescribeEnvironment(ClientOptions options, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var zone = TimeZoneInfo.Local;
        var offset = zone.GetUtcOffset(now);
        return new DeviceEnvironmentInfo(TimeZones.LocalIanaId(), offset.TotalMinutes, now.ToOffset(offset),
            options.Location?.Latitude, options.Location?.Longitude, options.Location?.Label);
    }

    /// <summary>The local address used to reach <paramref name="server"/>, as a URL of the local API.</summary>
    public static string? LocalEndpoint(Uri server, int localApiPort)
    {
        if (localApiPort <= 0) return null;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var host = server.IsLoopback ? IPAddress.Loopback : Dns.GetHostAddresses(server.DnsSafeHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (host is null) return null;
            socket.Connect(host, server.Port > 0 ? server.Port : 80);
            return socket.LocalEndPoint is IPEndPoint ep ? $"http://{ep.Address}:{localApiPort}" : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

public static class ClientJson
{
    public static readonly JsonSerializerOptions Options = SlopJson.Create();

    public static readonly JsonSerializerOptions Indented = new(SlopJson.Create()) { WriteIndented = true };

    /// <summary>Writes a file atomically (temp file + rename) so a crash never leaves a truncated file.</summary>
    public static void WriteFileAtomic(string path, string content, bool privateFile = false)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (privateFile && !OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
