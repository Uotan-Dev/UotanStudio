namespace UotanStudio.Compress.Codecs.Lz4;

/// <summary>
/// The named search-effort anchors of the frameless LZ4 block encoder, ordered from the fastest to the most exhaustive.
/// <para>无帧 LZ4 块编码器具名的搜索强度锚点，按从最快到最详尽排列。</para>
/// </summary>
/// <remarks>
/// The numeric values are the encoder's own anchors, not a caller's level scale. A format that publishes its
/// levels as <c>0</c>, <c>1</c>, <c>3</c>, <c>5</c> and <c>9</c> has to translate them onto this scale before calling
/// <see cref="Lz4BlockCodec.Encode"/>, and a wrong translation silently rewrites the emitted block instead of
/// failing, so the translation belongs to the format that owns the on-disk bytes.
/// <para>数值是编码器自身的锚点，而不是调用方的级别尺度。以 <c>0</c>、<c>1</c>、<c>3</c>、<c>5</c>、<c>9</c>
/// 对外暴露级别的格式，在调用 <see cref="Lz4BlockCodec.Encode"/> 之前必须先把级别翻译到本尺度；翻译错误不会报错，
/// 只会静默改写产出的块字节，因此该翻译留在持有磁盘字节的格式层。</para>
/// </remarks>
public enum Lz4BlockLevel
{
    /// <summary>
    /// The single-pass fast searcher, the anchor LZ4 uses when no effort is requested.
    /// <para>单遍快速搜索，即 LZ4 在未请求额外强度时使用的锚点。</para>
    /// </summary>
    Fast = 0,

    /// <summary>
    /// The first high-complexity searcher, trading CPU for a denser match chain.
    /// <para>第一档高复杂度搜索，用 CPU 换取更密的匹配链。</para>
    /// </summary>
    High = 3,

    /// <summary>
    /// The second high-complexity searcher, with a longer match chain than <see cref="High"/>.
    /// <para>第二档高复杂度搜索，匹配链长于 <see cref="High"/>。</para>
    /// </summary>
    VeryHigh = 6,

    /// <summary>
    /// The third high-complexity searcher, the deepest chain before the exhaustive anchor.
    /// <para>第三档高复杂度搜索，是穷尽锚点之前最深的匹配链。</para>
    /// </summary>
    Ultra = 9,

    /// <summary>
    /// The exhaustive searcher, the highest effort the block encoder exposes.
    /// <para>穷尽式搜索，块编码器暴露的最高强度。</para>
    /// </summary>
    Maximum = 12,
}