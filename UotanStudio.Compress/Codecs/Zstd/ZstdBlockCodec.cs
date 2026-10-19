using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Frame-level Zstd block primitives over spans, backed by ZstdSharp, for filesystem formats that store a whole
/// zstd frame per block rather than a container with headers and checksums.
/// <para>基于 ZstdSharp、以 span 交换数据的帧级 Zstd 块原语，供按块存储完整 zstd 帧而非带头部与校验和容器的文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// <para>Both directions are span-based. The span decode overload that takes no window bound reuses a cached zstd
/// decompression context so a per-cluster loop does not rebuild a native context for every block, and it returns the
/// number of bytes it actually wrote because a span destination has no implicit size hint like the array-level
/// <c>Unwrap(compressed, expectedSize)</c> overload; readers that rely on that hint's array length stay on their
/// own path.</para>
/// <para>The bytes the span encode overload that takes no window bound writes match the single-frame path of the
/// array-level compressor for the same level and payload: a zstd frame is destination-independent, so no content-size
/// flag, content checksum or window override is applied there and no frame is emitted for an empty payload.</para>
/// <para>A window is the only context setting either direction can be asked for. The encode side writes it into the
/// frame header and clears it again before the context returns to the cache, because a zstd compression context keeps
/// a parameter across <c>Wrap</c> calls and an uncleared window would silently re-appear in the next caller's frame.
/// The decode side never touches its cached context: <c>ZSTD_d_windowLogMax</c> is not consulted by the one-shot
/// frame decode ZstdSharp performs, so the ceiling is enforced by reading the declared window out of the frame header
/// before any decoding happens.</para>
/// <para>每个方向唯一的上下文设置就是窗口。编码侧把它写入帧头，并在上下文回到缓存前再次清零，因为 zstd 压缩上下文会在
/// <c>Wrap</c> 调用之间保留该参数，未清零的窗口会静默出现在下一个调用方写出的帧里。解码侧完全不碰它缓存的上下文：
/// ZstdSharp 所执行的一次性帧解码并不查看 <c>ZSTD_d_windowLogMax</c>，因此上限改为在任何解码之前从帧头读取所声明的窗口来执行。</para>
/// </remarks>
internal static class ZstdBlockCodec
{
    private const int MinimumLevel = -5;
    private const int MaximumLevel = 22;
    private const int DefaultLevel = 3;
    private const int SmallSourceThreshold = 128 << 10;
    private const int MinimumWindowLog = 10;

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
    /// <param name="windowLog">Window log written into the frame header when it is at least <c>10</c>, the smallest
    /// window zstd encodes; <c>0</c> keeps the window the level would pick, so the emitted bytes stay
    /// destination-independent. <para>不小于 <c>10</c>（zstd 可编码的最小窗口）时写入帧头的窗口对数；<c>0</c> 保持级别自身选定的窗口，
    /// 因此产出的字节与目标无关。</para></param>
    /// <returns>Number of frame bytes written into <paramref name="destination"/>, or <c>0</c> for an empty source.
    /// <para>写入 <paramref name="destination"/> 的帧字节数；源为空时返回 <c>0</c>。</para></returns>
    /// <exception cref="CompressionException">The zstd context rejected the parameters or the frame did not fit
    /// <paramref name="destination"/>; the underlying ZstdSharp error is the inner exception.
    /// <para>zstd 上下文拒绝参数，或帧无法容纳于 <paramref name="destination"/>；ZstdSharp 的原始错误为内部异常。</para></exception>
    internal static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level = DefaultLevel, int windowLog = 0)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        var clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
        var compressor = CompressorCache.Rent(clamped);
        try
        {
            if (windowLog < MinimumWindowLog)
            {
                return compressor.Wrap(source, destination);
            }

            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            try
            {
                return compressor.Wrap(source, destination);
            }
            finally
            {
                compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 0);
            }
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD block compression failed. ZSTD 块压缩失败。", ex);
        }
        finally
        {
            CompressorCache.Return(clamped, compressor);
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
        => Decompress(source, destination, maximumWindowLog: 0);

    /// <summary>
    /// Decompresses one standalone zstd frame into a caller-owned span while bounding the window the frame declares,
    /// and reports the produced byte count.
    /// <para>在限制帧所声明窗口的前提下，把一个独立的 zstd 帧解压到调用方持有的 span，并报告实际产出的字节数。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes; an empty source produces nothing and returns <c>0</c> without
    /// touching a decompression context. <para>完整的帧字节；空源不产出内容，也不接触解压上下文，直接返回 <c>0</c>。</para></param>
    /// <param name="destination">Destination span written from its start; it must be large enough for the whole
    /// decoded frame. <para>从起始处写入的目标 span；其容量必须容纳整个解码帧。</para></param>
    /// <param name="maximumWindowLog">Ceiling on the window the frame may declare, expressed as a power of two and
    /// read from the frame header before any decoding starts; <c>0</c> keeps the unbounded path, and a value of
    /// <c>64</c> or above cannot be exceeded by any declared window. An untrusted frame declaring a larger window
    /// fails here instead of reaching the engine, which bounds nothing on this path: its
    /// <c>ZSTD_d_windowLogMax</c> setting is not consulted by the one-shot frame decode.
    /// <para>帧可声明窗口的上限，以 2 的幂表示，在任何解码开始前从帧头读取；<c>0</c> 保持不设限的路径，而
    /// <c>64</c> 及以上不可能被任何声明的窗口超出。声明更大窗口的不可信帧在此失败而不进入引擎：该一次性帧解码
    /// 并不会查看引擎的 <c>ZSTD_d_windowLogMax</c> 设置。</para></param>
    /// <returns>The number of decoded bytes written into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解码字节数。</para></returns>
    /// <exception cref="CompressionException">The frame declares a window above <paramref name="maximumWindowLog"/>,
    /// which is reported without an inner exception, or it is malformed or needs more bytes than
    /// <paramref name="destination"/> holds, where the underlying ZstdSharp error is the inner exception.
    /// <para>帧声明的窗口超出 <paramref name="maximumWindowLog"/>（此情形无内部异常），或帧无效、目标容量不足；
    /// 后两种情形的 ZstdSharp 原始错误为内部异常。</para></exception>
    internal static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int maximumWindowLog)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        if (maximumWindowLog > 0)
        {
            RefuseOversizedWindow(source, maximumWindowLog);
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
    /// Reads the window a frame declares out of its header and refuses the frame when that window exceeds the
    /// caller's ceiling, leaving the destination untouched.
    /// <para>从帧头读出该帧声明的窗口，并在其超出调用方上限时拒绝该帧，目标缓冲区保持未被写入。</para>
    /// </summary>
    /// <param name="source">Frame bytes to inspect. <para>待检查的帧字节。</para></param>
    /// <param name="maximumWindowLog">Window log the declared window must stay within. <para>所声明窗口必须落入的窗口对数上限。</para></param>
    /// <remarks>
    /// <para>Any non-zero return from the header read means the buffer does not hold a parseable frame header, whether
    /// because it is shorter than the header or because the magic is wrong, and is left to the decode call, which
    /// reports it as the malformed frame it is. Only a zero return fills the header fields, so no window is ever read
    /// from an unfilled struct.</para>
    /// <para>读取帧头返回非零即表示缓冲区并不含有可解析的帧头——无论是因为短于帧头还是因为魔数错误——都交由解码调用
    /// 按其真实情况报为无效帧。只有返回零才会填好帧头字段，因此绝不会从未填充的结构里读出窗口。</para>
    /// </remarks>
    /// <exception cref="CompressionException">The declared window is larger than the ceiling.
    /// <para>所声明的窗口大于上限。</para></exception>
    private static void RefuseOversizedWindow(ReadOnlySpan<byte> source, int maximumWindowLog)
    {
        var ceiling = maximumWindowLog >= 64 ? ulong.MaxValue : 1UL << maximumWindowLog;
        ZSTD_frameHeader header = default;
        unsafe
        {
            fixed (byte* frame = source)
            {
                if (Methods.ZSTD_getFrameHeader(&header, frame, (UIntPtr)source.Length) != UIntPtr.Zero)
                {
                    return;
                }
            }
        }

        if (header.windowSize > ceiling)
        {
            throw new CompressionException(
                $"ZSTD block declares a {header.windowSize} byte window above the {ceiling} byte ceiling. "
                + $"ZSTD 块声明的窗口为 {header.windowSize} 字节，超出 {ceiling} 字节的上限。");
        }
    }

    /// <summary>
    /// Decompresses one standalone zstd frame into a fresh array of the declared decoded length.
    /// <para>把一个独立的 zstd 帧解压到按声明解码长度新建的数组。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes, treated as untrusted. <para>完整的帧字节，按不可信输入处理。</para></param>
    /// <param name="expectedSize">Decoded length the caller expects, which is also the size of the returned array; a
    /// frame that decodes to more or fewer bytes is rejected rather than silently clipped or padded.
    /// <para>调用方期望的解码长度，同时也是返回数组的长度；解码字节数多于或少于该长度时按失败处理，而不是静默截断或补零。</para></param>
    /// <returns>A new array holding the decoded bytes, empty when <paramref name="expectedSize"/> is <c>0</c>.
    /// <para>保存解码字节的新数组；<paramref name="expectedSize"/> 为 <c>0</c> 时为空数组。</para></returns>
    /// <exception cref="CompressionException">The frame is malformed, needs more bytes than
    /// <paramref name="expectedSize"/> (the underlying ZstdSharp error is the inner exception), or produces fewer than
    /// the declared count. <para>帧无效，或所需解码字节多于 <paramref name="expectedSize"/>（ZstdSharp 的原始错误为内部异常），
    /// 或产出的字节少于声明数量。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedSize"/> is negative.
    /// <para><paramref name="expectedSize"/> 为负数。</para></exception>
    internal static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        if (source.IsEmpty || expectedSize == 0)
        {
            return [];
        }

        var decoded = new byte[expectedSize];
        var written = Decompress(source, decoded);
        if (written != expectedSize)
        {
            throw new CompressionException(
                $"ZSTD block decoded to {written} bytes while {expectedSize} were declared. "
                + $"ZSTD 块解出 {written} 字节，与声明的 {expectedSize} 字节不符。");
        }

        return decoded;
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