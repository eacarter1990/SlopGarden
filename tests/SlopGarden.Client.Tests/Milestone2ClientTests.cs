using System.Text.Json.Nodes;
using SlopGarden.Client.Updates;
using SlopGarden.Plugins.Hardware;

namespace SlopGarden.Client.Tests;

public class Milestone2ClientTests
{
    [Fact]
    public void Jpeg_extractor_returns_the_frame_and_ignores_the_padding()
    {
        var jpeg = new byte[] { 0x00, 0x11, 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9, 0x99 };
        var frame = JpegFrames.Extract(jpeg);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 }, frame);
        Assert.Null(JpegFrames.Extract([1, 2, 3]));
    }

    [Fact]
    public void V4l2_capture_asks_ffmpeg_for_one_mjpeg_frame_from_the_device()
    {
        var args = FfmpegGrabber.Arguments("/dev/video2");
        Assert.Contains("-f", args);
        Assert.Contains("v4l2", args);
        Assert.Contains("/dev/video2", args);
        Assert.Contains("mjpeg", args);
    }

    [Fact]
    public void Container_replacement_keeps_the_mounts_and_uses_the_new_image()
    {
        var inspect = new JsonObject
        {
            ["Config"] = new JsonObject
            {
                ["Image"] = "ghcr.io/example/slopgarden-client:0.1.0",
                ["Env"] = new JsonArray("SLOPGARDEN_CONTAINER=slopgarden-client"),
                ["Entrypoint"] = new JsonArray("./slopgarden-client"),
            },
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray("/var/run/docker.sock:/var/run/docker.sock", "/data:/data"),
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
            },
            ["NetworkSettings"] = new JsonObject
            {
                ["Networks"] = new JsonObject { ["slopgarden"] = new JsonObject { ["Aliases"] = new JsonArray("client") } },
            },
        };
        var body = ContainerSpec.Replacement(inspect, "ghcr.io/example/slopgarden-client:0.2.0");
        Assert.Equal("ghcr.io/example/slopgarden-client:0.2.0", body["Image"]!.ToString());
        Assert.Contains("/data:/data", body["HostConfig"]!["Binds"]!.ToJsonString());
        Assert.Equal("unless-stopped", body["HostConfig"]!["RestartPolicy"]!["Name"]!.ToString());
        Assert.NotNull(body["NetworkingConfig"]!["EndpointsConfig"]!["slopgarden"]);
    }

    [Fact]
    public void Image_refs_split_the_tag_from_the_registry_port()
    {
        Assert.Equal(("ghcr.io/owner/slopgarden-client", "0.2.3"), DockerEngine.Split("ghcr.io/owner/slopgarden-client:0.2.3"));
        Assert.Equal(("localhost:5000/slopgarden-client", "latest"), DockerEngine.Split("localhost:5000/slopgarden-client"));
    }
}
