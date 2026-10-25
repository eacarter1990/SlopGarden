using System.Diagnostics;
using System.Text.Json.Nodes;
using SlopGarden.Contracts;
using SlopGarden.Plugins.Abstractions;

namespace SlopGarden.Plugins.Hardware;

/// <summary>Finds a JPEG (SOI…EOI) inside a byte stream, such as an ffmpeg image2pipe capture.</summary>
public static class JpegFrames
{
    public static byte[]? Extract(ReadOnlySpan<byte> data)
    {
        var start = IndexOf(data, 0xFF, 0xD8);
        if (start < 0) return null;
        var end = IndexOf(data[start..], 0xFF, 0xD9);
        if (end < 0) return null;
        return data.Slice(start, end + 2).ToArray();
    }

    private static int IndexOf(ReadOnlySpan<byte> data, byte a, byte b)
    {
        for (var i = 0; i < data.Length - 1; i++)
            if (data[i] == a && data[i + 1] == b) return i;
        return -1;
    }
}

/// <summary>A USB UVC webcam exposed by Video4Linux (<c>/dev/videoN</c>), captured with ffmpeg.</summary>
public sealed class V4l2CameraPlugin : ICameraPlugin
{
    public const string Id = "v4l2-camera";

    public string PluginId => Id;
    public string DisplayName => "USB webcam (V4L2)";
    public string Description => "Captures a USB webcam on the client machine through Video4Linux. Requires ffmpeg and a /dev/video device.";

    public JsonObject SettingsSchema => new SchemaBuilder()
        .Prop("device", "string", "V4L2 device path.", JsonValue.Create("/dev/video0"))
        .Build();

    public ICamera Create(PluginContext context)
    {
        var device = Settings.String(context.Settings, "device", "/dev/video0");
        if (string.IsNullOrWhiteSpace(device)) device = "/dev/video0";
        return new V4l2Camera(device, new FfmpegGrabber());
    }
}

public interface IFrameGrabber
{
    Task<byte[]> GrabAsync(string device, CancellationToken cancellationToken);
}

public sealed class FfmpegGrabber : IFrameGrabber
{
    public const string FileName = "ffmpeg";

    public static IReadOnlyList<string> Arguments(string device) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "v4l2", "-i", device,
        "-frames:v", "1",
        "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1",
    ];

    public async Task<byte[]> GrabAsync(string device, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(FileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in Arguments(device)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("ffmpeg did not start. Install ffmpeg on the client.");
        await using var ms = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new IOException($"ffmpeg did not return a frame from {device} within 8 seconds.");
        }
        var jpeg = JpegFrames.Extract(ms.ToArray());
        if (jpeg is null)
        {
            var err = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new IOException(string.IsNullOrWhiteSpace(err) ? $"No JPEG from {device}." : err.Trim());
        }
        return jpeg;
    }
}

/// <summary>Lists USB UVC webcams. A camera often has a second video node for metadata; only the capture node (index 0, driver uvcvideo) is reported.</summary>
public static class V4l2Devices
{
    public static IReadOnlyList<DiscoveredDevice> List(string sysfsRoot)
    {
        if (!Directory.Exists(sysfsRoot)) return [];
        var found = new List<DiscoveredDevice>();
        foreach (var dir in Directory.EnumerateDirectories(sysfsRoot).Order(StringComparer.Ordinal))
        {
            var node = Path.GetFileName(dir);
            if (!node.StartsWith("video", StringComparison.Ordinal)) continue;
            if (!string.Equals(DriverName(dir), "uvcvideo", StringComparison.Ordinal)) continue;
            var indexPath = Path.Combine(dir, "index");
            var index = File.Exists(indexPath) ? File.ReadAllText(indexPath).Trim() : "0";
            if (index != "0") continue;
            var device = "/dev/" + node;
            var raw = File.Exists(Path.Combine(dir, "name")) ? File.ReadAllText(Path.Combine(dir, "name")) : "";
            found.Add(new DiscoveredDevice(
                V4l2CameraPlugin.Id, PluginKinds.Camera, device, Friendly(raw, node),
                new JsonObject { ["device"] = device }, []));
        }
        return found;
    }

    private static string? DriverName(string nodeDir)
    {
        var info = new DirectoryInfo(Path.Combine(nodeDir, "device", "driver"));
        var target = info.LinkTarget;
        if (string.IsNullOrWhiteSpace(target) && info.Exists)
            target = info.ResolveLinkTarget(returnFinalTarget: false)?.Name;
        if (string.IsNullOrWhiteSpace(target)) return null;
        return Path.GetFileName(target.TrimEnd('/', '\\'));
    }

    private static string Friendly(string raw, string node)
    {
        var text = raw.Trim();
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var left = text[..colon].Trim();
            var right = text[(colon + 1)..].Trim();
            if (left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return left;
        }
        return text.Length > 0 ? text : $"USB webcam {node}";
    }
}

public sealed class V4l2Discovery : IHardwareDiscovery
{
    public string PluginId => V4l2CameraPlugin.Id;

    public ValueTask<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return ValueTask.FromResult<IReadOnlyList<DiscoveredDevice>>([]);
        return ValueTask.FromResult(V4l2Devices.List("/sys/class/video4linux"));
    }
}

public sealed class V4l2Camera(string device, IFrameGrabber grabber) : ICamera
{
    public async ValueTask<CameraFrame> CaptureAsync(int? width, int? height, CancellationToken cancellationToken)
    {
        var jpeg = await grabber.GrabAsync(device, cancellationToken);
        return new CameraFrame(jpeg, "image/jpeg", width ?? 0, height ?? 0);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
