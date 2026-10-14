using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public class ScrcpyVideoMetadataTests
{
    [Fact]
    public void StreamerConfigFlag_IsBitSixtyTwo()
    {
        var packet = new ScrcpyVideoFrame(unchecked((long)ScrcpyConstants.PacketFlagConfig), [0, 0, 0, 1, 0x67]);
        Assert.True(packet.IsCodecConfig);
        Assert.False(packet.IsKeyFrame);
        Assert.Throws<InvalidOperationException>(() => packet.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void StreamerSessionFlag_IsBitSixtyThreeAndNotAFrameFlag()
    {
        Assert.Equal(1UL << 63, ScrcpyConstants.PacketFlagSession);
        Assert.Equal(1UL << 62, ScrcpyConstants.PacketFlagConfig);
        Assert.Equal(1UL << 61, ScrcpyConstants.PacketFlagKeyFrame);
        Assert.Equal(ScrcpyConstants.PacketFlagKeyFrame - 1, ScrcpyConstants.PacketTimestampMask);
    }

    [Theory]
    [InlineData(123456L, false)]
    [InlineData(0x2000_0000_0001_E240L, true)]
    public void StreamerTimestamp_DoesNotIncludeKeyFrameBit(long raw, bool keyFrame)
    {
        var packet = new ScrcpyVideoFrame(raw, [1]);
        Assert.False(packet.IsCodecConfig);
        Assert.Equal(keyFrame, packet.IsKeyFrame);
        Assert.Equal(0x1E240L, packet.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void VideoCodec_UsesUpstreamLowercaseFourCc()
    {
        Assert.Equal("h264", new ScrcpyVideoMetadata(0x68323634, 1080, 1920).CodecName);
        Assert.Equal("h265", new ScrcpyVideoMetadata(0x68323635, 1080, 1920).CodecName);
        Assert.Equal("av1", new ScrcpyVideoMetadata(0x00617631, 1080, 1920).CodecName);
    }
}