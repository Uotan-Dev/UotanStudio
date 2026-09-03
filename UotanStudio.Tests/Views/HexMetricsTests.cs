using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Views;

/// <summary>
/// Hex metrics geometry tests: verify coordinate ↔ byte-index conversions, line math and bounds clamping.
/// <para>十六进制几何辅助测试：校验坐标与字节索引互转、行计算与越界钳制。</para>
/// </summary>
public class HexMetricsTests
{
    [Fact]
    public void TotalLines_ReturnsCeilingOfDataLength()
    {
        Assert.Equal(0, HexMetrics.TotalLines(0));
        Assert.Equal(1, HexMetrics.TotalLines(1));
        Assert.Equal(1, HexMetrics.TotalLines(16));
        Assert.Equal(2, HexMetrics.TotalLines(17));
        Assert.Equal(3, HexMetrics.TotalLines(48));
    }

    [Fact]
    public void LineAndByteInLine_FromOffset()
    {
        Assert.Equal(0, HexMetrics.LineFromOffset(0));
        Assert.Equal(0, HexMetrics.LineFromOffset(15));
        Assert.Equal(1, HexMetrics.LineFromOffset(16));
        Assert.Equal(1, HexMetrics.LineFromOffset(31));

        Assert.Equal(0, HexMetrics.ByteInLineFromOffset(0));
        Assert.Equal(15, HexMetrics.ByteInLineFromOffset(15));
        Assert.Equal(0, HexMetrics.ByteInLineFromOffset(16));
        Assert.Equal(7, HexMetrics.ByteInLineFromOffset(39));
    }

    [Fact]
    public void LineFromY_AccountsForHeader()
    {
        Assert.Equal(0, HexMetrics.LineFromY(HexMetrics.HeaderHeight));
        Assert.Equal(1, HexMetrics.LineFromY(HexMetrics.HeaderHeight + HexMetrics.LineHeight));
        Assert.Equal(-1, HexMetrics.LineFromY(HexMetrics.HeaderHeight - 1));
        Assert.Equal(-1, HexMetrics.LineFromY(0));
    }

    [Fact]
    public void ByteIndexFromX_HexZone()
    {
        var layout = HexMetrics.CreateLayout(800);

        Assert.Equal(0, layout.ByteIndexFromX(layout.HexZoneStart, hexZone: true));
        Assert.Equal(1, layout.ByteIndexFromX(layout.HexZoneStart + layout.HexCellWidth, hexZone: true));
        Assert.Equal(layout.BytesPerLine - 1, layout.ByteIndexFromX(layout.HexZoneEnd - 1, hexZone: true));
        // 越界返回 -1
        Assert.Equal(-1, layout.ByteIndexFromX(layout.HexZoneStart - 1, hexZone: true));
        Assert.Equal(-1, layout.ByteIndexFromX(layout.HexZoneEnd, hexZone: true));
    }

    [Fact]
    public void ByteIndexFromX_AsciiZone()
    {
        var layout = HexMetrics.CreateLayout(800);

        Assert.Equal(0, layout.ByteIndexFromX(layout.AsciiZoneStart, hexZone: false));
        Assert.Equal(
            layout.BytesPerLine - 1,
            layout.ByteIndexFromX(
                layout.AsciiZoneStart + ((layout.BytesPerLine - 1) * layout.AsciiCellWidth),
                hexZone: false));
        Assert.Equal(-1, layout.ByteIndexFromX(layout.AsciiZoneStart - 1, hexZone: false));
        Assert.Equal(-1, layout.ByteIndexFromX(layout.AsciiZoneEnd, hexZone: false));
    }

    [Fact]
    public void IsHexZone_DistinguishesZones()
    {
        var layout = HexMetrics.CreateLayout(800);

        Assert.True(layout.IsHexZone(layout.HexZoneStart));
        Assert.True(layout.IsHexZone(layout.HexZoneEnd - 1));
        Assert.False(layout.IsHexZone(layout.HexZoneStart - 1));
        Assert.False(layout.IsHexZone(layout.AsciiZoneStart));
    }

    [Fact]
    public void XFromByteIndex_ClampsAndPositions()
    {
        var layout = HexMetrics.CreateLayout(800);

        Assert.Equal(layout.HexZoneStart, layout.XFromByteIndex(0, hexZone: true), 6);
        Assert.Equal(layout.HexZoneStart + layout.HexCellWidth, layout.XFromByteIndex(1, hexZone: true), 6);
        Assert.Equal(layout.AsciiZoneStart, layout.XFromByteIndex(0, hexZone: false), 6);
        // 越界钳制到当前响应式行的最后一格。
        Assert.Equal(
            layout.HexZoneStart + ((layout.BytesPerLine - 1) * layout.HexCellWidth),
            layout.XFromByteIndex(99, hexZone: true),
            6);
    }

    [Fact]
    public void CreateLayout_UsesAvailableWidthAndBoundedOffsetColumn()
    {
        var layout = HexMetrics.CreateLayout(1000);

        Assert.Equal(1000, layout.ContentWidth);
        Assert.Equal(HexMetrics.MaximumOffsetWidth, layout.OffsetWidth, 6);
        Assert.Equal(632.8, layout.HexWidth, 6);
        Assert.Equal(271.2, layout.AsciiWidth, 6);
        Assert.Equal(32, layout.BytesPerLine);
        Assert.Equal(layout.ContentWidth, layout.OffsetWidth + layout.HexWidth + layout.AsciiWidth, 6);
    }

    [Fact]
    public void CreateLayout_IncreasesBytesPerLineAsViewportWidens()
    {
        Assert.Equal(16, HexMetrics.CreateLayout(500).BytesPerLine);
        Assert.Equal(24, HexMetrics.CreateLayout(800).BytesPerLine);
        Assert.Equal(32, HexMetrics.CreateLayout(1000).BytesPerLine);
    }

    [Fact]
    public void CreateLayout_PreservesReadableMinimumForNarrowWidth()
    {
        var layout = HexMetrics.CreateLayout(320);

        Assert.Equal(HexMetrics.MinimumContentWidth, layout.ContentWidth);
        Assert.Equal(HexMetrics.MinimumOffsetWidth, layout.OffsetWidth);
        Assert.Equal(HexMetrics.BytesPerLine, layout.BytesPerLine);
        Assert.True(layout.HexCellWidth > 0);
        Assert.True(layout.AsciiCellWidth > 0);
    }
}
