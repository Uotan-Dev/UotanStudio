using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

public class VideoDecoderFactoryTests
{
    [Fact]
    public void IsSupported_UnknownCodec_IsFalse() => Assert.False(VideoDecoderFactory.IsSupported((VideoCodecId)999));

    [Fact]
    public void Create_KnownPlatform_IsUninitialized()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Throws<NotSupportedException>(() => VideoDecoderFactory.Create());
            return;
        }
        using var decoder = VideoDecoderFactory.Create();
        Assert.False(decoder.IsInitialized);
    }

    [Fact]
    public void Linux_DoesNotAdvertisePrimitivesAsAnnexBDecoder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux-specific capability boundary.");
        Assert.False(VideoDecoderFactory.IsSupported(VideoCodecId.H264));
        Assert.False(VideoDecoderFactory.IsSupported(VideoCodecId.H265));
    }

    [Fact]
    public void DecodedVideoFrame_RejectsInvalidArguments()
    {
        var buffer = new byte[64];
        Assert.Throws<ArgumentNullException>(() => new DecodedVideoFrame(null!, 64, 4, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 128, 4, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 64, 0, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 64, 4, 4, 2, 0));
        Assert.Throws<ArgumentException>(() => new DecodedVideoFrame(buffer, 23, 4, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 64, 3, 3, 3, 0));
    }

    [Fact]
    public void DecodedVideoFrame_AfterDispose_ThrowsObjectDisposed()
    {
        var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        var frame = new DecodedVideoFrame(pooled, 64, 4, 4, 4, 0);
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Buffer);
        Assert.Throws<ObjectDisposedException>(() => frame.Span.Length);
    }
}
