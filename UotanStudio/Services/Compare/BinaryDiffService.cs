namespace UotanStudio.Services.Compare;

/// <summary>
/// <para>二进制差异统计信息。</para>
/// Binary diff statistics.
/// </summary>
public sealed class BinaryDiffStats
{
    /// <summary>
    /// <para>总差异字节数。</para>
    /// Total number of different bytes.
    /// </summary>
    public required long TotalDifferentBytes { get; init; }

    /// <summary>
    /// <para>差异区间数量。</para>
    /// Number of diff ranges.
    /// </summary>
    public required int RangeCount { get; init; }

    /// <summary>
    /// <para>首个差异偏移（字节）；文件完全相同时为 -1。</para>
    /// Offset of the first difference in bytes; -1 when the files are completely identical.
    /// </summary>
    public required long FirstDiffOffset { get; init; }
}

/// <summary>
/// <para>二进制比对服务：流式单遍比较两个文件，产出差异统计信息与（可选的）行差异位图，并提供
/// 文本文件探测。差异信息不物化为区间列表——高度不相似的固件镜像会产生数百万个区间，按对象列表
/// 常驻内存可达数 GB；行差异位图按每行（16 字节）1 bit 标记差异，内存与文件长度成正比。</para>
/// Binary comparison service: streams a single pass over two files to produce diff statistics and an
/// optional per-row diff bitmap, and detects text files. Diff data is never materialized as a range
/// list — highly dissimilar firmware images produce millions of ranges, which would reach gigabytes
/// when retained as an object list; the per-row bitmap marks each 16-byte row with 1 bit, so its size
/// scales with the file length instead.
/// </summary>
public sealed class BinaryDiffService
{
    private static readonly Lazy<BinaryDiffService> LazyInstance = new(() => new BinaryDiffService());

    /// <summary>
    /// <para>获取 BinaryDiffService 单例。</para>
    /// Gets the BinaryDiffService singleton instance.
    /// </summary>
    public static BinaryDiffService Instance => LazyInstance.Value;

    private BinaryDiffService() { }

    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// <para>每行字节数，与 hex 视图行宽一致（16 字节/行）。</para>
    /// Bytes per row, matching the hex view row width (16 bytes/row).
    /// </summary>
    private const int BytesPerRow = 16;

    /// <summary>
    /// <para>计算行差异位图的字节数：行数 = (maxLength + 15) / 16，位图 = (行数 + 7) / 8。</para>
    /// Computes the row-diff bitmap byte count: rows = (maxLength + 15) / 16, bitmap = (rows + 7) / 8.
    /// </summary>
    /// <param name="maxLength">两侧文件中较长者的字节数。<para>The byte length of the longer of the two files.</para></param>
    /// <returns><para>位图所需字节数。</para>The required bitmap byte count.</returns>
    public static int RowBitmapLength(long maxLength)
    {
        var rows = (maxLength + BytesPerRow - 1) / BytesPerRow;
        return (int)((rows + 7) / 8);
    }

    /// <summary>
    /// <para>流式单遍字节比较两个文件（64 KB 缓冲、两个 FileStream 同步前移），汇总总差异字节数、
    /// 差异区间数与首个差异偏移；不物化差异区间列表，内存恒定。</para>
    /// Streams a single-pass byte comparison of two files (64 KB buffer, both FileStreams advanced in
    /// sync) and aggregates the total different bytes, the range count and the first diff offset,
    /// without materializing a diff-range list (constant memory).
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径。<para>Right file path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>差异统计信息。</para>The diff statistics.</returns>
    public BinaryDiffStats CompareStats(string leftPath, string rightPath, CancellationToken ct = default)
        => Scan(leftPath, rightPath, rowDiffBitmap: null, ct);

    /// <summary>
    /// <para>流式单遍比较并写入行差异位图：与 <see cref="CompareStats(string, string, CancellationToken)"/>
    /// 相同的统计，同时按每 16 字节行 1 bit 标记该行是否含差异（供 hex 视图 O(1) 高亮判定）。
    /// 位图长度必须不小于 <see cref="RowBitmapLength"/> 的返回值，否则抛
    /// <see cref="ArgumentOutOfRangeException"/>。</para>
    /// Streams a single-pass comparison and fills a row-diff bitmap: the same statistics as
    /// <see cref="CompareStats(string, string, CancellationToken)"/>, while marking each 16-byte row
    /// with 1 bit when it contains any difference (for O(1) hex-view highlight queries). The bitmap
    /// length must be at least <see cref="RowBitmapLength"/>'s return value, otherwise an
    /// <see cref="ArgumentOutOfRangeException"/> is thrown.
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径。<para>Right file path.</para></param>
    /// <param name="rowDiffBitmap">行差异位图（可为 null 表示不输出）。<para>Row-diff bitmap (nullable to skip).</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>差异统计信息。</para>The diff statistics.</returns>
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
                // 协作式取消：提前退出并返回已扫描的部分统计/位图，由调用方在 await 后检查令牌。
                // Cooperative cancellation: exit early and return the partially-scanned
                // statistics/bitmap; the caller checks the token after awaiting.
                break;
            }

            var leftRead = left.Read(leftBuffer, 0, BufferSize);
            var rightRead = right.Read(rightBuffer, 0, BufferSize);
            var count = Math.Max(leftRead, rightRead);
            if (count == 0)
            {
                break;
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
    /// <para>把 [start, start+length) 字节区间覆盖的每行（16 字节）在位图中置 1；位图为 null 时跳过。</para>
    /// Sets the bit of every row (16 bytes) covered by [start, start+length) in the bitmap;
    /// no-op when the bitmap is null.
    /// </summary>
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
    /// <para>探测文件是否为文本：读取前 8 KB，包含 NUL 字节视为二进制返回 false；空文件返回 true。</para>
    /// Detects whether a file is text: reads the first 8 KB and returns false (binary) when a NUL byte is
    /// present; an empty file returns true.
    /// </summary>
    /// <param name="path">文件路径。<para>File path.</para></param>
    /// <returns><para>文本文件返回 true，二进制文件返回 false。</para> true for a text file; false for a binary file.</returns>
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
