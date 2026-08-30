namespace UotanStudio.Services.Compare;

/// <summary>
/// <para>二进制差异区间（相邻差异已合并）。</para>
/// A binary diff range (adjacent differences are merged).
/// </summary>
public sealed class BinaryDiffRange
{
    /// <summary>
    /// <para>差异区间起始偏移（字节）。</para>
    /// Start offset of the diff range in bytes.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>
    /// <para>差异区间长度（字节）。</para>
    /// Length of the diff range in bytes.
    /// </summary>
    public required long Length { get; init; }
}

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
/// <para>二进制比对服务：流式单遍比较两个文件，产出相邻合并的差异区间与统计信息，并提供文本文件探测。</para>
/// Binary comparison service: streams a single pass over two files to produce adjacent-merged diff ranges
/// and statistics, and detects text files.
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
    /// <para>流式单遍字节比较两个文件（64 KB 缓冲、两个 FileStream 同步前移），返回相邻合并的差异区间 (offset, length)。</para>
    /// Streams a single-pass byte comparison of two files (64 KB buffer, both FileStreams advanced in sync)
    /// and returns adjacent-merged diff ranges (offset, length).
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径。<para>Right file path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>差异区间列表；文件完全相同时为空列表。</para> List of diff ranges; empty when the files are completely identical.</returns>
    public List<BinaryDiffRange> CompareRanges(string leftPath, string rightPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leftPath);
        ArgumentNullException.ThrowIfNull(rightPath);

        List<BinaryDiffRange> ranges = [];

        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];
        long offset = 0;
        long diffStart = -1;
        var same = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

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
                        ranges.Add(new BinaryDiffRange
                        {
                            Offset = diffStart,
                            Length = offset + i - diffStart,
                        });
                        diffStart = -1;
                        same = true;
                    }
                }
            }

            offset += count;
        }

        if (diffStart >= 0)
        {
            ranges.Add(new BinaryDiffRange
            {
                Offset = diffStart,
                Length = offset - diffStart,
            });
        }

        return ranges;
    }

    /// <summary>
    /// <para>汇总两个文件的总差异字节数、差异区间数与首个差异偏移；文件完全相同时首个差异偏移为 -1。</para>
    /// Aggregates the total different byte count, the number of diff ranges and the first diff offset;
    /// the first diff offset is -1 when the files are completely identical.
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径。<para>Right file path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>差异统计信息。</para> The diff statistics.</returns>
    public BinaryDiffStats CompareStats(string leftPath, string rightPath, CancellationToken ct = default)
    {
        var ranges = CompareRanges(leftPath, rightPath, ct);

        long totalDifferentBytes = 0;
        long firstDiffOffset = -1;
        foreach (var range in ranges)
        {
            totalDifferentBytes += range.Length;
            if (firstDiffOffset < 0)
            {
                firstDiffOffset = range.Offset;
            }
        }

        return new BinaryDiffStats
        {
            TotalDifferentBytes = totalDifferentBytes,
            RangeCount = ranges.Count,
            FirstDiffOffset = firstDiffOffset,
        };
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
