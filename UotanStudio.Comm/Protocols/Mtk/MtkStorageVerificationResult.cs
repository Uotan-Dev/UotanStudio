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
/// <param name="ExpectedBytes">
/// The requested range length, or null when the caller did not supply one.
/// <para>请求的范围长度；调用方未提供时为 null。</para>
/// </param>
public sealed record MtkStorageVerificationResult(long ComparedBytes, long? FirstMismatchOffset, long? ExpectedBytes = null)
{
    /// <summary>
    /// Gets whether the whole requested range was compared and matched.
    /// <para>获取请求范围是否已全部比对且一致。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when no mismatch was observed, at least one byte was compared, and the
    /// compared count equals <see cref="ExpectedBytes"/> whenever that bound is known; a short read that
    /// reports no mismatch is therefore not a match.
    /// <para>仅在未观察到差异、至少比对了一个字节，且在已知 <see cref="ExpectedBytes"/> 时比对字节数与之一致时
    /// 返回 <see langword="true"/>；因此"读取不足但无差异"不构成匹配。</para>
    /// </value>
    public bool Matches => FirstMismatchOffset is null
        && ComparedBytes > 0
        && (ExpectedBytes is not { } expected || ComparedBytes == expected);
}