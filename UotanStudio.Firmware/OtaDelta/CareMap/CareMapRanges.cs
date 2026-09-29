using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.OtaDelta.CareMap;

/// <summary>
/// Converts between the care-map range text and normalized half-open ranges. The text form is a
/// comma-separated list whose first token counts the remaining integer tokens, which then appear as
/// <c>start,end</c> pairs with exclusive ends.
/// <para>在 care map 区间文本与规范化的半开区间之间转换。文本为逗号分隔的整数列表，首词统计其余整数的个数，
/// 其后按 <c>start,end</c> 成对出现且端点独占。</para>
/// </summary>
public static class CareMapRanges
{
    /// <summary>
    /// Upper bound on ranges per care-map entry, guarding a hostile range string against unbounded work.
    /// <para>单条 care map 记录的区间数量上限，防止敌意区间文本造成无界工作。</para>
    /// </summary>
    /// <value>65,536 ranges. <para>65,536 个区间。</para></value>
    public const int MaximumRangeCount = 65_536;

    /// <summary>Renders ranges in the care-map text form.
    /// <para>按 care map 文本形式呈现区间。</para></summary>
    /// <param name="ranges">Ranges to render; they are normalized first, and the collection must not be empty.
    /// <para>待呈现的区间；呈现前先规范化，且集合不得为空。</para></param>
    /// <returns>The count-prefixed, comma-separated range text.
    /// <para>以计数开头、逗号分隔的区间文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="ranges"/> is empty or holds an inverted range.
    /// <para><paramref name="ranges"/> 为空或含有反向区间。</para></exception>
    public static string Format(IReadOnlyList<CareMapRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var normalized = Normalize(ranges);
        if (normalized.Count == 0)
        {
            throw new ArgumentException("A care map needs at least one block range.", nameof(ranges));
        }

        var buffer = new StringBuilder();
        buffer.Append(CultureInfo.InvariantCulture, $"{normalized.Count * 2}");
        foreach (var range in normalized)
        {
            buffer.Append(CultureInfo.InvariantCulture, $",{range.Start},{range.End}");
        }

        return buffer.ToString();
    }

    /// <summary>Parses the care-map text form into ranges.
    /// <para>把 care map 文本形式解析为区间。</para></summary>
    /// <param name="text">Range text produced by <see cref="Format"/>. <para>由 <see cref="Format"/> 生成的区间文本。</para></param>
    /// <returns>The parsed ranges in the order and shape the producer supplied.
    /// <para>按生产者给出的顺序与形状解析出的区间。</para></returns>
    /// <exception cref="FormatException">The token count is missing, odd, or does not match the tokens that follow,
    /// a token is not a non-negative integer, or a range is empty.
    /// <para>计数缺失、为奇数或与后续 token 数量不符；某个 token 不是非负整数；或某个区间为空。</para></exception>
    public static IReadOnlyList<CareMapRange> Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var tokens = text.Split(',');
        if (tokens.Length < 3)
        {
            throw new FormatException($"Invalid care-map range text: {text}");
        }

        if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared) || declared <= 0)
        {
            throw new FormatException($"Care-map range count is not a positive integer: {tokens[0]}");
        }

        if (declared % 2 != 0)
        {
            throw new FormatException("Care-map range count must be even.");
        }

        if (declared / 2 > MaximumRangeCount)
        {
            throw new FormatException($"Care-map range count exceeds {MaximumRangeCount} ranges.");
        }

        if (declared != tokens.Length - 1)
        {
            throw new FormatException($"Care-map range count {declared} does not match {tokens.Length - 1} tokens.");
        }

        var ranges = new CareMapRange[declared / 2];
        for (var index = 0; index < ranges.Length; index++)
        {
            var start = ParseBlock(tokens[index * 2 + 1]);
            var end = ParseBlock(tokens[index * 2 + 2]);
            if (end <= start)
            {
                throw new FormatException("Care-map ranges must have an exclusive end beyond their start.");
            }

            ranges[index] = new CareMapRange(start, end);
        }

        return ranges;
    }

    /// <summary>
    /// Merges overlapping or adjacent ranges and returns them ordered by start block.
    /// <para>合并重叠或相邻的区间，并按起始块排序返回。</para>
    /// </summary>
    /// <param name="ranges">Ranges to normalize; each must end after it starts.
    /// <para>待规范化的区间；每个区间的终点都必须晚于起点。</para></param>
    /// <returns>The smallest ordered set of ranges covering the same blocks.
    /// <para>覆盖相同块集合的最小有序区间组。</para></returns>
    /// <exception cref="ArgumentException">A range is empty or inverted. <para>某个区间为空或反向。</para></exception>
    public static IReadOnlyList<CareMapRange> Normalize(IReadOnlyList<CareMapRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count > MaximumRangeCount)
        {
            throw new ArgumentException($"At most {MaximumRangeCount} ranges are accepted.", nameof(ranges));
        }

        var ordered = new List<CareMapRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.End <= range.Start)
            {
                throw new ArgumentException($"Range {range.Start}-{range.End} covers no block.", nameof(ranges));
            }

            ordered.Add(range);
        }

        ordered.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<CareMapRange>(ordered.Count);
        foreach (var range in ordered)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var tail = merged[^1];
                merged[^1] = new CareMapRange(tail.Start, Math.Max(tail.End, range.End));
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    /// <summary>
    /// Intersects two range collections, dropping every block outside the other collection.
    /// <para>对两组区间求交集，丢弃另一组之外的所有块。</para>
    /// </summary>
    /// <param name="left">First operand, already normalized or not. <para>第一个操作数，是否已规范化均可。</para></param>
    /// <param name="right">Second operand, already normalized or not. <para>第二个操作数，是否已规范化均可。</para></param>
    /// <returns>The normalized intersection, which may be empty. <para>规范化后的交集，可能为空。</para></returns>
    public static IReadOnlyList<CareMapRange> Intersect(IReadOnlyList<CareMapRange> left, IReadOnlyList<CareMapRange> right)
    {
        var first = Normalize(left);
        var second = Normalize(right);
        var result = new List<CareMapRange>();
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < first.Count && rightIndex < second.Count)
        {
            var start = Math.Max(first[leftIndex].Start, second[rightIndex].Start);
            var end = Math.Min(first[leftIndex].End, second[rightIndex].End);
            if (end > start)
            {
                result.Add(new CareMapRange(start, end));
            }

            if (first[leftIndex].End < second[rightIndex].End)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return result;
    }

    private static long ParseBlock(string token)
    {
        if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new FormatException($"Care-map block index is not a non-negative integer: {token}");
        }

        return value;
    }
}