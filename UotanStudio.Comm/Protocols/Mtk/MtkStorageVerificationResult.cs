namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Byte comparison evidence from a completely received storage range.
/// <para>来自完整接收到的存储范围的逐字节比对证据。</para>
/// </summary>
/// <param name="ComparedBytes">
/// The number of bytes compared after successful protocol completion.
/// <para>协议成功完成后已比对的字节数。</para>
/// </param>
/// <param name="FirstMismatchOffset">
/// The first differing byte relative to the requested range, or null if identical.
/// <para>相对于请求范围第一个不一致字节的位置；完全一致时为 null。</para>
/// </param>
public sealed record MtkStorageVerificationResult(long ComparedBytes, long? FirstMismatchOffset)
{
    /// <summary>
    /// Gets whether all bytes in the requested range matched the source.
    /// <para>获取请求范围内的所有字节是否都与源数据一致。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when no mismatch was observed; <see langword="false"/> when <c>FirstMismatchOffset</c> carries an offset.
    /// <para>未观察到差异时为 <see langword="true"/>；<c>FirstMismatchOffset</c> 携带偏移量时为 <see langword="false"/>。</para>
    /// </value>
    public bool Matches => FirstMismatchOffset is null;
}