using System.Buffers.Binary;

namespace SlopGarden.Plugins.Emulator;

/// <summary>Reads the pixel size from a JPEG or PNG header without decoding the image.</summary>
public static class ImageSize
{
    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;

        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = data[i + 1];
            if (marker is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += marker == 0xFF ? 1 : 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 > data.Length) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]));
            }
            if (marker == 0xDA || length < 2) return null;
            i += 2 + length;
        }
        return null;
    }
}
