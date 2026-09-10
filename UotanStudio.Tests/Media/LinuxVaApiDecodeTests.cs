using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

/// <summary>Real VA hardware tests. Set UOTAN_TEST_VA_REQUIRED=1 on the acceptance host: unavailable hardware then fails.</summary>
public class LinuxVaApiDecodeTests
{
    private static string? RenderNode => Environment.GetEnvironmentVariable("UOTAN_TEST_VA_RENDER_NODE");

    private static void RequireHardware(VideoCodecId codec)
    {
        bool supported = VaApiVideoDecoder.IsSupported(codec, RenderNode);
        if (Environment.GetEnvironmentVariable("UOTAN_TEST_VA_REQUIRED") == "1")
            Assert.True(supported, $"Required {codec} VA hardware/bridge is unavailable on {RenderNode ?? "any render node"}.");
        else Assert.SkipUnless(supported, $"Linux VA {codec} hardware and native bridge required; not a hardware pass.");
    }

    [Theory]
    [InlineData(VideoCodecId.H264, "black-64x64.h264", 64, 64, 1)]
    [InlineData(VideoCodecId.H264, "openh264-qcif-cabac.h264", 176, 144, 30)]
    [InlineData(VideoCodecId.H265, "testsrc-128x96.h265", 128, 96, 10)]
    public async Task HardwareOutput_MatchesSoftwarePixelsAndDisplayOrder_AfterReset(VideoCodecId codec, string name, int width, int height, int count)
    {
        RequireHardware(codec);
        await CompareFixture(codec, Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", name), width, height, count);
    }

    // AV1 is optional on older hardware, even on an H.264/HEVC acceptance host.
    [Fact]
    public async Task Av1HardwareOutput_MatchesReference_WhenAvailable()
    {
        Assert.SkipUnless(VaApiVideoDecoder.IsSupported(VideoCodecId.Av1, RenderNode), "VA-API AV1 profile unavailable.");
        await CompareFixture(VideoCodecId.Av1, Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", "testsrc-128x96.obu"), 128, 96, 10);
    }

    [Theory]
    [InlineData("black-64x64.h264", 64, 64, 1)]
    [InlineData("openh264-qcif-cabac.h264", 176, 144, 30)]
    [InlineData("testsrc-128x96.h265", 128, 96, 10)]
    [InlineData("testsrc-128x96.obu", 128, 96, 10)]
    public async Task SoftwareReferenceTools_ProvideCompletePacketMapping(string name, int width, int height, int count)
    {
        // Validates the external reference workflow on development Linux hosts without a GPU.
        // This is deliberately separate from every hardware test above.
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/ffmpeg") && File.Exists("/usr/bin/ffprobe"),
            "Linux reference tools required; this test does not exercise VA hardware.");
        string path = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", name);
        using JsonDocument probe = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-show_packets", "-show_frames",
            "-show_entries", "packet=pos,size:frame=pkt_pos", "-of", "json", path));
        JsonElement[] entries = probe.RootElement.GetProperty("packets_and_frames").EnumerateArray().ToArray();
        var positions = entries.Where(e => e.GetProperty("type").GetString() == "packet")
            .Select(e => e.GetProperty("pos").GetString()).ToHashSet();
        var frames = entries.Where(e => e.GetProperty("type").GetString() == "frame").ToArray();
        Assert.Equal(count, frames.Length);
        foreach (JsonElement frame in frames) Assert.Contains(frame.GetProperty("pkt_pos").GetString(), positions);
        string output = Path.Combine(Path.GetTempPath(), "uotan-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Run("ffmpeg", "-v", "error", "-hwaccel", "none", "-i", path,
                "-threads", "1", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-f", "rawvideo", output);
            Assert.Equal((long)width * height * 3 / 2 * count, new FileInfo(output).Length);
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [InlineData(VideoCodecId.H264, "libx264", "-x264-params", "bframes=3:weightp=2:slices=4:keyint=12")]
    [InlineData(VideoCodecId.H265, "libx265", "-x265-params", "bframes=3:weightp=1:wpp=1:pools=1:frame-threads=1:keyint=12")]
    public async Task WeightedBFramesAndCroppedDimensions_MatchReference(VideoCodecId codec, string encoder, string option, string settings)
    {
        RequireHardware(codec);
        string directory = Path.Combine(Path.GetTempPath(), "uotan-va-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, codec == VideoCodecId.H264 ? "generated.h264" : "generated.h265");
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=190x106:rate=30", "-frames:v", "30",
                "-c:v", encoder, "-pix_fmt", "yuv420p", "-threads", "1", option, settings, "-color_range", "pc", "-colorspace", "bt709", path);
            await CompareFixture(codec, path, 190, 106, 30, expectedFullRange: true);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitMissingRenderNode_DoesNotFallback()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux only."); return; }
        using var decoder = VideoDecoderFactory.Create(VideoCodecId.H265,
            new() { Backend = VideoDecoderBackend.VaApi, VaApiRenderNode = "/dev/dri/uotan-missing-render-node" });
        Exception? error = Record.Exception(() => decoder.Initialize(VideoCodecId.H265, 64, 64, _ => { }));
        Assert.NotNull(error);
        Assert.True(error is NotSupportedException or DllNotFoundException, error.ToString());
        Assert.False(decoder.IsInitialized);
    }

    private static async Task CompareFixture(VideoCodecId codec, string path, int width, int height, int count, bool expectedFullRange = false)
    {
        // After hardware preflight, missing ffmpeg/ffprobe, malformed output and decode errors FAIL.
        string reference = Path.Combine(Path.GetTempPath(), "uotan-va-reference-" + Guid.NewGuid().ToString("N") + ".nv12");
        try
        {
            await Run("ffmpeg", "-v", "error", "-hwaccel", "none", "-i", path,
                "-threads", "1", "-pix_fmt", "nv12", "-fps_mode", "passthrough", "-f", "rawvideo", reference);
            string packetsJson = await Run("ffprobe", "-v", "error", "-show_packets", "-show_frames",
                "-show_entries", "packet=pos,size:frame=pkt_pos", "-of", "json", path);
            using JsonDocument probe = JsonDocument.Parse(packetsJson);
            // ffprobe returns a combined packets_and_frames array when both are requested.
            JsonElement[] entries = probe.RootElement.GetProperty("packets_and_frames").EnumerateArray().ToArray();
            var packets = entries.Where(e => e.GetProperty("type").GetString() == "packet")
                .Select(e => (Position: long.Parse(e.GetProperty("pos").GetString()!, CultureInfo.InvariantCulture),
                    Size: int.Parse(e.GetProperty("size").GetString()!, CultureInfo.InvariantCulture))).ToArray();
            long[] displayPositions = entries.Where(e => e.GetProperty("type").GetString() == "frame")
                .Select(e => long.Parse(e.GetProperty("pkt_pos").GetString()!, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(count, displayPositions.Length);
            var timestamps = displayPositions.Select((position, index) => (position, pts: 1000000L + index * 333333L))
                .ToDictionary(p => p.position, p => p.pts);
            int stride = (width + 1) & ~1;
            int frameBytes = stride * (height + (height + 1) / 2);
            byte[] expected = await File.ReadAllBytesAsync(reference, TestContext.Current.CancellationToken);
            Assert.Equal(count * frameBytes, expected.Length);
            byte[] data = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            using var decoder = VideoDecoderFactory.Create(codec, new() { Backend = VideoDecoderBackend.VaApi, VaApiRenderNode = RenderNode });
            int delivered = 0;
            decoder.Initialize(codec, width, height, frame =>
            {
                int index = delivered++;
                Assert.True(index < count, "Extra decoded frame.");
                Assert.Equal(width, frame.Width);
                Assert.Equal(height, frame.Height);
                Assert.Equal(1000000L + index * 333333L, frame.TimestampTicks);
                if (expectedFullRange)
                {
                    Assert.Equal(VideoColorRange.Full, frame.ColorRange);
                    Assert.Equal(VideoColorMatrix.Bt709, frame.ColorMatrix);
                }
                double squaredError = 0;
                for (int y = 0; y < height + (height + 1) / 2; y++)
                    for (int x = 0; x < stride; x++)
                    {
                        int difference = frame.Span[y * frame.Stride + x] - expected[index * frameBytes + y * stride + x];
                        squaredError += difference * difference;
                    }
                Assert.True(squaredError / frameBytes <= 2.0, $"Frame {index} differs from software reference (MSE={squaredError / frameBytes}).");
                if (Path.GetFileName(path) == "black-64x64.h264")
                {
                    Assert.All(frame.Span[..(width * height)].ToArray(), value => Assert.Equal(16, value));
                    Assert.All(frame.Span[(width * height)..].ToArray(), value => Assert.Equal(128, value));
                }
            });
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0) decoder.Reset();
                delivered = 0;
                foreach (var packet in packets)
                    decoder.Decode(data.AsSpan(checked((int)packet.Position), packet.Size), timestamps.GetValueOrDefault(packet.Position, long.MinValue));
                decoder.Poll();
                decoder.Drain();
                Assert.Equal(count, delivered);
                Assert.Contains("VA-API", VideoDecoderFactory.GetBackendName(decoder));
            }
        }
        finally { File.Delete(reference); }
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        string result = await output;
        string diagnostic = await error;
        Assert.True(process.ExitCode == 0, $"{executable} failed: {diagnostic}");
        return result;
    }
}
