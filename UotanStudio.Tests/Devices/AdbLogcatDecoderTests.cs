using System.Text;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbLogcatDecoderTests
{
    [Fact]
    public void Decoder_PreservesUtf8AcrossEveryPacketBoundary()
    {
        const string text = "09-07 12:00:00.000  123  456 I Test: 中文日志\r\n\nlast line";
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var lines = new List<string>();
            var decoder = new AdbLogcatLineDecoder(lines.Add);
            decoder.Feed(bytes.AsSpan(0, split));
            decoder.Feed(bytes.AsSpan(split));
            decoder.Feed([], flush: true);
            Assert.Equal(["09-07 12:00:00.000  123  456 I Test: 中文日志", "", "last line"], lines);
        }
    }

    [Fact]
    public void Decoder_BoundsUnterminatedLinesAndRecoversAtNewline()
    {
        var lines = new List<string>();
        var decoder = new AdbLogcatLineDecoder(lines.Add);
        var packet = Encoding.UTF8.GetBytes(new string('x', 4096));
        for (var i = 0; i < 100; i++) decoder.Feed(packet);
        Assert.Empty(lines);
        decoder.Feed("\nnext\n"u8);
        Assert.Equal(2, lines.Count);
        Assert.Equal(AdbLogcatLineDecoder.MaximumLineLength, lines[0].Length);
        Assert.EndsWith(AdbLogcatLineDecoder.TruncationMarker, lines[0], StringComparison.Ordinal);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public void Decoder_LineAtMaximumLength_EmitsVerbatimWithoutMarker()
    {
        var content = new string('x', AdbLogcatLineDecoder.MaximumLineLength);
        var lines = new List<string>();
        var decoder = new AdbLogcatLineDecoder(lines.Add);
        decoder.Feed(Encoding.UTF8.GetBytes(content));
        decoder.Feed("\n"u8);
        Assert.Equal(content, Assert.Single(lines));
    }

    [Fact]
    public void Decoder_LineOverMaximumLength_AppendsMarkerWithinMaximumLength()
    {
        var capacity = AdbLogcatLineDecoder.MaximumLineLength - AdbLogcatLineDecoder.TruncationMarker.Length;
        var lines = new List<string>();
        var decoder = new AdbLogcatLineDecoder(lines.Add);
        decoder.Feed(Encoding.UTF8.GetBytes(new string('x', AdbLogcatLineDecoder.MaximumLineLength + 1)));
        decoder.Feed("\n"u8);
        var line = Assert.Single(lines);
        Assert.Equal(AdbLogcatLineDecoder.MaximumLineLength, line.Length);
        Assert.EndsWith(AdbLogcatLineDecoder.TruncationMarker, line, StringComparison.Ordinal);
        Assert.Equal(new string('x', capacity), line[..capacity]);
    }
}