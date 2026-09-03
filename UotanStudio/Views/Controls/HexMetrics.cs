namespace UotanStudio.Views.Controls;

/// <summary>
/// Pure geometry helpers for the hex viewer layout: converts between byte offsets, line/column indices and
/// pixel coordinates. Kept free of UI dependencies so it can be unit-tested.
/// <para>十六进制查看器布局的纯几何辅助：在字节偏移、行列索引与像素坐标之间转换。不依赖 UI，可单测。</para>
/// </summary>
public static class HexMetrics
{
    /// <summary>
    /// Minimum/default number of bytes rendered per line.
    /// <para>每行渲染的最小/默认字节数。</para>
    /// </summary>
    public const int BytesPerLine = 16;

    /// <summary>
    /// Maximum number of bytes rendered per line on a wide viewport.
    /// <para>宽视口下每行最多渲染的字节数。</para>
    /// </summary>
    public const int MaximumBytesPerLine = 32;

    private const int BytesPerLineStep = 8;
    private const double CompactHexCellWidth = 18.5d;
    private const double CompactAsciiCellWidth = 8d;

    /// <summary>
    /// Height of one data row in pixels.
    /// <para>单行数据行的高度（像素）。</para>
    /// </summary>
    public const int LineHeight = 20;

    /// <summary>
    /// Height of the column header in pixels.
    /// <para>列表头的高度（像素）。</para>
    /// </summary>
    public const int HeaderHeight = 20;

    /// <summary>
    /// Minimum width that keeps all three columns readable without overlapping glyphs.
    /// <para>保证三栏字符不重叠的最小内容宽度。</para>
    /// </summary>
    public const double MinimumContentWidth = 500d;

    /// <summary>
    /// Target proportion assigned to the offset column before applying its readable bounds.
    /// <para>应用可读宽度限制前，偏移地址列的目标比例。</para>
    /// </summary>
    public const double OffsetColumnRatio = 0.10d;

    /// <summary>
    /// Minimum readable width of the offset column.
    /// <para>偏移地址列的最小可读宽度。</para>
    /// </summary>
    public const double MinimumOffsetWidth = 68d;

    /// <summary>
    /// Maximum width of the offset column on wide viewports.
    /// <para>宽视口下偏移地址列的最大宽度。</para>
    /// </summary>
    public const double MaximumOffsetWidth = 96d;

    /// <summary>
    /// Proportion of the remaining width assigned to the hexadecimal byte column.
    /// <para>剩余宽度中分配给十六进制字节列的比例。</para>
    /// </summary>
    public const double HexColumnRatio = 0.70d;

    /// <summary>
    /// Proportion of the remaining width assigned to the ASCII column.
    /// <para>剩余宽度中分配给 ASCII 列的比例。</para>
    /// </summary>
    public const double AsciiColumnRatio = 0.30d;

    /// <summary>
    /// Creates three-column geometry for the available width. Widths below the readable minimum
    /// retain the minimum and are handled by horizontal scrolling.
    /// <para>根据可用宽度创建三栏几何；宽度不足时保留可读最小宽度并交由水平滚动处理。</para>
    /// </summary>
    public static HexColumnLayout CreateLayout(double availableWidth)
    {
        var contentWidth = double.IsFinite(availableWidth) && availableWidth > 0
            ? Math.Max(MinimumContentWidth, availableWidth)
            : MinimumContentWidth;
        var offsetWidth = Math.Clamp(
            contentWidth * OffsetColumnRatio,
            MinimumOffsetWidth,
            MaximumOffsetWidth);
        var remainingWidth = contentWidth - offsetWidth;
        var hexWidth = remainingWidth * HexColumnRatio;
        var asciiWidth = remainingWidth - hexWidth;
        var byteCapacity = Math.Min(
            (int)Math.Floor(hexWidth / CompactHexCellWidth),
            (int)Math.Floor(asciiWidth / CompactAsciiCellWidth));
        var bytesPerLine = Math.Clamp(
            (byteCapacity / BytesPerLineStep) * BytesPerLineStep,
            BytesPerLine,
            MaximumBytesPerLine);

        return new HexColumnLayout(contentWidth, offsetWidth, hexWidth, asciiWidth, bytesPerLine);
    }

    /// <summary>
    /// Returns the number of lines needed for a data length.
    /// <para>返回容纳指定数据长度所需的行数。</para>
    /// </summary>
    public static int TotalLines(long dataLength, int bytesPerLine = BytesPerLine) =>
        dataLength <= 0 ? 0 : (int)((dataLength + bytesPerLine - 1) / bytesPerLine);

    /// <summary>
    /// Returns the line index containing the byte offset.
    /// <para>返回字节偏移所在的行索引。</para>
    /// </summary>
    public static long LineFromOffset(long offset, int bytesPerLine = BytesPerLine) => offset / bytesPerLine;

    /// <summary>
    /// Returns the byte position within its line (0..<see cref="BytesPerLine"/>-1).
    /// <para>返回字节在其行内的位置（0..<see cref="BytesPerLine"/>-1）。</para>
    /// </summary>
    public static int ByteInLineFromOffset(long offset, int bytesPerLine = BytesPerLine) => (int)(offset % bytesPerLine);

    /// <summary>
    /// Returns the data-row index at a viewport Y coordinate (accounts for the header); may be negative.
    /// <para>返回视口 Y 坐标对应的数据行索引（扣除表头）；可能为负。</para>
    /// </summary>
    public static int LineFromY(double y) =>
        y < HeaderHeight ? -1 : (int)((y - HeaderHeight) / LineHeight);

    /// <summary>
    /// Returns the top pixel of a data row.
    /// <para>返回数据行顶部像素。</para>
    /// </summary>
    public static double YFromLine(int line) => HeaderHeight + ((long)line * LineHeight);

}

/// <summary>
/// Width-dependent geometry for the offset, hexadecimal and ASCII columns.
/// <para>随可用宽度变化的偏移、十六进制和 ASCII 三栏几何。</para>
/// </summary>
/// <param name="ContentWidth">Total rendered width.<para>渲染总宽度。</para></param>
/// <param name="OffsetWidth">Offset-column width.<para>偏移列宽度。</para></param>
/// <param name="HexWidth">Hexadecimal-column width.<para>十六进制列宽度。</para></param>
/// <param name="AsciiWidth">ASCII-column width.<para>ASCII 列宽度。</para></param>
/// <param name="BytesPerLine">Bytes rendered on each responsive row.<para>响应式数据行的字节数。</para></param>
public readonly record struct HexColumnLayout(
    double ContentWidth,
    double OffsetWidth,
    double HexWidth,
    double AsciiWidth,
    int BytesPerLine)
{
    /// <summary>Left edge of the hexadecimal column.<para>十六进制列左边缘。</para></summary>
    public double HexZoneStart => OffsetWidth;

    /// <summary>Right edge of the hexadecimal column.<para>十六进制列右边缘。</para></summary>
    public double HexZoneEnd => OffsetWidth + HexWidth;

    /// <summary>Left edge of the ASCII column.<para>ASCII 列左边缘。</para></summary>
    public double AsciiZoneStart => HexZoneEnd;

    /// <summary>Right edge of the ASCII column.<para>ASCII 列右边缘。</para></summary>
    public double AsciiZoneEnd => ContentWidth;

    /// <summary>Width of one hexadecimal byte cell.<para>单个十六进制字节单元宽度。</para></summary>
    public double HexCellWidth => HexWidth / BytesPerLine;

    /// <summary>Width of one ASCII byte cell.<para>单个 ASCII 字节单元宽度。</para></summary>
    public double AsciiCellWidth => AsciiWidth / BytesPerLine;

    /// <summary>
    /// Returns the byte position within its line for an X coordinate, or -1 outside the requested column.
    /// <para>返回 X 坐标对应的行内字节位置；位于指定列之外时返回 -1。</para>
    /// </summary>
    public int ByteIndexFromX(double x, bool hexZone)
    {
        var start = hexZone ? HexZoneStart : AsciiZoneStart;
        var end = hexZone ? HexZoneEnd : AsciiZoneEnd;
        if (x < start || x >= end)
        {
            return -1;
        }

        var cellWidth = hexZone ? HexCellWidth : AsciiCellWidth;
        var index = (int)Math.Floor(((x - start) / cellWidth) + 1e-9);
        return Math.Clamp(index, 0, BytesPerLine - 1);
    }

    /// <summary>
    /// Returns the left pixel of the cell for a byte position.
    /// <para>返回指定行内字节单元的左侧像素。</para>
    /// </summary>
    public double XFromByteIndex(int byteIndex, bool hexZone)
    {
        byteIndex = Math.Clamp(byteIndex, 0, BytesPerLine - 1);
        var start = hexZone ? HexZoneStart : AsciiZoneStart;
        var cellWidth = hexZone ? HexCellWidth : AsciiCellWidth;
        return start + (byteIndex * cellWidth);
    }

    /// <summary>
    /// Determines whether an X coordinate falls inside the hexadecimal column.
    /// <para>判断 X 坐标是否位于十六进制列内。</para>
    /// </summary>
    public bool IsHexZone(double x) => x >= HexZoneStart && x < HexZoneEnd;
}
