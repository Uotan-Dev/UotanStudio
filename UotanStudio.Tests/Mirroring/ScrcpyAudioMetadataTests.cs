using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public class ScrcpyAudioMetadataTests
{
    [Theory]
    [InlineData(0x6F707573u, "OPUS")]
    [InlineData(0x61616320u, "AAC")]
    [InlineData(0x666C6163u, "FLAC")]
    public void AudioCodec_UsesUpstreamLowercaseFourCc(uint fourCc, string expected)
    {
        Assert.Equal(expected, new ScrcpyAudioMetadata(fourCc).CodecName);
    }

    [Fact]
    public void AudioCodec_UnknownFourCc_DoesNotMatchKnownCodecs()
    {
        var metadata = new ScrcpyAudioMetadata(0x21212121);
        Assert.Equal("!!!!", metadata.CodecName);
        Assert.NotEqual("OPUS", metadata.CodecName);
    }
}

public class ScrcpyAudioFrameTests
{
    [Fact]
    public void ConfigPacket_IsRecognizedAndHasNoTimestamp()
    {
        var frame = new ScrcpyAudioFrame(long.MinValue, [0xFF, 0xF1]);
        Assert.True(frame.IsCodecConfig);
        Assert.Throws<InvalidOperationException>(() => frame.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void DataPacket_KeepsTimestampWithoutConfigBit()
    {
        var frame = new ScrcpyAudioFrame(987654L, [1, 2, 3]);
        Assert.False(frame.IsCodecConfig);
        Assert.Equal(987654L, frame.PresentationTimestampMicroseconds);
    }

    [Fact]
    public void DataPacket_HighBitsDoNotLeakIntoTimestamp()
    {
        ulong raw = ScrcpyConstants.PacketFlagConfig | 1_000_000UL;
        var frame = new ScrcpyAudioFrame(unchecked((long)raw), [0]);
        Assert.True(frame.IsCodecConfig);
    }
}