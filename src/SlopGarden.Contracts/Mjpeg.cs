using System.Text;

namespace SlopGarden.Contracts;

/// <summary>multipart/x-mixed-replace frames. Browsers render this in an <c>&lt;img&gt;</c> as a live feed.</summary>
public static class Mjpeg
{
    public const string Boundary = "frame";
    public const string ContentType = "multipart/x-mixed-replace; boundary=frame";

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(jpeg, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }
}
