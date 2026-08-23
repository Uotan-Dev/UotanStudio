namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩选项。
/// <para>Compression options.</para>
/// <para>各格式按其自身语义解释这些选项；不支持的选项会被忽略。</para>
/// <para>Each format interprets these options with its own semantics; unsupported options are ignored.</para>
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// 压缩级别。
    /// <para>Compression level.</para>
    /// <para>gzip/zlib/deflate/brotli/bzip2：0-9（0=不压缩，9=最高）；zstd：-5..22；lz4：0-12；null 表示使用默认级别。</para>
    /// <para>gzip/zlib/deflate/brotli/bzip2: 0-9 (0=stored, 9=best); zstd: -5..22; lz4: 0-12; null means the format default.</para>
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// 字典大小（字节），仅 lzma / xz 使用；null 表示使用格式默认值。
    /// <para>Dictionary size in bytes, used only by lzma / xz; null means the format default.</para>
    /// </summary>
    public uint? DictionarySize { get; set; }

    /// <summary>
    /// 编码端多核并行度：null 或 1 表示串行（默认）；&gt;1 时对支持分块的编码器按块并行压缩。
    /// 仅作用于编码端；解码端始终单线程，但能解回并行产出的多成员流。
    /// <para>Encoding-side multi-core parallelism: null or 1 means sequential (default);
    /// &gt;1 compresses supported encoders in parallel by chunks. Applies to encoding only;
    /// decoding stays single-threaded but can decode the multi-member streams produced.</para>
    /// <para>
    /// 行为分两类：(a) 自研 xz/lzma2 按固定窗口并行，输出与串行**逐字节一致**；
    /// (b) gzip/zlib/deflate/bzip2/zstd 按 1 MiB 块生成**独立成员/帧**后按序拼接，
    /// 输出为确定性（同输入同参数同结果）且格式合法、可用标准解压器解开的**多成员流**，
    /// 但与单成员串行输出的字节不同。brotli 因 .NET 解码器不支持串联流、lz4 因 K4os
    /// 解码器对串联帧不可靠，暂不并行。
    /// </para>
    /// <para>
    /// Two behaviors: (a) the self-implemented xz/lzma2 parallelizes over fixed windows
    /// with output **byte-identical** to sequential; (b) gzip/zlib/deflate/bzip2/zstd
    /// compress 1 MiB chunks into **independent members/frames** concatenated in order —
    /// deterministic (same input, options and result) and format-valid multi-member
    /// output decodable by standard tools, but byte-different from single-member
    /// sequential output. brotli is not parallelized because the .NET decoder does not
    /// support concatenated streams, and lz4 is not parallelized because the K4os
    /// decoder does not reliably decode concatenated frames.
    /// </para>
    /// </summary>
    public int? MaxDegreeOfParallelism { get; set; }
}