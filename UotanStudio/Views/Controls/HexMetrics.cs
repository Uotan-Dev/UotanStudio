namespace UotanStudio.Views.Controls;

/// <summary>
/// Pure geometry helpers for the hex viewer layout: converts between byte offsets, line/column indices and
/// pixel coordinates. Kept free of UI dependencies so it can be unit-tested.
/// <para>十六进制查看器布局的纯几何辅助：在字节偏移、行列索引与像素坐标之间转换。不依赖 UI，可单测。</para>
/// </summary>
public static class HexMetrics
{
    /// <summary>
    /// Number of bytes rendered per line.
    /// <para>每行渲染的字节数。</para>
    /// </summary>
    public const int BytesPerLine = 16;

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
    /// Width of the offset gutter in pixels.
    /// <para>偏移列宽度（像素）。</para>
    /// </summary>
    public const int OffsetWidth = 84;

    /// <summary>
    /// Width of one hex cell in pixels.
    /// <para>单个十六进制单元格宽度（像素）。</para>
    /// </summary>
    public const int HexCellWidth = 22;

    /// <summary>
    /// Width of one ASCII cell in pixels.
    /// <para>单个 ASCII 单元格宽度（像素）。</para>
    /// </summary>
    public const int AsciiCellWidth = 13;

    /// <summary>
    /// Left edge of the hex zone.
    /// <para>十六进制区域左边缘。</para>
    /// </summary>
    public static int HexZoneStart => OffsetWidth;

    /// <summary>
    /// Right edge of the hex zone (left edge of the ASCII zone).
    /// <para>十六进制区域右边缘（ASCII 区域左边缘）。</para>
    /// </summary>
    public static int HexZoneEnd => OffsetWidth + (BytesPerLine * HexCellWidth);

    /// <summary>
    /// Left edge of the ASCII zone.
    /// <para>ASCII 区域左边缘。</para>
    /// </summary>
    public static int AsciiZoneStart => HexZoneEnd;

    /// <summary>
    /// Total content width in pixels.
    /// <para>内容总宽度（像素）。</para>
    /// </summary>
    public static int ContentWidth => AsciiZoneStart + (BytesPerLine * AsciiCellWidth);

    /// <summary>
    /// Returns the number of lines needed for a data length.
    /// <para>返回容纳指定数据长度所需的行数。</para>
    /// </summary>
    public static int TotalLines(long dataLength) => dataLength <= 0 ? 0 : (int)((dataLength + BytesPerLine - 1) / BytesPerLine);

    /// <summary>
    /// Returns the line index containing the byte offset.
    /// <para>返回字节偏移所在的行索引。</para>
    /// </summary>
    public static long LineFromOffset(long offset) => offset / BytesPerLine;

    /// <summary>
    /// Returns the byte position within its line (0..<see cref="BytesPerLine"/>-1).
    /// <para>返回字节在其行内的位置（0..<see cref="BytesPerLine"/>-1）。</para>
    /// </summary>
    public static int ByteInLineFromOffset(long offset) => (int)(offset % BytesPerLine);

    /// <summary>
    /// Returns the data-row index at a viewport Y coordinate (accounts for the header); may be negative.
    /// <para>返回视口 Y 坐标对应的数据行索引（扣除表头）；可能为负。</para>
    /// </summary>
    public static int LineFromY(double y) => (int)((y - HeaderHeight) / LineHeight);

    /// <summary>
    /// Returns the top pixel of a data row.
    /// <para>返回数据行顶部像素。</para>
    /// </summary>
    public static double YFromLine(int line) => HeaderHeight + ((long)line * LineHeight);

    /// <summary>
    /// Returns the byte position within its line for an X coordinate, or -1 when outside the data columns.
    /// <para>返回 X 坐标对应的行内字节位置，落在数据列之外时返回 -1。</para>
    /// </summary>
    public static int ByteIndexFromX(double x, bool hexZone)
    {
        if (hexZone)
        {
            if (x < HexZoneStart || x >= HexZoneEnd)
            {
                return -1;
            }

            var index = (int)((x - HexZoneStart) / HexCellWidth);
            return Math.Clamp(index, 0, BytesPerLine - 1);
        }

        var asciiEnd = AsciiZoneStart + (BytesPerLine * AsciiCellWidth);
        if (x < AsciiZoneStart || x >= asciiEnd)
        {
            return -1;
        }

        var asciiIndex = (int)((x - AsciiZoneStart) / AsciiCellWidth);
        return Math.Clamp(asciiIndex, 0, BytesPerLine - 1);
    }

    /// <summary>
    /// Returns the left pixel of the cell for a byte position.
    /// <para>返回字节位置对应单元格的左像素。</para>
    /// </summary>
    public static double XFromByteIndex(int byteIndex, bool hexZone)
    {
        byteIndex = Math.Clamp(byteIndex, 0, BytesPerLine - 1);
        return hexZone ? HexZoneStart + (byteIndex * HexCellWidth) : AsciiZoneStart + (byteIndex * AsciiCellWidth);
    }

    /// <summary>
    /// Determines whether an X coordinate falls in the hex zone (vs the ASCII zone).
    /// <para>判断 X 坐标是否落在十六进制区域（而非 ASCII 区域）。</para>
    /// </summary>
    public static bool IsHexZone(double x) => x < HexZoneEnd;
}