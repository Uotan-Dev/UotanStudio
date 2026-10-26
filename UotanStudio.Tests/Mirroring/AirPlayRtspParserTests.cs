using System.Buffers.Binary;
using System.Text;
using UotanStudio.Mirroring.AirPlay;
using UotanStudio.Mirroring.AirPlay.Rtsp;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class AirPlayRtspParserTests
{
    private static byte[] HeaderBlock(string method, string uri, params string[] headers)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        foreach (var header in headers)
        {
            builder.Append(header).Append("\r\n");
        }

        builder.Append("\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static byte[] HeaderBlockWithoutTerminator(string method, string uri, params string[] headers)
    {
        var complete = HeaderBlock(method, uri, headers);
        return complete.AsSpan(0, complete.Length - 4).ToArray();
    }

    [Fact]
    public void Parse_OptionsWithCSeq_ReadsMethodUriAndHeader()
    {
        var header = HeaderBlockWithoutTerminator("OPTIONS", "*", "CSeq: 7", "Apple-Challenge: abcd");

        var request = AirPlayRtspParser.Parse(header, []);

        Assert.Equal("OPTIONS", request.Method);
        Assert.Equal("*", request.Uri);
        Assert.Equal("RTSP/1.0", request.Version);
        Assert.Equal(7, request.CSeq);
        Assert.Equal("abcd", request.GetHeader("apple-challenge"));
        Assert.Empty(request.Body);
    }

    [Fact]
    public void Parse_AnnounceWithPlistBody_KeepsBodyBytes()
    {
        var body = new byte[] { 0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x30, 0x30, 0x00 };
        var header = HeaderBlockWithoutTerminator("ANNOUNCE", "rtsp://127.0.0.1/1",
            "CSeq: 2", "Content-Type: application/x-apple-binary-plist",
            "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var request = AirPlayRtspParser.Parse(header, body);

        Assert.Equal("ANNOUNCE", request.Method);
        Assert.Equal(body, request.Body);
        Assert.Equal(2, request.CSeq);
    }

    [Fact]
    public void Parse_BodyLengthBeyondBound_Throws()
    {
        var header = HeaderBlockWithoutTerminator("SETUP", "rtsp://127.0.0.1/1",
            "Content-Length: " + (AirPlayConstants.MaximumBodyBytes + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        Assert.Throws<InvalidDataException>(() => AirPlayRtspParser.Parse(header, []));
    }

    [Fact]
    public void Parse_HeaderCountBeyondBound_Throws()
    {
        var headers = Enumerable.Range(0, AirPlayConstants.MaximumHeaderLines)
            .Select(index => "X-Probe-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": 0")
            .ToArray();
        var header = HeaderBlockWithoutTerminator("OPTIONS", "*", headers);

        Assert.Throws<InvalidDataException>(() => AirPlayRtspParser.Parse(header, []));
    }

    [Fact]
    public void Parse_RequestLineWithoutVersion_Throws()
    {
        var header = Encoding.Latin1.GetBytes("OPTIONS *\r\nCSeq: 1");

        Assert.Throws<InvalidDataException>(() => AirPlayRtspParser.Parse(header, []));
    }

    [Fact]
    public void Parse_BodyWithoutContentLength_Throws()
    {
        var header = HeaderBlockWithoutTerminator("RECORD", "rtsp://127.0.0.1/1", "CSeq: 3");

        Assert.Throws<InvalidDataException>(() => AirPlayRtspParser.Parse(header, [1, 2, 3]));
    }

    [Fact]
    public void FindHeaderEnd_CompleteBlock_ReturnsIndexPastEmptyLine()
    {
        var block = HeaderBlock("OPTIONS", "*", "CSeq: 1");

        var end = AirPlayRtspParser.FindHeaderEnd(block);

        Assert.Equal(block.Length, end);
    }

    [Fact]
    public void FindHeaderEnd_IncompleteBlock_ReturnsMinusOne()
    {
        var block = HeaderBlock("OPTIONS", "*", "CSeq: 1");

        Assert.Equal(-1, AirPlayRtspParser.FindHeaderEnd(block.AsSpan(0, block.Length - 2)));
    }

    [Fact]
    public void Serialize_ResponseWithBody_WritesStatusHeadersAndBody()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var response = AirPlayRtspResponse.Ok(body, ("Content-Type", "application/x-apple-binary-plist"));

        var packet = AirPlayRtspParser.Serialize(response, 42);

        var text = Encoding.Latin1.GetString(packet, 0, packet.Length - body.Length);
        Assert.StartsWith("RTSP/1.0 200 OK\r\n", text, StringComparison.Ordinal);
        Assert.Contains("CSeq: 42\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 4\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
        Assert.Equal(body, packet[^body.Length..]);
    }

    [Fact]
    public void ReadContentLength_AbsentHeader_ReturnsZero()
    {
        var header = HeaderBlockWithoutTerminator("OPTIONS", "*", "CSeq: 1");

        Assert.Equal(0, AirPlayRtspParser.ReadContentLength(header));
    }

    [Fact]
    public void ReadContentLength_NonNumeric_Throws()
    {
        var header = HeaderBlockWithoutTerminator("ANNOUNCE", "*", "Content-Length: many");

        Assert.Throws<InvalidDataException>(() => AirPlayRtspParser.ReadContentLength(header));
    }

    [Fact]
    public void Parse_PacketWithBigEndianLengthField_ProducesBodyOfAnnouncedSize()
    {
        var body = new byte[256];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0xDEADBEEF);
        var header = HeaderBlockWithoutTerminator("SETUP", "rtsp://host/1", "Content-Length: 256");

        var request = AirPlayRtspParser.Parse(header, body);

        Assert.Equal(256, request.Body.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32BigEndian(request.Body));
    }
}