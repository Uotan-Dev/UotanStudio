using System.Runtime.InteropServices;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

public class NativeVideoTests
{
    [Fact]
    public void Mac_HardwareEncoderAndDecoder_RoundTripNv12()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("VideoToolbox requires macOS hardware."); return; }
        Assert.SkipUnless(VideoDecoderFactory.IsSupported(VideoCodecId.H264), "Hardware H.264 decode is unavailable.");
        int frames = 0;
        using var decoder = VideoDecoderFactory.Create();
        decoder.Initialize(VideoCodecId.H264, 64, 64, frame =>
        {
            Assert.Equal(64, frame.Width);
            Assert.Equal(64, frame.Height);
            for (int row = 0; row < 64; row++)
                Assert.All(frame.Span.Slice(row * frame.Stride, 64).ToArray(), value => Assert.InRange(value, (byte)14, (byte)18));
            frames++;
        });
        using var encoder = new UotanStudio.Media.Encoding.VideoToolboxVideoEncoder(VideoCodecId.H264, 64, 64,
            packet => decoder.Decode(packet.Data, packet.TimestampTicks));
        for (int index = 0; index < 8; index++)
        {
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(6144);
            pixels.AsSpan(0, 4096).Fill(16);
            pixels.AsSpan(4096, 2048).Fill(128);
            using var frame = new DecodedVideoFrame(pixels, 6144, 64, 64, 64, index * 166667L);
            encoder.Encode(frame);
        }
        decoder.Drain();
        Assert.Equal(8, frames);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlatformDecoder_OpenH264Cabac_ProducesChangingFrames(bool directGpu, bool nv12)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("No Annex-B backend on this platform.");
            return;
        }
        if (OperatingSystem.IsMacOS()) Assert.SkipUnless(VideoDecoderFactory.IsSupported(VideoCodecId.H264), "Mac hardware H.264 is unavailable.");
        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", "openh264-qcif-cabac.h264"));
        Assert.Equal("D5A2D70C45100CF8143572D9E9E48A867C04890690E4DE7633ED4E655DD95F9E",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
        int count = 0;
        var hashes = new HashSet<string>();
        byte[]? readbackStorage = null;
        DecodedVideoFrame? previousFrame = null;
        if (directGpu && !OperatingSystem.IsWindows()) { Assert.Skip("D3D11 requires Windows."); return; }
        if (directGpu) Assert.SkipUnless(VideoDecoderFactory.HasGpuDecoder(VideoCodecId.H264), "No D3D11 decoder.");
        using var decoder = directGpu && OperatingSystem.IsWindows() ? new MediaFoundationGpuVideoDecoder(new VideoDecoderOptions { PreferNv12Output = nv12 }) : VideoDecoderFactory.Create();
        decoder.Initialize(VideoCodecId.H264, 176, 144, frame =>
        {
            if (directGpu)
            {
                Assert.Equal(frame.Length, frame.Buffer.Length);
                if (readbackStorage is not null) Assert.Same(readbackStorage, frame.Buffer);
                if (previousFrame is not null)
                    Assert.Throws<ObjectDisposedException>(() => previousFrame.Buffer);
                readbackStorage = frame.Buffer;
                previousFrame = frame;
            }
            Assert.Equal(176, frame.Width);
            Assert.Equal(144, frame.Height);
            if (directGpu && !nv12) Assert.IsType<GpuDecodedVideoFrame>(frame);
            if (nv12) Assert.IsType<DecodedVideoFrame>(frame);
            int rowBytes = frame is GpuDecodedVideoFrame ? 176 * 4 : 176;
            byte[] visibleLuma = new byte[rowBytes * 144];
            for (int row = 0; row < 144; row++) frame.Span.Slice(row * frame.Stride, rowBytes).CopyTo(visibleLuma.AsSpan(row * rowBytes));
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(visibleLuma)));
            count++;
        });
        using var accessUnit = new MemoryStream();
        foreach (Range range in AnnexB.Split(stream))
        {
            ReadOnlySpan<byte> nal = stream[range];
            accessUnit.Write([0, 0, 0, 1]);
            accessUnit.Write(nal);
            if ((nal[0] & 31) is 1 or 5)
            {
                decoder.Decode(accessUnit.GetBuffer().AsSpan(0, checked((int)accessUnit.Length)));
                accessUnit.SetLength(0); // This fixed upstream fixture has one slice per picture.
            }
        }
        decoder.Drain();
        Assert.Equal(30, count);
        Assert.True(hashes.Count >= 10, "Expected changing output pixels across the upstream CABAC pictures.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlatformDecoder_IpcmH264_OutputsExactBlackPixels(bool directGpu, bool nv12)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "No Annex-B backend on this platform.");
        if (OperatingSystem.IsMacOS()) Assert.SkipUnless(VideoDecoderFactory.IsSupported(VideoCodecId.H264), "Mac hardware H.264 is unavailable.");
        byte[] packet = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", "black-64x64.h264"));
        int count = 0;
        if (directGpu && !OperatingSystem.IsWindows()) { Assert.Skip("D3D11 requires Windows."); return; }
        if (directGpu) Assert.SkipUnless(VideoDecoderFactory.HasGpuDecoder(VideoCodecId.H264), "No D3D11 decoder.");
        using var decoder = directGpu && OperatingSystem.IsWindows() ? new MediaFoundationGpuVideoDecoder(new VideoDecoderOptions { PreferNv12Output = nv12 }) : VideoDecoderFactory.Create();
        decoder.Initialize(VideoCodecId.H264, 64, 64, frame =>
        {
            Assert.Equal(64, frame.Width);
            Assert.Equal(64, frame.Height);
            if (directGpu && !nv12) Assert.IsType<GpuDecodedVideoFrame>(frame);
            if (nv12) Assert.IsType<DecodedVideoFrame>(frame);
            if (frame is GpuDecodedVideoFrame)
            {
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64 * 4; x++)
                        Assert.Equal(x % 4 == 3 ? 255 : 0, frame.Span[y * frame.Stride + x]);
            }
            else
            {
                for (int y = 0; y < 64; y++)
                    Assert.All(frame.Span.Slice(y * frame.Stride, 64).ToArray(), value => Assert.Equal(16, value));
                for (int y = 0; y < 32; y++)
                    Assert.All(frame.Span.Slice((64 + y) * frame.Stride, 64).ToArray(), value => Assert.Equal(128, value));
            }
            count++;
        });
        byte[] original = packet.ToArray();
        for (int i = 0; i < 12; i++) decoder.Decode(packet);
        decoder.Drain();
        Assert.Equal(12, count);
        Assert.True(count > 0, "Native decoder produced no frames.");
        Assert.Equal(original, packet); // Regression for reversed native input copy.
        decoder.Reset();
        int before = count;
        for (int i = 0; i < 12; i++) decoder.Decode(packet);
        decoder.Drain();
        Assert.Equal(before + 12, count);
        Assert.True(count > before, "Native decoder did not recover after Reset.");
        int reinitialized = 0;
        decoder.Initialize(VideoCodecId.H264, 64, 64, frame =>
        {
            if (directGpu && !nv12) Assert.IsType<GpuDecodedVideoFrame>(frame);
            if (nv12) Assert.IsType<DecodedVideoFrame>(frame);
            reinitialized++;
        });
        for (int i = 0; i < 12; i++) decoder.Decode(packet);
        decoder.Drain();
        Assert.Equal(12, reinitialized);
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(packet));
    }

    [Fact]
    public void Windows_Nv12Readback_MatchesCpuPixels()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("D3D11 requires Windows."); return; }
        Assert.SkipUnless(VideoDecoderFactory.HasGpuDecoder(VideoCodecId.H264), "No D3D11 decoder.");
        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Media", "openh264-qcif-cabac.h264"));
        List<byte[]> Decode(bool gpu)
        {
            using IVideoDecoder decoder = gpu
                ? new MediaFoundationGpuVideoDecoder(new VideoDecoderOptions { PreferNv12Output = true })
                : new MediaFoundationVideoDecoder();
            var frames = new List<byte[]>();
            decoder.Initialize(VideoCodecId.H264, 176, 144, frame =>
            {
                Assert.IsType<DecodedVideoFrame>(frame);
                byte[] pixels = new byte[176 * 216];
                for (int row = 0; row < 216; row++)
                    frame.Span.Slice(row * frame.Stride, 176).CopyTo(pixels.AsSpan(row * 176));
                frames.Add(pixels);
            });
            using var accessUnit = new MemoryStream();
            foreach (Range range in AnnexB.Split(stream))
            {
                ReadOnlySpan<byte> nal = stream[range];
                accessUnit.Write([0, 0, 0, 1]);
                accessUnit.Write(nal);
                if ((nal[0] & 31) is 1 or 5)
                {
                    decoder.Decode(accessUnit.GetBuffer().AsSpan(0, checked((int)accessUnit.Length)));
                    accessUnit.SetLength(0);
                }
            }
            decoder.Drain();
            return frames;
        }
        var expected = Decode(false);
        var actual = Decode(true);
        Assert.Equal(30, actual.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void Windows_MultipleDecoderLifetimes_AreIndependent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation runs on Windows.");
        using var first = VideoDecoderFactory.Create();
        using var second = VideoDecoderFactory.Create();
        first.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        second.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        first.Dispose();
        first.Dispose();
        second.Reset();
        using var third = VideoDecoderFactory.Create();
        third.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        Assert.True(second.IsInitialized);
        Assert.True(third.IsInitialized);
    }

    [Fact]
    public void NativeLayouts_MatchSdkAbi()
    {
        Assert.Equal(40, Marshal.SizeOf<D3D11VideoProcessorContentDesc>());
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(16, Marshal.SizeOf<D3D11Interop.VideoProcessorInputViewDesc>());
            Assert.Equal(16, Marshal.SizeOf<D3D11Interop.VideoProcessorOutputViewDesc>());
            Assert.Equal(IntPtr.Size == 8 ? 72 : 44, Marshal.SizeOf<D3D11Interop.VideoProcessorStream>());
            Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.OffsetOf<D3D11Interop.VideoProcessorStream>("InputFramePtr").ToInt32());
        }
        Assert.Equal(312, Marshal.SizeOf<DmaBufDescriptor>());
        Assert.Equal(24, Marshal.SizeOf<UotanStudio.Media.Decoding.VideoToolbox.CmTime>());
        Assert.Equal(72, Marshal.SizeOf<UotanStudio.Media.Decoding.VideoToolbox.CmTiming>());
    }

    [Fact]
    public void Linux_VaApi_QueriesExistingRenderNode()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("VA-API runs on Linux."); return; }
        string node = Environment.GetEnvironmentVariable("UOTAN_TEST_VA_RENDER_NODE") ?? "/dev/dri/renderD128";
        Assert.SkipUnless(File.Exists(node), $"No DRM render node at {node}.");
        using var session = new VaApiSession(node);
        Assert.NotEmpty(session.QueryCapabilities());
    }
}