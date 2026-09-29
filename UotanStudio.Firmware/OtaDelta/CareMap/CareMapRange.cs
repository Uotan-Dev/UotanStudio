namespace UotanStudio.Firmware.OtaDelta.CareMap;

/// <summary>
/// Half-open block range <c>[Start, End)</c> inside one partition, expressed in care-map blocks.
/// <para>分区内的半开区间 <c>[Start, End)</c>，以 care map 块为单位。</para>
/// </summary>
/// <param name="Start">
/// First covered block index. <para>首个被覆盖的块索引。</para>
/// </param>
/// <param name="End">
/// One past the last covered block index; always greater than <paramref name="Start"/>.
/// <para>最后一个被覆盖块索引之后的一位，恒大于 <paramref name="Start"/>。</para>
/// </param>
public readonly record struct CareMapRange(long Start, long End)
{
    /// <summary>Gets the number of blocks covered by the range.
    /// <para>获取该区间覆盖的块数量。</para></summary>
    /// <value>The difference between the exclusive end and the inclusive start.
    /// <para>独占端点与起始端点之差。</para></value>
    public long Count => End - Start;

    /// <summary>Returns the inclusive <c>start-end</c> text form used by transfer lists.
    /// <para>返回 transfer list 使用的包含式 <c>start-end</c> 文本形式。</para></summary>
    /// <returns>The range rendered with an inclusive final block index.
    /// <para>以包含式末块索引呈现的区间文本。</para></returns>
    public override string ToString() => Count == 1
        ? Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Start}-{End - 1}");
}