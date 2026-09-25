namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;

/// <summary>
/// Canonical selector tokens the packing panel writes for the EROFS codec, and the mapping from a
/// token to the on-disk algorithm plus the LZ4HC block-encoder flag. EROFS records no encoder
/// identity, so <see cref="Lz4HighCompression"/> only trades build time for density while the disk
/// codec stays <see cref="ErofsCompressionAlgorithm.Lz4"/>.
/// <para>打包面板写入的 EROFS 编解码器选择令牌，以及令牌到磁盘算法与 LZ4HC 块编码器标志的映射。
/// EROFS 不记录编码器身份，因此 <see cref="Lz4HighCompression"/> 只用构建时间换取压缩率，磁盘编解码器
/// 仍是 <see cref="ErofsCompressionAlgorithm.Lz4"/>。</para>
/// </summary>
public static class ErofsAlgorithmSelectors
{
    /// <summary>Fast LZ4 block matcher.<para>快速 LZ4 块匹配器。</para></summary>
    public const string Lz4 = "lz4";

    /// <summary>LZ4 with the high-compression (HC) block search.<para>带 HC 高压缩搜索的 LZ4。</para></summary>
    public const string Lz4HighCompression = "lz4hc";

    /// <summary>MicroLZMA payload.<para>MicroLZMA 数据。</para></summary>
    public const string Lzma = "lzma";

    /// <summary>DEFLATE (zlib) payload.<para>DEFLATE（zlib）数据。</para></summary>
    public const string Deflate = "deflate";

    /// <summary>Zstandard payload.<para>Zstandard 数据。</para></summary>
    public const string Zstd = "zstd";

    /// <summary>The picker choices in display order.<para>按显示顺序排列的选项。</para></summary>
    /// <value>Read-only list <c>lz4, lz4hc, lzma, deflate, zstd</c>, shared across threads.
    /// <para>只读列表 <c>lz4, lz4hc, lzma, deflate, zstd</c>，可跨线程共享。</para></value>
    public static IReadOnlyList<string> Ordered { get; } = [Lz4, Lz4HighCompression, Lzma, Deflate, Zstd];

    /// <summary>
    /// Normalizes a free-form selector to one of <see cref="Ordered"/>, falling back to
    /// <see cref="Lz4"/> for unknown or empty input so untrusted metadata never selects an
    /// unintended codec.
    /// <para>将自由形式的选择器规范化为 <see cref="Ordered"/> 之一；空或未知输入回退到 <see cref="Lz4"/>，
    /// 使不可信元数据永远不会选到非预期编解码器。</para>
    /// </summary>
    /// <param name="selector">Token matched case-insensitively against <see cref="Ordered"/>; <see langword="null"/>
    /// or any unmatched value maps to <see cref="Lz4"/>.
    /// <para>与 <see cref="Ordered"/> 逐项做忽略大小写匹配的令牌；<see langword="null"/> 或未匹配的值映射为
    /// <see cref="Lz4"/>。</para></param>
    /// <returns>The canonical token from <see cref="Ordered"/>.
    /// <para><see cref="Ordered"/> 中的规范令牌。</para></returns>
    public static string Normalize(string? selector)
        => Ordered.FirstOrDefault(token => string.Equals(token, selector, StringComparison.OrdinalIgnoreCase)) ?? Lz4;

    /// <summary>
    /// Resolves a selector into the disk algorithm and the LZ4HC encoder flag.
    /// <para>将选择器解析为磁盘算法与 LZ4HC 编码器标志。</para>
    /// </summary>
    /// <param name="selector">Free-form token normalized by <see cref="Normalize"/>; <see langword="null"/> or
    /// unknown input resolves to plain LZ4.
    /// <para>由 <see cref="Normalize"/> 规范化的自由形式令牌；<see langword="null"/> 或未知输入解析为普通 LZ4。</para></param>
    /// <returns>A pair of the on-disk <see cref="ErofsCompressionAlgorithm"/> and whether the LZ4 payload
    /// must use the HC encoder; the flag is only <see langword="true"/> for the <c>lz4hc</c> token.
    /// <para>磁盘 <see cref="ErofsCompressionAlgorithm"/> 与 LZ4 数据是否使用 HC 编码器的组合；该标志仅在
    /// <c>lz4hc</c> 令牌时为 <see langword="true"/>。</para></returns>
    public static (ErofsCompressionAlgorithm Algorithm, bool Lz4HighCompression) Parse(string? selector)
        => Normalize(selector) switch
        {
            Lz4HighCompression => (ErofsCompressionAlgorithm.Lz4, true),
            Lzma => (ErofsCompressionAlgorithm.Lzma, false),
            Deflate => (ErofsCompressionAlgorithm.Deflate, false),
            Zstd => (ErofsCompressionAlgorithm.Zstd, false),
            _ => (ErofsCompressionAlgorithm.Lz4, false),
        };
}