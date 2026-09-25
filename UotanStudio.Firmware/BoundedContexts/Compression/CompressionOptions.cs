namespace UotanStudio.Firmware.BoundedContexts.Compression;

/// <summary>
/// Optional codec tuning knobs accepted by <see cref="CompressionService"/>; every member is nullable so an unset value keeps the codec default.
/// <para><see cref="CompressionService"/> 接受的可选压缩调优参数；每个成员均可空，未设置时保持对应编解码器的默认值。</para>
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// Unified compression level on the 0-9 scale, where 0 means no compression and 9 the smallest output.
    /// <para>0-9 的统一压缩等级，0 表示不压缩，9 表示最小输出。</para></summary>
    /// <value>
    /// The level handed to the per-codec mapper when a format is compressed: 0-9 bands for gzip, zlib and deflate, Brotli quality 0-11, LZ4 level 0-12, Zstandard level with 3 as the fallback; <see langword="null"/> selects that codec default and is ignored by formats with a fixed level such as LZMA, XZ and bzip2.
    /// <para>压缩某种格式时交给对应编解码器映射器的等级：gzip、zlib、deflate 按 0-9 分档，Brotli 质量 0-11，LZ4 等级 0-12，Zstandard 缺省为 3；为 <see langword="null"/> 时采用该编解码器默认值，LZMA、XZ、bzip2 等固定等级格式会忽略该值。</para></value>
    public int? Level { get; set; }

    /// <summary>
    /// LZMA2 dictionary window size in bytes used by the XZ encoder.
    /// <para>XZ 编码器使用的 LZMA2 字典窗口大小，单位为字节。</para></summary>
    /// <value>
    /// A caller-chosen window size; <see langword="null"/> falls back to 8 MiB (<c>1 &lt;&lt; 23</c>). Other formats do not read this member, and the value is passed to the encoder without further range checks.
    /// <para>由调用方指定的窗口大小；为 <see langword="null"/> 时回退到 8 MiB（<c>1 &lt;&lt; 23</c>）。其他格式不读取该成员；该值不做额外范围检查，直接传给编码器。</para></value>
    public uint? DictionarySize { get; set; }

    /// <summary>
    /// Upper bound on worker tasks for chunked compression.
    /// <para>分块压缩时工作线程任务数的上限。</para></summary>
    /// <value>
    /// <see langword="null"/> or a value of 1 and below keeps the sequential path; above 1 the input is split into 1 MiB chunks (64 KiB for LZ4 block frames) that are compressed in parallel and concatenated in chunk order, producing bytes identical to the sequential result. The effective degree is capped by the chunk count, and formats without chunking ignore this member.
    /// <para>为 <see langword="null"/> 或小于等于 1 时保持串行路径；大于 1 时把输入切分为 1 MiB 块（LZ4 块帧为 64 KiB）并行压缩并按块顺序拼接，输出字节与串行结果完全一致。实际并行度受块数量上限约束，不支持分块的格式忽略该成员。</para></value>
    public int? MaxDegreeOfParallelism { get; set; }
}