using System.Collections.Concurrent;
using SkiaSharp;

namespace SlopGarden.Server.Services;

public sealed record StoredFrame(byte[] Data, string ContentType, int Width, int Height, DateTimeOffset CapturedAt);

/// <summary>Latest frame per camera (the live "feed"), per-view rescaling, and on-demand capture requests.</summary>
public sealed class CameraFrameStore
{
    private readonly ConcurrentDictionary<Guid, StoredFrame> _latest = new();
    private readonly ConcurrentDictionary<(Guid, int), StoredFrame> _scaled = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StoredFrame>> _pending = new();

    public StoredFrame Put(Guid cameraId, byte[] data, string contentType, Guid? requestId)
    {
        var (w, h) = Dimensions(data);
        var frame = new StoredFrame(data, contentType, w, h, DateTimeOffset.UtcNow);
        _latest[cameraId] = frame;
        foreach (var key in _scaled.Keys.Where(k => k.Item1 == cameraId).ToList()) _scaled.TryRemove(key, out _);
        if (requestId is { } rid && _pending.TryRemove(rid, out var tcs)) tcs.TrySetResult(frame);
        return frame;
    }

    public StoredFrame? Latest(Guid cameraId) => _latest.TryGetValue(cameraId, out var f) ? f : null;

    public void Drop(Guid cameraId)
    {
        _latest.TryRemove(cameraId, out _);
        foreach (var key in _scaled.Keys.Where(k => k.Item1 == cameraId).ToList()) _scaled.TryRemove(key, out _);
    }

    /// <summary>Returns the latest frame scaled to <paramref name="width"/> (never upscaled), cached until the next frame.</summary>
    public StoredFrame? Scaled(Guid cameraId, int? width)
    {
        var f = Latest(cameraId);
        if (f is null || width is null || width <= 0 || width >= f.Width) return f;
        var w = Math.Clamp(width.Value / 16 * 16, 64, 4096);
        return _scaled.GetOrAdd((cameraId, w), _ => Resize(f, w));
    }

    public Task<StoredFrame> WaitForAsync(Guid requestId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<StoredFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        cts.Token.Register(() =>
        {
            _pending.TryRemove(requestId, out _);
            tcs.TrySetCanceled();
            cts.Dispose();
        });
        return tcs.Task;
    }

    private static (int, int) Dimensions(byte[] data)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static StoredFrame Resize(StoredFrame f, int width)
    {
        try
        {
            using var bmp = SKBitmap.Decode(f.Data);
            if (bmp is null) return f;
            var height = Math.Max(1, (int)Math.Round(bmp.Height * (width / (double)bmp.Width)));
            using var scaled = bmp.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (scaled is null) return f;
            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82);
            return new StoredFrame(encoded.ToArray(), "image/jpeg", width, height, f.CapturedAt);
        }
        catch
        {
            return f;
        }
    }
}
