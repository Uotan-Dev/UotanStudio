using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public class ScrcpyVideoMetadataTests
{
    [Fact]
    public void StreamerConfigFlag_IsRecognizedWithoutLegacySentinel()
    {
        var packet = new ScrcpyVideoFrame(long.MinValue, [0, 0, 0, 1, 0x67]);
        Assert.True(packet.IsCodecConfig);
        Assert.False(packet.IsKeyFrame);
        Assert.Throws<InvalidOperationException>(() => packet.PresentationTimestampMicroseconds);
    }

    [Theory]
    [InlineData(123456L, false)]
    [InlineData(0x400000000001E240L, true)]
    public void StreamerTimestamp_DoesNotIncludeKeyFrameBit(long raw, bool keyFrame)
    {
        var packet = new ScrcpyVideoFrame(raw, [1]);
        Assert.False(packet.IsCodecConfig);
        Assert.Equal(keyFrame, packet.IsKeyFrame);
        Assert.Equal(123456L, packet.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void VideoCodec_UsesUpstreamLowercaseFourCc()
    {
        Assert.Equal("h264", new ScrcpyVideoMetadata(0x68323634, 1080, 1920).CodecName);
        Assert.Equal("h265", new ScrcpyVideoMetadata(0x68323635, 1080, 1920).CodecName);
    }
}