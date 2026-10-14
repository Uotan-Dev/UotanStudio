using ZstdSharp;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Frame-level Zstd block primitives over spans, backed by ZstdSharp, for filesystem formats that store a whole
/// zstd frame per block rather than a container with headers and checksums.
/// <para>基于 ZstdSharp、以 span 交换数据的帧级 Zstd 块原语，供按块存储完整 zstd 帧而非带头部与校验和容器的文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// <para>Only the compression side is exposed. The F2FS cluster reader still calls ZstdSharp's
/// <c>Unwrap(compressed, expectedSize)</c> directly because it depends on that overload's size-hint semantics: it
/// returns the array the library allocated for the hint and hands it back verbatim, so the stored array length is a
/// property of the hint rather than of the decoded content, and the coverage of that path assumes hint equals
/// content. A span-based decompress would silently change those array lengths with nothing in the suite catching the
/// difference, so it is intentionally absent until that reader is reworked around an explicit buffer.</para>
/// <para>The bytes <see cref="Compress"/> writes match the single-frame path of the array-level compressor for the
/// same level and payload: a zstd frame is destination-independent, so no content-size flag, content checksum or
/// window override is applied here and no frame is emitted for an empty payload.</para>
/// </remarks>
internal static class ZstdBlockCodec
{
    private const int MinimumLevel = -5;
    private const int MaximumLevel = 22;
    private const int DefaultLevel = 3;
    private const int SmallSourceThreshold = 128 << 10;

    /// <summary>
    /// Conservative zstd bound for a source of the given length, the ZSTD_compressBound formula.
    /// <para>指定长度源数据的保守 zstd 上界，即 ZSTD_compressBound 公式。</para>
    /// </summary>
    /// <param name="sourceLength">Length in bytes of the block to compress; it must already be a non-negative value
    /// bounded by the calling format, and the arithmetic here does not probe for overflow.
    /// <para>待压缩块的字节长度；调用方须保证其为非负且受自身格式上限约束的值，此处的算术不做溢出探测。</para></param>
    /// <returns>The upper bound on the bytes <see cref="Compress"/> can write: the source length plus 1/256 of it,
    /// lifted to at least the 128 KiB frame overhead allowance so short sources still get a full block's room.
    /// <para><see cref="Compress"/> 可能写出的字节数上界：源长度加上其 1/256，并在长度不足时抬到 128 KiB 帧开销余量，
    /// 使短源也保留整块所需的容量。</para></returns>
    internal static int MaximumCompressedSize(int sourceLength)
    {
        var bound = sourceLength + (sourceLength >> 8);
        if (sourceLength < SmallSourceThreshold)
        {
            bound += SmallSourceThreshold - sourceLength;
        }

        return bound;
    }

    /// <summary>
    /// Compresses a block into a single standalone zstd frame.
    /// <para>把整块数据压缩为一个独立的 zstd 帧。</para>
    /// </summary>
    /// <param name="source">Block payload to compress; an empty payload produces no frame at all and no
    /// compression context is created for it.
    /// <para>待压缩的块载荷；空载荷不生成任何帧，也不会为其创建压缩上下文。</para></param>
    /// <param name="destination">Destination span; the frame is written from its start and no byte goes past its
    /// length, so a span too small to hold the frame is reported as a failure.
    /// <para>目标 span；帧从其起始处写入且不会越过长度边界，容量不足以容纳整帧时按失败处理。</para></param>
    /// <param name="level">Compression level, clamped into the zstd range from <c>-5</c> to <c>22</c> before the
    /// context is created; the default matches the level the array-level compressor uses when no options are given.
    /// <para>压缩级别，创建上下文前先夹取到 zstd 的 <c>-5</c> 至 <c>22</c> 区间；默认值与无选项时数组级压缩器使用的级别一致。</para></param>
    /// <returns>Number of frame bytes written into <paramref name="destination"/>, or <c>0</c> for an empty source.
    /// <para>写入 <paramref name="destination"/> 的帧字节数；源为空时返回 <c>0</c>。</para></returns>
    /// <exception cref="CompressionException">The zstd context rejected the parameters or the frame did not fit
    /// <paramref name="destination"/>; the underlying ZstdSharp error is the inner exception.
    /// <para>zstd 上下文拒绝参数，或帧无法容纳于 <paramref name="destination"/>；ZstdSharp 的原始错误为内部异常。</para></exception>
    internal static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level = DefaultLevel)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        try
        {
            using var compressor = new Compressor(Math.Clamp(level, MinimumLevel, MaximumLevel));
            return compressor.Wrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD block compression failed. ZSTD 块压缩失败。", ex);
        }
    }
}