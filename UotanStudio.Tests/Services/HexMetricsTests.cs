using UotanStudio.Pages.DockControl;
using Xunit;

namespace UotanStudio.Tests.Services;

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
        // 负值按向零截断：HeaderHeight-1 落在表头内，结果为 0；y=0 得到 -1。
        Assert.Equal(0, HexMetrics.LineFromY(HexMetrics.HeaderHeight - 1));
        Assert.Equal(-1, HexMetrics.LineFromY(0));
    }

    [Fact]
    public void ByteIndexFromX_HexZone()
    {
        Assert.Equal(0, HexMetrics.ByteIndexFromX(HexMetrics.HexZoneStart, hexZone: true));
        Assert.Equal(1, HexMetrics.ByteIndexFromX(HexMetrics.HexZoneStart + HexMetrics.HexCellWidth, hexZone: true));
        Assert.Equal(15, HexMetrics.ByteIndexFromX(HexMetrics.HexZoneEnd - 1, hexZone: true));
        // 越界返回 -1
        Assert.Equal(-1, HexMetrics.ByteIndexFromX(HexMetrics.HexZoneStart - 1, hexZone: true));
        Assert.Equal(-1, HexMetrics.ByteIndexFromX(HexMetrics.HexZoneEnd, hexZone: true));
    }

    [Fact]
    public void ByteIndexFromX_AsciiZone()
    {
        Assert.Equal(0, HexMetrics.ByteIndexFromX(HexMetrics.AsciiZoneStart, hexZone: false));
        Assert.Equal(15, HexMetrics.ByteIndexFromX(HexMetrics.AsciiZoneStart + (15 * HexMetrics.AsciiCellWidth), hexZone: false));
        Assert.Equal(-1, HexMetrics.ByteIndexFromX(HexMetrics.AsciiZoneStart - 1, hexZone: false));
        Assert.Equal(-1, HexMetrics.ByteIndexFromX(HexMetrics.AsciiZoneStart + (16 * HexMetrics.AsciiCellWidth), hexZone: false));
    }

    [Fact]
    public void IsHexZone_DistinguishesZones()
    {
        Assert.True(HexMetrics.IsHexZone(HexMetrics.HexZoneStart));
        Assert.True(HexMetrics.IsHexZone(HexMetrics.HexZoneEnd - 1));
        Assert.False(HexMetrics.IsHexZone(HexMetrics.HexZoneEnd));
        Assert.False(HexMetrics.IsHexZone(HexMetrics.AsciiZoneStart));
    }

    [Fact]
    public void XFromByteIndex_ClampsAndPositions()
    {
        Assert.Equal(HexMetrics.HexZoneStart, HexMetrics.XFromByteIndex(0, hexZone: true));
        Assert.Equal(HexMetrics.HexZoneStart + HexMetrics.HexCellWidth, HexMetrics.XFromByteIndex(1, hexZone: true));
        Assert.Equal(HexMetrics.AsciiZoneStart, HexMetrics.XFromByteIndex(0, hexZone: false));
        // 越界钳制到 0..15
        Assert.Equal(HexMetrics.HexZoneStart + (15 * HexMetrics.HexCellWidth), HexMetrics.XFromByteIndex(99, hexZone: true));
    }

    [Fact]
    public void ContentWidth_MatchesZoneMath()
    {
        Assert.Equal(
            HexMetrics.HexZoneEnd + (HexMetrics.BytesPerLine * HexMetrics.AsciiCellWidth),
            HexMetrics.ContentWidth);
    }
}