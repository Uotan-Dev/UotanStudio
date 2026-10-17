using ZstdSharp;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Frame-level Zstd block primitives over spans, backed by ZstdSharp, for filesystem formats that store a whole
/// zstd frame per block rather than a container with headers and checksums.
/// <para>基于 ZstdSharp、以 span 交换数据的帧级 Zstd 块原语，供按块存储完整 zstd 帧而非带头部与校验和容器的文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// <para>Both directions are span-based. <see cref="Decompress"/> reuses a cached zstd decompression context so a
/// per-cluster loop does not rebuild a native context for every block, and it returns the number of bytes it
/// actually wrote because a span destination has no implicit size hint like the array-level
/// <c>Unwrap(compressed, expectedSize)</c> overload; readers that rely on that hint's array length stay on their
/// own path.</para>
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

        var clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
        var lease = CompressorCache.Rent(clamped);
        try
        {
            return lease.Wrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD block compression failed. ZSTD 块压缩失败。", ex);
        }
        finally
        {
            CompressorCache.Return(clamped, lease);
        }
    }

    /// <summary>
    /// Decompresses one standalone zstd frame into a caller-owned span and reports the produced byte count.
    /// <para>把一个独立的 zstd 帧解压到调用方持有的 span，并报告实际产出的字节数。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes; an empty source produces nothing and returns <c>0</c> without
    /// touching a decompression context. <para>完整的帧字节；空源不产出内容，也不接触解压上下文，直接返回 <c>0</c>。</para></param>
    /// <param name="destination">Destination span written from its start; it must be large enough for the whole
    /// decoded frame, and the caller uses the returned length rather than <paramref name="destination"/>'s length
    /// because the span carries no size hint. <para>从起始处写入的目标 span；其容量必须容纳整个解码帧，调用方应以返回长度
    /// 而非 <paramref name="destination"/> 的长度为准，因为 span 不携带尺寸提示。</para></param>
    /// <returns>The number of decoded bytes written into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解码字节数。</para></returns>
    /// <exception cref="CompressionException">The frame is malformed or the destination is too small; the
    /// underlying ZstdSharp error is the inner exception.
    /// <para>帧无效或目标容量不足；ZstdSharp 的原始错误为内部异常。</para></exception>
    internal static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        var lease = DecompressorCache.Rent();
        try
        {
            return lease.Unwrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD block decompression failed. ZSTD 块解压失败。", ex);
        }
        finally
        {
            DecompressorCache.Return(lease);
        }
    }

    /// <summary>
    /// Caches one zstd compression context per level so a per-cluster loop reuses the context instead of
    /// rebuilding it for every block.
    /// <para>按级别各缓存一个 zstd 压缩上下文，使逐 cluster 的循环复用上下文而不必每块重建。</para>
    /// </summary>
    /// <remarks>
    /// A zstd context carries the level's window state, so constructing one per block repeats that setup for every
    /// cluster of an image. The cache hands out an exclusive owner per call and stores it back on return; a second
    /// caller arriving while one is checked out constructs its own, so no caller ever waits on another. A context is
    /// never shared concurrently and is therefore never observed in a half-consumed state. A context displaced from
    /// a slot is idle by construction, because renting removes the slot entry under the same lock.
    /// <para>zstd 上下文承载该级别的窗口状态，每块新建就会让镜像的每个 cluster 重复一次搭建。该缓存每次借出
    /// 独占所有者并在归还时存回；借用期间来的第二个调用方自行新建，因此不会有调用方等待他人。上下文从不并发共享，
    /// 也就不会出现处于半消费状态被观测到的情况。被挤出槽位的上下文按构造必为空闲，因为借用会在同一把锁内移除槽位条目。</para>
    /// </remarks>
    private static class CompressorCache
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<int, Holder> Idle = [];

        internal static Compressor Rent(int level)
        {
            lock (Gate)
            {
                if (Idle.Remove(level, out var holder))
                {
                    return holder.Value;
                }
            }

            return new Compressor(level);
        }

        internal static void Return(int level, Compressor compressor)
        {
            Holder? displaced;
            lock (Gate)
            {
                Idle.Remove(level, out displaced);
                Idle[level] = new Holder(compressor);
            }

            displaced?.Value.Dispose();
        }

        private sealed class Holder(Compressor value)
        {
            internal Compressor Value { get; } = value;
        }
    }

    /// <summary>
    /// Reuses one zstd decompression context across calls, with the same exclusive-rental contract as
    /// <see cref="CompressorCache"/>.
    /// <para>跨调用复用同一个 zstd 解压上下文，独占借用契约与 <see cref="CompressorCache"/> 相同。</para>
    /// </summary>
    private static class DecompressorCache
    {
        private static readonly Lock Gate = new();
        private static Decompressor? Idle;

        internal static Decompressor Rent()
        {
            lock (Gate)
            {
                var idle = Idle;
                if (idle is not null)
                {
                    Idle = null;
                    return idle;
                }
            }

            return new Decompressor();
        }

        internal static void Return(Decompressor decompressor)
        {
            Decompressor? displaced;
            lock (Gate)
            {
                displaced = Idle;
                Idle = decompressor;
            }

            displaced?.Dispose();
        }
    }
}