using SkiaSharp;
using SlopGarden.Client.Cameras;
using SlopGarden.Plugins.Emulator;

namespace SlopGarden.Client.Tests.Cameras;

public class CameraSizeTests
{
    [Theory]
    [InlineData(640, null, 1280, 720, 640, 360)]
    [InlineData(null, 360, 1280, 720, 640, 360)]
    [InlineData(800, 800, 1280, 720, 800, 800)]
    [InlineData(null, null, 1280, 720, 1280, 720)]
    [InlineData(640, null, 0, 0, 640, 0)]
    public void Requested_size_keeps_the_configured_aspect_ratio(int? width, int? height, int configWidth, int configHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), CameraManager.RequestedSize(width, height, configWidth, configHeight));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, 320, 180)]
    [InlineData(SKEncodedImageFormat.Png, 33, 17)]
    public void Reads_size_from_image_header(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var data = bitmap.Encode(format, 80);
        Assert.Equal((width, height), ImageSize.TryRead(data.ToArray()));
    }

    [Fact]
    public void Jpeg_size_skips_segments_before_the_frame_header()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC4, 0x00, 0x03, 0x00,
            0xFF, 0xC2, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        ];
        Assert.Equal((640, 480), ImageSize.TryRead(jpeg));
    }

    [Fact]
    public void Unknown_or_truncated_data_has_no_size()
    {
        Assert.Null(ImageSize.TryRead([]));
        Assert.Null(ImageSize.TryRead("GIF89a"u8));
        Assert.Null(ImageSize.TryRead([0xFF, 0xD8, 0xFF, 0xC0, 0x00]));
    }
}
