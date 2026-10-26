namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Resource bounds applied while decoding Apple property lists from untrusted input.
/// <para>解码来自不可信输入的 Apple 属性列表时应用的资源上限。</para>
/// </summary>
/// <param name="MaxTotalBytes">Total encoded payload upper bound in bytes; the caller must also bound the buffer itself. <para>编码载荷的总字节上限；调用方仍需自行限制缓冲区大小。</para></param>
/// <param name="MaxDepth">Maximum nesting depth of containers. <para>容器的最大嵌套深度。</para></param>
/// <param name="MaxElements">Maximum number of values across the whole document. <para>整个文档中值的最大数量。</para></param>
/// <param name="MaxCollectionCount">Maximum number of entries in a single collection. <para>单个集合中条目的最大数量。</para></param>
public sealed record ApplePlistLimits(
    int MaxTotalBytes = 32 * 1024 * 1024,
    int MaxDepth = 64,
    long MaxElements = 1 << 20,
    long MaxCollectionCount = 1 << 20)
{
    /// <summary>Gets the default limits used when none are supplied. <para>未提供时的默认上限。</para></summary>
    public static ApplePlistLimits Default { get; } = new();

    /// <summary>
    /// Returns the strictest of this instance and <paramref name="other"/> for every field.
    /// <para>返回本实例与 <paramref name="other"/> 每个字段中更严格的一个。</para>
    /// </summary>
    /// <param name="other">The limits to intersect with. <para>参与求交的上限。</para></param>
    /// <returns>A record whose bounds never exceed either input. <para>任何边界都不超过两个输入的记录。</para></returns>
    public ApplePlistLimits Intersect(ApplePlistLimits other) => new(
        Math.Min(MaxTotalBytes, other.MaxTotalBytes),
        Math.Min(MaxDepth, other.MaxDepth),
        Math.Min(MaxElements, other.MaxElements),
        Math.Min(MaxCollectionCount, other.MaxCollectionCount));
}