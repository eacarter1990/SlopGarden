using System.Collections.Concurrent;
using SkiaSharp;
using SlopGarden.Emulator.Simulation;

namespace SlopGarden.Emulator.Imaging;

/// <summary>Produces camera snapshots: the camera's image cropped to the requested size with a name/time overlay.
/// Falls back to the unmodified source image if the native Skia library is unavailable.</summary>
public sealed class SnapshotRenderer(ILogger<SnapshotRenderer> logger)
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    private readonly ConcurrentDictionary<string, byte[]> _bundled = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _skiaUnavailable;

    public sealed record Snapshot(byte[] Data, string ContentType);

    public Snapshot Render(CameraDefinition camera, byte[]? customImage, int? width, int? height, DateTimeOffset localTime)
    {
        var source = customImage ?? LoadSource(camera.Image);
        var w = Math.Clamp(width ?? DefaultWidth, 16, 3840);
        var h = Math.Clamp(height ?? (width is null ? DefaultHeight : w * DefaultHeight / DefaultWidth), 16, 2160);
        if (_skiaUnavailable) return new Snapshot(source, SniffContentType(source));
        try
        {
            return new Snapshot(RenderWithSkia(source, w, h, camera.Name, localTime), "image/jpeg");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            _skiaUnavailable = true;
            logger.LogWarning(ex, "SkiaSharp native library unavailable; serving camera images without resizing/overlay");
            return new Snapshot(source, SniffContentType(source));
        }
    }

    public bool IsValidImage(byte[] data)
    {
        if (data.Length < 8) return false;
        if (_skiaUnavailable) return SniffContentType(data) != "application/octet-stream";
        try
        {
            using var bmp = SKBitmap.Decode(data);
            return bmp is not null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            _skiaUnavailable = true;
            return SniffContentType(data) != "application/octet-stream";
        }
    }

    private static byte[] RenderWithSkia(byte[] source, int width, int height, string label, DateTimeOffset localTime)
    {
        using var bitmap = SKBitmap.Decode(source) ?? throw new InvalidOperationException("Camera image could not be decoded.");
        using var image = SKImage.FromBitmap(bitmap);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        var scale = Math.Max((float)width / bitmap.Width, (float)height / bitmap.Height);
        var srcW = width / scale;
        var srcH = height / scale;
        var src = SKRect.Create((bitmap.Width - srcW) / 2, (bitmap.Height - srcH) / 2, srcW, srcH);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(image, src, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }

        var fontSize = Math.Max(11f, height / 34f);
        using var font = new SKFont(OverlayTypeface.Value, fontSize);
        var text = $"{label}  ·  {localTime:yyyy-MM-dd HH:mm:ss}";
        var textWidth = font.MeasureText(text);
        var pad = fontSize * 0.6f;
        var box = SKRect.Create(pad, height - fontSize * 2.2f, textWidth + pad * 2 + fontSize, fontSize * 1.6f);
        using (var bg = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true })
            canvas.DrawRoundRect(box, fontSize * 0.4f, fontSize * 0.4f, bg);
        using (var dot = new SKPaint { Color = new SKColor(239, 68, 68), IsAntialias = true })
            canvas.DrawCircle(box.Left + pad + fontSize * 0.3f, box.MidY, fontSize * 0.28f, dot);
        using (var fg = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawText(text, box.Left + pad + fontSize * 0.9f, box.MidY + fontSize * 0.35f, SKTextAlign.Left, font, fg);

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    // The dependency-free Linux Skia build has no fontconfig, so SKTypeface.Default has no glyphs there.
    private static readonly Lazy<SKTypeface> OverlayTypeface = new(() =>
    {
        string[] candidates =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf",
            "/usr/share/fonts/opentype/noto/NotoSans-Bold.ttf",
            @"C:\Windows\Fonts\segoeuib.ttf",
            @"C:\Windows\Fonts\arialbd.ttf",
            "/System/Library/Fonts/Helvetica.ttc",
            "/Library/Fonts/Arial.ttf",
        ];
        foreach (var path in candidates.Where(File.Exists))
        {
            var tf = SKTypeface.FromFile(path);
            if (tf is { GlyphCount: > 0 }) return tf;
        }
        return SKTypeface.Default;
    });

    private byte[] LoadSource(string image) => _bundled.GetOrAdd(image, static name =>
    {
        var asm = typeof(SnapshotRenderer).Assembly;
        using var stream = asm.GetManifestResourceStream($"SlopGarden.Emulator.Assets.{name}");
        if (stream is not null)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        if (File.Exists(name)) return File.ReadAllBytes(name);
        using var fallback = asm.GetManifestResourceStream("SlopGarden.Emulator.Assets.tomato-bed.jpg")!;
        using var fb = new MemoryStream();
        fallback.CopyTo(fb);
        return fb.ToArray();
    });

    public static string SniffContentType(byte[] data) => data switch
    {
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => "application/octet-stream",
    };
}
