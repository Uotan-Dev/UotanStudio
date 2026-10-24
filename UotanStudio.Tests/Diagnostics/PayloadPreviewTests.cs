using UotanStudio.Comm.Diagnostics;
using Xunit;

namespace UotanStudio.Tests.Diagnostics;

public sealed class PayloadPreviewTests
{
    [Fact]
    public void Format_EmptyPayload_ReportsZeroBytes()
    {
        var text = PayloadPreview.Format([]);

        Assert.Equal("0 B", text);
    }

    [Fact]
    public void Format_ShortPrintablePayload_RendersHexAndText()
    {
        var text = PayloadPreview.Format("Hi"u8);

        Assert.Equal("2 B: 48 69 |Hi|", text);
    }

    [Fact]
    public void Format_PayloadLongerThanBound_TruncatesAndMarksIt()
    {
        var payload = "0123456789"u8;

        var text = PayloadPreview.Format(payload, 4);

        Assert.StartsWith("10 B: 30 31 32 33 |", text, StringComparison.Ordinal);
        Assert.EndsWith("|0123| (truncated)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_NonPrintableBytes_RendersDotsInTheTextColumn()
    {
        byte[] payload = [0x00, 0x1F, 0x7F, 0x41];

        var text = PayloadPreview.Format(payload);

        Assert.EndsWith("|...A|", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_BoundBelowOne_StillRendersOneByte()
    {
        byte[] payload = [0x41, 0x42];

        var text = PayloadPreview.Format(payload, 0);

        Assert.Equal("2 B: 41 |A| (truncated)", text);
    }
}