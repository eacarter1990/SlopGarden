using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlopGarden.Client.Infrastructure;

namespace SlopGarden.Client.Identity;

public sealed record DeviceIdentity(
    string HardwareId,
    string RegistrationSecret,
    Guid? DeviceId = null,
    string? DeviceToken = null,
    string? ServerUrl = null,
    string? DiscoveredVia = null);

public interface IIdentityStore
{
    DeviceIdentity Current { get; }
    DeviceIdentity Update(Func<DeviceIdentity, DeviceIdentity> change);
}

/// <summary>Persists the device identity (hardware id, registration secret, device token) in identity.json.</summary>
public sealed class IdentityStore : IIdentityStore
{
    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly ILogger<IdentityStore> _logger;
    private DeviceIdentity _current;

    public IdentityStore(ClientPaths paths, IOptions<ClientOptions> options, ILogger<IdentityStore> logger)
    {
        _file = paths.IdentityFile;
        _logger = logger;
        var stored = TryLoad(_file, logger);
        var hardwareId = !string.IsNullOrWhiteSpace(options.Value.HardwareId)
            ? options.Value.HardwareId.Trim()
            : stored?.HardwareId ?? HardwareIds.Compute();
        _current = stored is null
            ? new DeviceIdentity(hardwareId, NewSecret())
            : stored with { HardwareId = hardwareId };
        if (stored != _current) Save(_current);
    }

    public DeviceIdentity Current
    {
        get { lock (_gate) return _current; }
    }

    public DeviceIdentity Update(Func<DeviceIdentity, DeviceIdentity> change)
    {
        lock (_gate)
        {
            var next = change(_current);
            if (next == _current) return next;
            _current = next;
            Save(next);
            return next;
        }
    }

    public static void Delete(ClientPaths paths)
    {
        if (File.Exists(paths.IdentityFile)) File.Delete(paths.IdentityFile);
    }

    public static DeviceIdentity? TryLoad(string file, ILogger? logger = null)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var identity = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(file), ClientJson.Options);
            return identity is { HardwareId.Length: > 0, RegistrationSecret.Length: > 0 } ? identity : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger?.LogWarning(ex, "identity.json is unreadable; a new identity will be created");
            return null;
        }
    }

    private void Save(DeviceIdentity identity)
    {
        try
        {
            ClientJson.WriteFileAtomic(_file, JsonSerializer.Serialize(identity, ClientJson.Indented), privateFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {IdentityFile}", _file);
        }
    }

    private static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

public static class HardwareIds
{
    /// <summary>A stable id derived from the machine (Pi serial, /etc/machine-id, Windows MachineGuid), else a random one.</summary>
    public static string Compute()
    {
        var source = ReadPiSerial() ?? ReadFirst("/etc/machine-id", "/var/lib/dbus/machine-id") ?? ReadWindowsMachineGuid();
        return source is null
            ? Guid.NewGuid().ToString("N")
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("slopgarden-hwid:" + source)))[..32];
    }

    private static string? ReadPiSerial()
    {
        var serial = ReadFirst("/sys/firmware/devicetree/base/serial-number")?.TrimEnd('\0');
        if (!string.IsNullOrWhiteSpace(serial) && serial.Trim('0').Length > 0) return "pi:" + serial;
        try
        {
            if (!File.Exists("/proc/cpuinfo")) return null;
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("Serial", StringComparison.Ordinal));
            var value = line?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
            return string.IsNullOrEmpty(value) || value.Trim('0').Length == 0 ? null : "pi:" + value;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadFirst(params string[] files)
    {
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                var text = File.ReadAllText(f).Trim();
                if (text.Length > 0) return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private static string? ReadWindowsMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
