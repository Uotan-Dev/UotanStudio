using System.Text;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.Streaming;

public class DiagHdlcCodecTests
{
    [Theory]
    [InlineData("", 0x0000)]
    [InlineData("123456789", 0x906E)]
    public void ComputeCrc16_ReferenceVectors_ProducesReferenceDigests(string text, ushort expected)
    {
        var data = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, DiagHdlcCodec.ComputeCrc16(data));
    }

    [Fact]
    public void ComputeCrc16_ResetCommand_MatchesHdlcReference()
    {
        Assert.Equal(0x4EAB, DiagHdlcCodec.ComputeCrc16([0x0B]));
    }

    [Fact]
    public void Encode_HelloPayload_MatchesReferenceFrameHex()
    {
        var payload = new byte[38];
        payload[0] = StreamingConstants.DiagHelloCommand;
        Encoding.ASCII.GetBytes(StreamingConstants.HostMagic).CopyTo(payload, 1);
        payload[33] = StreamingConstants.DiagHostVersion;
        payload[34] = 0x23;
        payload[35] = 0x23;
        payload[36] = 0x23;
        payload[37] = 0x20;

        var frame = DiagHdlcCodec.Encode(payload, withCrc: true);

        Assert.Equal(
            "0151434F4D206661737420646F776E6C6F61642070726F746F636F6C20686F73740323232320E7547E",
            Convert.ToHexString(frame));
    }

    [Fact]
    public void Encode_SecureCommand_MatchesReferenceFrameHex()
    {
        Assert.Equal("170157C67E",
            Convert.ToHexString(DiagHdlcCodec.Encode([StreamingConstants.DiagSecureCommand, 0x01], withCrc: true)));
    }

    [Fact]
    public void Encode_CloseCommand_MatchesReferenceFrameHex()
    {
        Assert.Equal("1554B77E",
            Convert.ToHexString(DiagHdlcCodec.Encode([StreamingConstants.DiagCloseCommand], withCrc: true)));
    }

    [Fact]
    public void Encode_MemoryReadPage_MatchesReferenceFrameHex()
    {
        byte[] payload = [StreamingConstants.DiagMemReadCommand, 0x00, 0x00, 0x80, 0x00, 0x00, 0x02];
        Assert.Equal("03000080000002622A7E", Convert.ToHexString(DiagHdlcCodec.Encode(payload, withCrc: true)));
    }

    [Fact]
    public void Encode_FlagAndEscapeBytes_EscapesWithXdPairs()
    {
        var frame = DiagHdlcCodec.Encode([0x7E, 0x7D, 0x01], withCrc: false);
        Assert.Equal([0x7D, 0x5E, 0x7D, 0x5D, 0x01, 0x7E], frame);
    }

    [Fact]
    public void RoundTrip_WithCrc_DecodesVerifiedPayload()
    {
        byte[] payload = [0x07, 0x2C, 0x01, 0x7E, 0x7D, 0xFF, 0x00];
        var frame = DiagHdlcCodec.Encode(payload, withCrc: true);

        Assert.True(DiagHdlcCodec.TryDecode(frame, withCrc: true, verifyCrc: true, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RoundTrip_WithoutCrc_DecodesFlagOnlyFrame()
    {
        byte[] payload = [0x03, 0x7D, 0x5E, 0x7E];
        var frame = DiagHdlcCodec.Encode(payload, withCrc: false);

        Assert.True(DiagHdlcCodec.TryDecode(frame, withCrc: false, verifyCrc: false, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void TryDecode_CorruptedBody_CrcVerificationFails()
    {
        var frame = DiagHdlcCodec.Encode([0x19, 0x00, 0x11], withCrc: true);
        frame[1] ^= 0xFF;

        Assert.False(DiagHdlcCodec.TryDecode(frame, withCrc: true, verifyCrc: true, out var rejected));
        Assert.Null(rejected);
        Assert.True(DiagHdlcCodec.TryDecode(frame, withCrc: true, verifyCrc: false, out var tolerated));
        Assert.NotNull(tolerated);
    }

    [Fact]
    public void TryDecode_TruncatedFrame_ReturnsFalse()
    {
        var frame = DiagHdlcCodec.Encode([0x15], withCrc: true);
        Assert.False(DiagHdlcCodec.TryDecode(frame[..^1], withCrc: true, verifyCrc: true, out _));
    }

    [Fact]
    public void TryDecode_DanglingEscape_ReturnsFalse()
    {
        Assert.False(DiagHdlcCodec.TryDecode([0x12, 0x7D], withCrc: false, verifyCrc: false, out _));
    }

    [Fact]
    public void TryDecode_InvalidEscapePair_ReturnsFalse()
    {
        Assert.False(DiagHdlcCodec.TryDecode([0x12, 0x7D, 0x30, 0x7E], withCrc: false, verifyCrc: false, out _));
    }

    [Fact]
    public void TryDecode_OversizedFrame_ReturnsFalse()
    {
        Assert.False(DiagHdlcCodec.TryDecode(new byte[StreamingConstants.MaxPacketBytes * 3],
            withCrc: false, verifyCrc: false, out _));
    }

    [Fact]
    public void Encode_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagHdlcCodec.Encode([], withCrc: true));
    }

    [Fact]
    public void Encode_OversizedPayload_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagHdlcCodec.Encode(new byte[StreamingConstants.MaxPacketBytes + 1], withCrc: true));
    }

    [Fact]
    public void TryDecode_LeadingFlagIsStrippedBeforePayload()
    {
        byte[] payload = [0x16];
        var inner = DiagHdlcCodec.Encode(payload, withCrc: true);
        var frame = new List<byte> { 0x7E };
        frame.AddRange(inner);

        Assert.True(DiagHdlcCodec.TryDecode(frame.ToArray(), withCrc: true, verifyCrc: true, out var decoded));
        Assert.Equal(payload, decoded);
    }
}