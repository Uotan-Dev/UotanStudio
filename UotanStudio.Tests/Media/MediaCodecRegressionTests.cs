using System.Buffers;
using System.Security.Cryptography;
using UotanStudio.Media.Audio;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

/// <summary>Codec interoperability and malformed stream regressions.</summary>
public class MediaCodecRegressionTests
{
    [Theory]
    [InlineData(VideoCodecId.H265, "testsrc-128x96.h265", false)]
    [InlineData(VideoCodecId.H265, "testsrc-128x96.h265", true)]
    [InlineData(VideoCodecId.Av1, "testsrc-128x96.obu", false)]
    [InlineData(VideoCodecId.Av1, "testsrc-128x96.obu", true)]
    public void WindowsGpu_DecodesIndependentDynamicFixture(VideoCodecId codec, string fixture, bool nv12)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows GPU test."); return; }
        Assert.SkipUnless(VideoDecoderFactory.HasGpuDecoder(codec), "Codec unavailable on this GPU.");
        using var decoder = new MediaFoundationGpuVideoDecoder(new VideoDecoderOptions { PreferNv12Output = nv12 });
        byte[] stream = File.ReadAllBytes(Fixture(fixture));
        var hashes = new HashSet<string>();
        int count = 0;
        decoder.Initialize(codec, 128, 96, frame =>
        {
            if (nv12) Assert.IsType<DecodedVideoFrame>(frame);
            else Assert.IsType<GpuDecodedVideoFrame>(frame);
            Assert.Equal(128, frame.Width);
            Assert.Equal(96, frame.Height);
            hashes.Add(Convert.ToHexString(SHA256.HashData(frame.Span)));
            count++;
            Assert.Throws<InvalidOperationException>(() => ((IVideoDecoder)decoder).Reset());
        });
        if (codec == VideoCodecId.Av1)
        {
            foreach (Range range in Av1Obu.SplitTemporalUnits(stream)) decoder.Decode(stream.AsSpan()[range]);
        }
        else
        {
            using var packet = new MemoryStream();
            foreach (Range range in AnnexB.Split(stream))
            {
                ReadOnlySpan<byte> nal = stream.AsSpan()[range];
                packet.Write([0, 0, 0, 1]); packet.Write(nal);
                if (((nal[0] >> 1) & 63) < 32)
                {
                    decoder.Decode(packet.GetBuffer().AsSpan(0, (int)packet.Length));
                    packet.SetLength(0);
                }
            }
        }
        decoder.Drain();
        Assert.Equal(10, count);
        Assert.Equal(10, hashes.Count);
    }

    [Fact]
    public void NativeAac_IndependentAdtsFixture_PreservesCallbackAcrossReset()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Native AAC requires Windows/macOS.");
        byte[] stream = File.ReadAllBytes(Fixture("silence-48k-stereo.aac"));
        using var decoder = AudioDecoderFactory.Create();
        int count = 0;
        decoder.Initialize([0x11, 0x90], frame =>
        {
            Assert.Equal(48000, frame.SampleRate);
            Assert.Equal(2, frame.Channels);
            Assert.Equal(16, frame.BitsPerSample);
            Assert.True(frame.Length > 0 && frame.Length % 4 == 0);
            count++;
        });
        for (int pass = 0; pass < 2; pass++)
        {
            int before = count;
            if (pass != 0) decoder.Reset();
            for (int pos = 0; pos < stream.Length;)
            {
                int length = ((stream[pos + 3] & 3) << 11) | (stream[pos + 4] << 3) | (stream[pos + 5] >> 5);
                decoder.Decode(stream.AsSpan(pos + 7, length - 7), pos * 10L);
                pos += length;
            }
            decoder.Poll();
            Assert.True(count - before >= 10);
        }
    }

    [Fact]
    public void AacConfig_RejectsTruncationAndUnsupportedProfiles()
    {
        Assert.Equal((48000, 2), AudioFormatLimits.ParseAacConfig([0x11, 0x90]));
        Assert.Throws<InvalidDataException>(() => AudioFormatLimits.ParseAacConfig([0x17, 0x80]));
        Assert.Throws<InvalidDataException>(() => AudioFormatLimits.ParseAacConfig([0x16, 0x90]));
        Assert.Throws<NotSupportedException>(() => AudioFormatLimits.ParseAacConfig([0x29, 0x90]));
        Assert.Throws<NotSupportedException>(() => AudioFormatLimits.ParseAacConfig([0x11, 0x80]));
    }

    [Fact]
    public void Pcm_CopiesBorrowedPacketsAndRejectsIncompleteSamples()
    {
        using var decoder = AudioDecoderFactory.Create(AudioCodecId.Pcm);
        byte[] packet = [1, 2, 3, 4];
        DecodedAudioFrame? borrowed = null;
        decoder.Initialize([], frame =>
        {
            borrowed = frame;
            packet[0] = 99;
            Assert.Equal((byte)1, frame.Span[0]);
            Assert.Equal(123, frame.TimestampTicks);
        });
        decoder.Decode(packet, 123);
        Assert.Throws<ObjectDisposedException>(() => borrowed!.Buffer);
        Assert.Throws<InvalidDataException>(() => decoder.Decode([0, 1, 2], 0));
    }

    [Fact]
    public void Av1Obu_RejectsTruncatedAndUnboundedLengths()
    {
        Assert.Throws<InvalidDataException>(() => Av1Obu.SplitTemporalUnits([0x12]));
        Assert.Throws<InvalidDataException>(() => Av1Obu.SplitTemporalUnits([0x12, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]));
        Assert.Throws<InvalidDataException>(() => Av1Obu.SplitTemporalUnits([0x32, 5, 0]));
        Assert.Equal(2, Av1Obu.SplitTemporalUnits([0x12, 0, 0x32, 1, 1, 0x12, 0, 0x32, 1, 2]).Count);
    }

    [Theory]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited)]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full)]
    public void ColorConversion_VectorAndTailMatchReference(VideoColorMatrix matrix, VideoColorRange range)
    {
        const int width = 35, height = 3, stride = 40;
        byte[] input = new byte[stride * 5], output = new byte[width * height * 4];
        new Random(725).NextBytes(input);
        Nv12ToBgraConverter.Convert(input, width, height, stride, output, matrix: matrix, range: range);
        bool full = range == VideoColorRange.Full, hd = matrix == VideoColorMatrix.Bt709;
        double kr = hd ? .2126 : .299, kb = hd ? .0722 : .114, kg = 1 - kr - kb;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double luma = (input[y * stride + x] - (full ? 0 : 16)) * (full ? 1 : 255.0 / 219);
            int uv = stride * height + y / 2 * stride + (x & ~1);
            double u = (input[uv] - 128) * (full ? 1 : 255.0 / 224), v = (input[uv + 1] - 128) * (full ? 1 : 255.0 / 224);
            double[] expected = [luma + 2 * (1 - kb) * u, luma - 2 * kb * (1 - kb) / kg * u - 2 * kr * (1 - kr) / kg * v, luma + 2 * (1 - kr) * v];
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(output[(y * width + x) * 4 + c] - Math.Clamp(expected[c], 0, 255)), 0, 1.5);
            Assert.Equal(255, output[(y * width + x) * 4 + 3]);
        }
    }
    [Fact]
    public void MirrorSettings_SourceGeneratedJsonPreservesExplicitChoices()
    {
        var settings = new UotanStudio.Services.AppSettings
        {
            ScreenMirrorVideoCodec = "av1",
            ScreenMirrorDecoderBackend = VideoDecoderBackend.GStreamerHardware,
            ScreenMirrorColorMatrix = VideoColorMatrix.Bt709,
            ScreenMirrorColorRange = VideoColorRange.Full,
            ScreenMirrorAudioCodec = "opus",
            ScreenMirrorAudioBackend = AudioDecoderBackend.GStreamer,
            ScreenMirrorVolume = 0.25f,
            ScreenMirrorControl = false,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(settings, UotanStudio.Services.AppSettingsJsonContext.Default.AppSettings);
        var copy = System.Text.Json.JsonSerializer.Deserialize(json, UotanStudio.Services.AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal("av1", copy.ScreenMirrorVideoCodec);
        Assert.Equal(VideoDecoderBackend.GStreamerHardware, copy.ScreenMirrorDecoderBackend);
        Assert.Equal(VideoColorMatrix.Bt709, copy.ScreenMirrorColorMatrix);
        Assert.Equal(VideoColorRange.Full, copy.ScreenMirrorColorRange);
        Assert.Equal("opus", copy.ScreenMirrorAudioCodec);
        Assert.Equal(AudioDecoderBackend.GStreamer, copy.ScreenMirrorAudioBackend);
        Assert.Equal(0.25f, copy.ScreenMirrorVolume);
        Assert.False(copy.ScreenMirrorControl);
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", name);
}