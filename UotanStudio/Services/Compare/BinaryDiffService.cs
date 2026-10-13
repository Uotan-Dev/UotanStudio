namespace UotanStudio.Services.Compare;

/// <summary>
/// Binary diff statistics.
/// <para>二进制差异统计信息。</para>
/// </summary>
public sealed class BinaryDiffStats
{
    /// <summary>
    /// Total number of different bytes.
    /// <para>总差异字节数。</para>
    /// </summary>
    /// <value>The count of bytes that differ between the two files. <para>两个文件之间不一致的字节数量。</para></value>
    public required long TotalDifferentBytes { get; init; }

    /// <summary>
    /// Number of diff ranges.
    /// <para>差异区间数量。</para>
    /// </summary>
    /// <value>The number of contiguous differing byte ranges. <para>连续差异字节区间的数量。</para></value>
    public required int RangeCount { get; init; }

    /// <summary>
    /// Offset of the first difference in bytes; -1 when the files are completely identical.
    /// <para>首个差异偏移（字节）；文件完全相同时为 -1。</para>
    /// </summary>
    /// <value>The byte offset of the first difference, or -1 when identical. <para>首个差异的字节偏移；完全相同时为 -1。</para></value>
    public required long FirstDiffOffset { get; init; }
}

/// <summary>
/// Binary comparison service: streams a single pass over two files to produce diff statistics and
/// an optional per-row diff bitmap, and detects text files.
/// <para>二进制比对服务：流式单遍比较两个文件，产出差异统计信息与可选的行差异位图，并探测文本文件。</para>
/// </summary>
public sealed class BinaryDiffService
{
    private static readonly Lazy<BinaryDiffService> LazyInstance = new(() => new BinaryDiffService());

    /// <summary>
    /// Gets the shared instance created on first access.
    /// <para>获取首次访问时创建的共享实例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static BinaryDiffService Instance => LazyInstance.Value;

    internal BinaryDiffService() { }

    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Bytes per row covered by one bit of the row-diff bitmap (16 bytes/row).
    /// <para>行差异位图每一位覆盖的行字节数（16 字节/行）。</para>
    /// </summary>
    private const int BytesPerRow = 16;

    /// <summary>
    /// Computes the row-diff bitmap byte count: rows = (maxLength + 15) / 16, bitmap = (rows + 7) / 8.
    /// <para>计算行差异位图的字节数：行数 = (maxLength + 15) / 16，位图 = (行数 + 7) / 8。</para>
    /// </summary>
    /// <param name="maxLength">The byte length of the longer of the two files. <para>两侧文件中较长者的字节数。</para></param>
    /// <returns>The required bitmap byte count. <para>位图所需字节数。</para></returns>
    public static int RowBitmapLength(long maxLength)
    {
        var rows = (maxLength + BytesPerRow - 1) / BytesPerRow;
        return (int)((rows + 7) / 8);
    }

    /// <summary>
    /// Streams a single-pass byte comparison of two files and aggregates the different byte total,
    /// the range count and the first diff offset without materializing a range list.
    /// <para>流式单遍字节比较两个文件，汇总差异字节总数、差异区间数与首个差异偏移，不物化区间列表。</para>
    /// </summary>
    /// <param name="leftPath">Left file path. <para>左文件路径。</para></param>
    /// <param name="rightPath">Right file path. <para>右文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The diff statistics. <para>差异统计信息。</para></returns>
    public BinaryDiffStats CompareStats(string leftPath, string rightPath, CancellationToken ct = default)
        => Scan(leftPath, rightPath, rowDiffBitmap: null, ct);

    /// <summary>
    /// Streams a single-pass comparison and fills a row-diff bitmap with 1 bit per 16-byte row that
    /// contains a difference, reporting the same statistics as the overload without a bitmap.
    /// <para>流式单遍比较并写入行差异位图，每个含差异的 16 字节行置 1 bit，统计结果与不带位图的重载一致。</para>
    /// </summary>
    /// <param name="leftPath">Left file path. <para>左文件路径。</para></param>
    /// <param name="rightPath">Right file path. <para>右文件路径。</para></param>
    /// <param name="rowDiffBitmap">Row-diff bitmap to fill, or null to skip marking. <para>要写入的行差异位图；为 null 时不标记。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The diff statistics. <para>差异统计信息。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The bitmap is shorter than the length the file pair requires. <para>位图短于该文件对所需的长度。</para></exception>
    public BinaryDiffStats CompareStats(string leftPath, string rightPath, byte[] rowDiffBitmap, CancellationToken ct = default)
        => Scan(leftPath, rightPath, rowDiffBitmap, ct);

    private static BinaryDiffStats Scan(string leftPath, string rightPath, byte[]? rowDiffBitmap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(leftPath);
        ArgumentNullException.ThrowIfNull(rightPath);

        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        if (rowDiffBitmap is not null)
        {
            var required = RowBitmapLength(Math.Max(left.Length, right.Length));
            if (rowDiffBitmap.Length < required)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowDiffBitmap), rowDiffBitmap.Length,
                    $"Row-diff bitmap must be at least {required} bytes for the given files.");
            }
        }

        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];
        long offset = 0;
        long diffStart = -1;
        var same = true;
        long totalDifferentBytes = 0;
        var rangeCount = 0;
        long firstDiffOffset = -1;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var leftRead = left.Read(leftBuffer, 0, BufferSize);
            var rightRead = right.Read(rightBuffer, 0, BufferSize);
            var count = Math.Max(leftRead, rightRead);
            if (count == 0)
            {
                break;
            }

            if (leftRead == rightRead && leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                offset += count;
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var leftByte = i < leftRead ? leftBuffer[i] : (byte)0;
                var rightByte = i < rightRead ? rightBuffer[i] : (byte)0;
                var isSameByte = leftByte == rightByte;

                if (isSameByte != same)
                {
                    if (same)
                    {
                        diffStart = offset + i;
                        same = false;
                    }
                    else
                    {
                        var length = offset + i - diffStart;
                        totalDifferentBytes += length;
                        rangeCount++;
                        if (firstDiffOffset < 0)
                        {
                            firstDiffOffset = diffStart;
                        }

                        MarkRows(rowDiffBitmap, diffStart, length);
                        diffStart = -1;
                        same = true;
                    }
                }
            }

            offset += count;
        }

        if (diffStart >= 0)
        {
            var length = offset - diffStart;
            totalDifferentBytes += length;
            rangeCount++;
            if (firstDiffOffset < 0)
            {
                firstDiffOffset = diffStart;
            }

            MarkRows(rowDiffBitmap, diffStart, length);
        }

        return new BinaryDiffStats
        {
            TotalDifferentBytes = totalDifferentBytes,
            RangeCount = rangeCount,
            FirstDiffOffset = firstDiffOffset,
        };
    }

    /// <summary>
    /// Sets the bit of every row (16 bytes) covered by [start, start+length) in the bitmap;
    /// no-op when the bitmap is null.
    /// <para>把 [start, start+length) 字节区间覆盖的每行（16 字节）在位图中置 1；位图为 null 时跳过。</para>
    /// </summary>
    /// <param name="bitmap">Row-diff bitmap to mark, or null to skip. <para>要标记的行差异位图；为 null 时跳过。</para></param>
    /// <param name="start">First byte offset of the differing range. <para>差异区间的起始字节偏移。</para></param>
    /// <param name="length">Byte length of the differing range. <para>差异区间的字节长度。</para></param>
    private static void MarkRows(byte[]? bitmap, long start, long length)
    {
        if (bitmap is null)
        {
            return;
        }

        var firstRow = start / BytesPerRow;
        var lastRow = (start + length - 1) / BytesPerRow;
        for (var row = firstRow; row <= lastRow; row++)
        {
            bitmap[(int)(row / 8)] |= (byte)(1 << (int)(row % 8));
        }
    }

    /// <summary>
    /// Detects whether a file is text: reads the first 8 KB and returns false (binary) when a NUL byte is
    /// present; an empty file returns true.
    /// <para>探测文件是否为文本：读取前 8 KB，包含 NUL 字节视为二进制返回 false；空文件返回 true。</para>
    /// </summary>
    /// <param name="path">File path. <para>文件路径。</para></param>
    /// <returns>true for a text file; false for a binary file. <para>文本文件返回 true，二进制文件返回 false。</para></returns>
    public bool IsTextFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        var buffer = new byte[8192];
        var read = stream.Read(buffer, 0, buffer.Length);

        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return false;
            }
        }

        return true;
    }
}