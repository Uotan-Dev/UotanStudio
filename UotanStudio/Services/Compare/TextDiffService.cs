using System.Text;
using DiffPlex;
using DiffPlex.Model;

namespace UotanStudio.Services.Compare;

/// <summary>
/// <para>文本差异行类型。</para>
/// Text diff line kind.
/// </summary>
public enum DiffLineKind
{
    /// <summary>
    /// <para>两侧均存在且内容相同的行。</para>
    /// A line present and identical on both sides.
    /// </summary>
    Equal,

    /// <summary>
    /// <para>仅右侧新增的行。</para>
    /// A line added on the right side only.
    /// </summary>
    Added,

    /// <summary>
    /// <para>仅左侧删除的行。</para>
    /// A line deleted from the left side only.
    /// </summary>
    Deleted,

    /// <summary>
    /// <para>两侧对应位置发生修改的行。</para>
    /// A line modified between the two sides.
    /// </summary>
    Modified,
}

/// <summary>
/// <para>文本差异的一行，携带两侧行号与文本。</para>
/// A single line of a text diff, carrying both side line numbers and text.
/// </summary>
public sealed class TextDiffLine
{
    /// <summary>
    /// <para>行类型。</para>
    /// Line kind.
    /// </summary>
    public required DiffLineKind Kind { get; init; }

    /// <summary>
    /// <para>左侧行号（1-based）；该行在左侧缺失时为 0。</para>
    /// Left line number (1-based); 0 when the line is absent on the left.
    /// </summary>
    public int LeftLine { get; init; }

    /// <summary>
    /// <para>右侧行号（1-based）；该行在右侧缺失时为 0。</para>
    /// Right line number (1-based); 0 when the line is absent on the right.
    /// </summary>
    public int RightLine { get; init; }

    /// <summary>
    /// <para>左侧行文本；缺失时为空串。</para>
    /// Left line text; empty string when absent.
    /// </summary>
    public required string LeftText { get; init; }

    /// <summary>
    /// <para>右侧行文本；缺失时为空串。</para>
    /// Right line text; empty string when absent.
    /// </summary>
    public required string RightText { get; init; }
}

/// <summary>
/// <para>文本比对服务，基于 DiffPlex 提供行级差异，并提供带行数与字节上限的流式重载。</para>
/// Text comparison service, providing line-level diffs via DiffPlex plus a stream overload bounded by line and byte limits.
/// </summary>
public sealed class TextDiffService
{
    private static readonly Lazy<TextDiffService> LazyInstance = new(() => new TextDiffService());

    /// <summary>
    /// <para>获取 TextDiffService 单例。</para>
    /// Gets the TextDiffService singleton instance.
    /// </summary>
    public static TextDiffService Instance => LazyInstance.Value;

    private TextDiffService() { }

    private const int MaxDiffLines = 2_000_000;
    private const long MaxDiffBytes = 256L * 1024 * 1024;

    /// <summary>
    /// <para>对两个文本执行行级差异（DiffPlex），并把差异块对齐为 <see cref="TextDiffLine"/> 列表：
    /// Insert 为 Added、Delete 为 Deleted、Replace 为 Modified、相等区间为 Equal；行号 1-based，缺失侧为 0，缺失文本为空串。</para>
    /// Performs a line-level diff of two texts (DiffPlex) and aligns the diff blocks into a
    /// <see cref="TextDiffLine"/> list: Insert → Added, Delete → Deleted, Replace → Modified, equal regions → Equal;
    /// line numbers are 1-based, 0 on the missing side, and missing text is an empty string.
    /// </summary>
    /// <param name="leftText">左侧文本。<para>Left text.</para></param>
    /// <param name="rightText">右侧文本。<para>Right text.</para></param>
    /// <returns><para>对齐后的差异行列表。</para> The aligned list of diff lines.</returns>
    public List<TextDiffLine> DiffLines(string leftText, string rightText)
    {
        ArgumentNullException.ThrowIfNull(leftText);
        ArgumentNullException.ThrowIfNull(rightText);

        var result = new Differ().CreateLineDiffs(leftText, rightText, ignoreWhitespace: false);
        return Align(result);
    }

    /// <summary>
    /// <para>从两个流逐行读取（UTF-8，保留原始行文本、去除换行符）并执行行级差异；任一文件超过 200 万行或 256 MB 时抛 <see cref="FormatException"/>。</para>
    /// Reads both streams line by line (UTF-8, preserving original line text and stripping line endings) and
    /// performs a line-level diff; throws <see cref="FormatException"/> when either file exceeds 2,000,000 lines or 256 MB.
    /// </summary>
    /// <param name="left">左侧流。<para>Left stream.</para></param>
    /// <param name="right">右侧流。<para>Right stream.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>对齐后的差异行列表。</para> The aligned list of diff lines.</returns>
    public List<TextDiffLine> DiffLines(Stream left, Stream right, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // DiffPlex 1.9.0 的公共 API 均以整段文本为输入（行数组无法直接喂入），
        // 故保留 Join 行为；本重载的优化点是读取侧的限长检查不再逐行扫描。
        // DiffPlex 1.9.0's public APIs all take whole text (line arrays cannot be fed
        // directly), so keep the Join; this overload's win is the read-side size bound
        // without per-line rescans.
        var leftLines = ReadLinesBounded(left, ct);
        var rightLines = ReadLinesBounded(right, ct);
        return DiffLines(string.Join('\n', leftLines), string.Join('\n', rightLines));
    }

    private static List<TextDiffLine> Align(DiffResult result)
    {
        var lines = new List<TextDiffLine>(result.PiecesOld.Count + result.PiecesNew.Count);
        var oldPos = 0;
        var newPos = 0;

        foreach (var block in result.DiffBlocks)
        {
            // 块前的相等区间。 Equal (unchanged) region before this block.
            while (oldPos < block.DeleteStartA)
            {
                lines.Add(new TextDiffLine
                {
                    Kind = DiffLineKind.Equal,
                    LeftLine = oldPos + 1,
                    RightLine = newPos + 1,
                    LeftText = result.PiecesOld[oldPos],
                    RightText = result.PiecesNew[newPos],
                });
                oldPos++;
                newPos++;
            }

            if (block.DeleteCountA == 0)
            {
                // Insert → Added（右侧行号）。 Insert-only block → Added (right line numbers).
                for (var i = 0; i < block.InsertCountB; i++)
                {
                    lines.Add(new TextDiffLine
                    {
                        Kind = DiffLineKind.Added,
                        LeftLine = 0,
                        RightLine = block.InsertStartB + i + 1,
                        LeftText = string.Empty,
                        RightText = result.PiecesNew[block.InsertStartB + i],
                    });
                }
            }
            else if (block.InsertCountB == 0)
            {
                // Delete → Deleted（左侧行号）。 Delete-only block → Deleted (left line numbers).
                for (var i = 0; i < block.DeleteCountA; i++)
                {
                    lines.Add(new TextDiffLine
                    {
                        Kind = DiffLineKind.Deleted,
                        LeftLine = block.DeleteStartA + i + 1,
                        RightLine = 0,
                        LeftText = result.PiecesOld[block.DeleteStartA + i],
                        RightText = string.Empty,
                    });
                }
            }
            else
            {
                // Replace → Modified（两侧行号，缺少的一侧行号为 0、文本为空串）。
                // Replace block → Modified (both-side line numbers; missing side is 0 and empty text).
                var count = Math.Max(block.DeleteCountA, block.InsertCountB);
                for (var i = 0; i < count; i++)
                {
                    var hasLeft = i < block.DeleteCountA;
                    var hasRight = i < block.InsertCountB;
                    lines.Add(new TextDiffLine
                    {
                        Kind = DiffLineKind.Modified,
                        LeftLine = hasLeft ? block.DeleteStartA + i + 1 : 0,
                        RightLine = hasRight ? block.InsertStartB + i + 1 : 0,
                        LeftText = hasLeft ? result.PiecesOld[block.DeleteStartA + i] : string.Empty,
                        RightText = hasRight ? result.PiecesNew[block.InsertStartB + i] : string.Empty,
                    });
                }
            }

            oldPos = block.DeleteStartA + block.DeleteCountA;
            newPos = block.InsertStartB + block.InsertCountB;
        }

        // 末尾的相等区间。 Trailing equal (unchanged) region.
        while (oldPos < result.PiecesOld.Count)
        {
            lines.Add(new TextDiffLine
            {
                Kind = DiffLineKind.Equal,
                LeftLine = oldPos + 1,
                RightLine = newPos + 1,
                LeftText = result.PiecesOld[oldPos],
                RightText = result.PiecesNew[newPos],
            });
            oldPos++;
            newPos++;
        }

        return lines;
    }

    private static List<string> ReadLinesBounded(Stream stream, CancellationToken ct)
    {
        List<string> lines = [];
        long totalBytes = 0;

        // 可 seek 的流（文件场景）一次性按流剩余长度做字节上限检查，消除每行
        // Encoding.UTF8.GetByteCount 的重复全文扫描；非 seek 流回退为逐行累计。
        // Seekable streams (the file case) check the byte cap once against the remaining
        // stream length, removing the per-line Encoding.UTF8.GetByteCount rescan;
        // non-seekable streams fall back to per-line accumulation.
        var perLineCounting = !stream.CanSeek;
        if (!perLineCounting && stream.Length - stream.Position > MaxDiffBytes)
        {
            throw new FormatException($"Text stream exceeds the {MaxDiffBytes} byte limit.");
        }

        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: true);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (lines.Count >= MaxDiffLines)
            {
                throw new FormatException($"Text stream exceeds the {MaxDiffLines} line limit.");
            }

            if (perLineCounting)
            {
                totalBytes += reader.CurrentEncoding.GetByteCount(line) + 2;
                if (totalBytes > MaxDiffBytes)
                {
                    throw new FormatException($"Text stream exceeds the {MaxDiffBytes} byte limit.");
                }
            }

            lines.Add(line);
        }

        return lines;
    }
}