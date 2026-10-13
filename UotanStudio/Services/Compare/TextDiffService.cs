using System.Text;
using DiffPlex;
using DiffPlex.Model;

namespace UotanStudio.Services.Compare;

/// <summary>
/// Text diff line kind.
/// <para>文本差异行类型。</para>
/// </summary>
public enum DiffLineKind
{
    /// <summary>
    /// A line present and identical on both sides.
    /// <para>两侧均存在且内容相同的行。</para>
    /// </summary>
    Equal,

    /// <summary>
    /// A line added on the right side only.
    /// <para>仅右侧新增的行。</para>
    /// </summary>
    Added,

    /// <summary>
    /// A line deleted from the left side only.
    /// <para>仅左侧删除的行。</para>
    /// </summary>
    Deleted,

    /// <summary>
    /// A line modified between the two sides.
    /// <para>两侧对应位置发生修改的行。</para>
    /// </summary>
    Modified,
}

/// <summary>
/// A single line of a text diff, carrying both side line numbers and text.
/// <para>文本差异的一行，携带两侧行号与文本。</para>
/// </summary>
public sealed class TextDiffLine
{
    /// <summary>
    /// Line kind.
    /// <para>行类型。</para>
    /// </summary>
    /// <value>The diff kind of this line. <para>该行的差异类型。</para></value>
    public required DiffLineKind Kind { get; init; }

    /// <summary>
    /// Left line number (1-based); 0 when the line is absent on the left.
    /// <para>左侧行号（1-based）；该行在左侧缺失时为 0。</para>
    /// </summary>
    /// <value>Left line number counting from 1, or 0 when absent. <para>从 1 开始计数的左侧行号；缺失时为 0。</para></value>
    public int LeftLine { get; init; }

    /// <summary>
    /// Right line number (1-based); 0 when the line is absent on the right.
    /// <para>右侧行号（1-based）；该行在右侧缺失时为 0。</para>
    /// </summary>
    /// <value>Right line number counting from 1, or 0 when absent. <para>从 1 开始计数的右侧行号；缺失时为 0。</para></value>
    public int RightLine { get; init; }

    /// <summary>
    /// Left line text; empty string when absent.
    /// <para>左侧行文本；缺失时为空串。</para>
    /// </summary>
    /// <value>The left line text, empty when the line is absent. <para>左侧行文本；该行缺失时为空串。</para></value>
    public required string LeftText { get; init; }

    /// <summary>
    /// Right line text; empty string when absent.
    /// <para>右侧行文本；缺失时为空串。</para>
    /// </summary>
    /// <value>The right line text, empty when the line is absent. <para>右侧行文本；该行缺失时为空串。</para></value>
    public required string RightText { get; init; }
}

/// <summary>
/// Text comparison service providing line-level diffs plus a stream overload bounded by line and byte limits.
/// <para>文本比对服务，提供行级差异与带行数、字节上限的流式重载。</para>
/// </summary>
public sealed class TextDiffService
{
    private static readonly Lazy<TextDiffService> LazyInstance = new(() => new TextDiffService());

    /// <summary>
    /// Gets the shared instance created on first access.
    /// <para>获取首次访问时创建的共享实例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static TextDiffService Instance => LazyInstance.Value;

    internal TextDiffService() { }

    private const int MaxDiffLines = 2_000_000;
    private const long MaxDiffBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Performs a line-level diff of two texts and aligns the diff blocks into diff lines: added,
    /// deleted, modified or equal, with line numbers counting from 1 and 0 on the missing side.
    /// <para>对两个文本执行行级差异，并把差异块对齐为差异行：新增、删除、修改或相等；行号从 1 起，缺失侧为 0。</para>
    /// </summary>
    /// <param name="leftText">Left text. <para>左侧文本。</para></param>
    /// <param name="rightText">Right text. <para>右侧文本。</para></param>
    /// <returns>The aligned list of diff lines. <para>对齐后的差异行列表。</para></returns>
    public List<TextDiffLine> DiffLines(string leftText, string rightText)
    {
        ArgumentNullException.ThrowIfNull(leftText);
        ArgumentNullException.ThrowIfNull(rightText);

        var result = new Differ().CreateLineDiffs(leftText, rightText, ignoreWhitespace: false);
        return Align(result);
    }

    /// <summary>
    /// Reads both streams line by line, keeping the original line text without its line ending, and
    /// performs a bounded line-level diff over what was read.
    /// <para>从两个流逐行读取，保留不含换行符的原始行文本，并对读到的内容执行有上限的行级差异。</para>
    /// </summary>
    /// <param name="left">Left text stream. <para>左侧文本流。</para></param>
    /// <param name="right">Right text stream. <para>右侧文本流。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The aligned list of diff lines. <para>对齐后的差异行列表。</para></returns>
    /// <exception cref="FormatException">A stream exceeds the line or byte limit. <para>某个流超出行数或字节上限。</para></exception>
    public List<TextDiffLine> DiffLines(Stream left, Stream right, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

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