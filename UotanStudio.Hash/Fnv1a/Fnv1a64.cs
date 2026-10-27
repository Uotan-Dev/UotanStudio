namespace UotanStudio.Hash.Fnv1a;

/// <summary>
/// Fowler-Noll-Vo 1a 64-bit mixing for identity keys that are compared in memory and never stored as a format digest.
/// <para>Fowler-Noll-Vo 1a 64 位混合，用于只在内存中比较、从不作为格式摘要落盘的身份键。</para>
/// </summary>
/// <remarks>
/// <para>The library forwards standard CRCs and digests to the BCL; FNV is not one of those, so it is implemented here
/// rather than left in a caller. The character overload consumes UTF-16 code units, which is the form the device
/// projection keys already have, while the byte overload serves callers that hold UTF-8 bytes.</para>
/// <para>本库把标准 CRC 与摘要转发给 BCL；FNV 不属于其中之一，因此在本库实现而不是留在调用方。字符重载消费
/// UTF-16 码元，正是设备投影键已有的形态；字节重载服务于持有 UTF-8 字节的调用方。</para>
/// </remarks>
public static class Fnv1a64
{
    /// <summary>The FNV-1a 64-bit offset basis. <para>FNV-1a 64 位的初始偏移基值。</para></summary>
    public const ulong OffsetBasis = 14695981039346656037UL;

    /// <summary>The FNV-1a 64-bit prime. <para>FNV-1a 64 位的质数。</para></summary>
    public const ulong Prime = 1099511628211UL;

    /// <summary>
    /// Hashes a UTF-16 code-unit sequence from the offset basis.
    /// <para>从偏移基值开始哈希一段 UTF-16 码元序列。</para>
    /// </summary>
    /// <param name="text">Characters to mix; an empty span returns <see cref="OffsetBasis"/>.
    /// <para>待混合的字符；空区间返回 <see cref="OffsetBasis"/>。</para></param>
    /// <returns>The mixed 64-bit value. <para>混合后的 64 位值。</para></returns>
    public static ulong Compute(ReadOnlySpan<char> text) => Compute(text, OffsetBasis);

    /// <summary>
    /// Continues a UTF-16 FNV-1a sequence from a previous result, so a caller can chain segments without
    /// materialising their concatenation.
    /// <para>从上次结果继续 UTF-16 的 FNV-1a 序列，使调用方无需拼接即可串联多段。</para>
    /// </summary>
    /// <param name="text">Characters to mix. <para>待混合的字符。</para></param>
    /// <param name="previous">The previous result or <see cref="OffsetBasis"/> to start fresh.
    /// <para>上次的结果，或从头开始时的 <see cref="OffsetBasis"/>。</para></param>
    /// <returns>The mixed 64-bit value. <para>混合后的 64 位值。</para></returns>
    public static ulong Compute(ReadOnlySpan<char> text, ulong previous)
    {
        var hash = previous;
        foreach (var character in text)
        {
            hash = unchecked((hash ^ character) * Prime);
        }

        return hash;
    }

    /// <summary>
    /// Hashes a byte sequence from the offset basis.
    /// <para>从偏移基值开始哈希一段字节序列。</para>
    /// </summary>
    /// <param name="data">Bytes to mix; an empty span returns <see cref="OffsetBasis"/>.
    /// <para>待混合的字节；空区间返回 <see cref="OffsetBasis"/>。</para></param>
    /// <returns>The mixed 64-bit value. <para>混合后的 64 位值。</para></returns>
    public static ulong Compute(ReadOnlySpan<byte> data) => Compute(data, OffsetBasis);

    /// <summary>
    /// Continues a byte-oriented FNV-1a sequence from a previous result.
    /// <para>从上次结果继续按字节的 FNV-1a 序列。</para>
    /// </summary>
    /// <param name="data">Bytes to mix. <para>待混合的字节。</para></param>
    /// <param name="previous">The previous result or <see cref="OffsetBasis"/> to start fresh.
    /// <para>上次的结果，或从头开始时的 <see cref="OffsetBasis"/>。</para></param>
    /// <returns>The mixed 64-bit value. <para>混合后的 64 位值。</para></returns>
    public static ulong Compute(ReadOnlySpan<byte> data, ulong previous)
    {
        var hash = previous;
        foreach (var value in data)
        {
            hash = unchecked((hash ^ value) * Prime);
        }

        return hash;
    }

    /// <summary>
    /// Mixes one scalar value into a running result, which is how a caller separates two adjacent segments.
    /// <para>把一个标量值混入当前结果，调用方以此分隔两个相邻片段。</para>
    /// </summary>
    /// <param name="previous">The running result. <para>当前结果。</para></param>
    /// <param name="value">Separator or scalar to fold in. <para>要折入的分隔值或标量。</para></param>
    /// <returns>The mixed 64-bit value. <para>混合后的 64 位值。</para></returns>
    public static ulong Mix(ulong previous, uint value) => unchecked((previous ^ value) * Prime);
}