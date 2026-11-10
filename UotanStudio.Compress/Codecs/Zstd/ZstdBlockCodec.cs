using System.Buffers;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Frame-level Zstd block primitives over spans, built on the zstd library's own entry points, for filesystem
/// formats that store a whole zstd frame per block rather than a container with headers and checksums.
/// <para>以 zstd 库自身的入口实现的帧级 Zstd 块原语，以 span 交换数据，供按块存储完整 zstd 帧而非带头部与校验和容器的
/// 文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// Both directions are span-based and call <c>ZSTD_compress2</c> / <c>ZSTD_decompressDCtx</c> on contexts this type
/// owns and pools, so every parameter is applied through <c>ZSTD_CCtx_setParameter</c> exactly as the zstd manual
/// describes and no convenience wrapper resets state behind the caller's back. A pooled context keeps the compression
/// level it was created with, and the window is written on every call (auto when the caller does not ask for one), so
/// no frame inherits the previous frame's window. The bytes written for a given level and payload match the
/// single-frame path of the array-level compressor, because a zstd frame is destination-independent, and an empty
/// payload produces no frame at all.
/// The decode side cannot rely on the context's <c>ZSTD_d_windowLogMax</c>: a one-shot frame decode does not consult
/// it, so the declared window is read out of the frame header and refused before any decoding starts.
/// <para>两个方向都以 span 交换数据，并在本类型自有且池化的上下文上调用 <c>ZSTD_compress2</c> /
/// <c>ZSTD_decompressDCtx</c>，因此每个参数都按 zstd 手册通过 <c>ZSTD_CCtx_setParameter</c> 生效，也不会出现便利包装在
/// 调用方背后重置状态的情形。池中的上下文保留其创建时的压缩级别，窗口则在每次调用时写入（调用方不指定时取自动），因此没有帧会
/// 继承上一帧的窗口。给定级别与载荷时写出的字节与数组级压缩器的单帧路径一致，因为 zstd 帧与目标无关；空载荷不会产生任何帧。
/// 解码侧不能依赖上下文的 <c>ZSTD_d_windowLogMax</c>：一次性帧解码并不查看它，因此先读出帧声明的窗口并在任何解码开始前拒绝
/// 超限帧。</para>
/// </remarks>
public static class ZstdBlockCodec
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
    public static int MaximumCompressedSize(int sourceLength)
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
    /// <paramref name="destination"/>; the message carries the zstd error name.
    /// <para>zstd 上下文拒绝参数，或帧无法容纳于 <paramref name="destination"/>；消息中携带 zstd 的错误名。</para></exception>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level = DefaultLevel, int windowLog = 0)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        var clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
        var context = CompressionContextPool.Rent(clamped);
        try
        {
            unsafe
            {
                var cctx = (ZSTD_CCtx_s*)context;
                SetCompressionParameter(cctx, ZSTD_cParameter.ZSTD_c_windowLog,
                    windowLog < MinimumWindowLog ? 0 : windowLog);
                fixed (byte* input = source)
                fixed (byte* output = destination)
                {
                    var written = Methods.ZSTD_compress2(cctx, output, (nuint)destination.Length, input,
                        (nuint)source.Length);
                    return EnsureSuccess(written, "ZSTD block compression");
                }
            }
        }
        finally
        {
            CompressionContextPool.Return(clamped, context);
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
    /// <exception cref="CompressionException">The frame is malformed or the destination is too small; the message
    /// carries the zstd error name. <para>帧无效或目标容量不足；消息中携带 zstd 的错误名。</para></exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
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
    /// fails here instead of reaching the engine, which bounds nothing on this path: a one-shot frame decode does not
    /// consult <c>ZSTD_d_windowLogMax</c>.
    /// <para>帧可声明窗口的上限，以 2 的幂表示，在任何解码开始前从帧头读取；<c>0</c> 保持不设限的路径，而
    /// <c>64</c> 及以上不可能被任何声明的窗口超出。声明更大窗口的不可信帧在此失败而不进入引擎：该一次性帧解码
    /// 并不会查看 <c>ZSTD_d_windowLogMax</c>。</para></param>
    /// <returns>The number of decoded bytes written into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解码字节数。</para></returns>
    /// <exception cref="CompressionException">The frame declares a window above <paramref name="maximumWindowLog"/>,
    /// which is reported without a zstd error, or it is malformed or needs more bytes than
    /// <paramref name="destination"/> holds, where the message carries the zstd error name.
    /// <para>帧声明的窗口超出 <paramref name="maximumWindowLog"/>（此情形不带 zstd 错误），或帧无效、目标容量不足；
    /// 后两种情形的消息携带 zstd 的错误名。</para></exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int maximumWindowLog)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        if (maximumWindowLog > 0)
        {
            RefuseOversizedWindow(source, maximumWindowLog);
        }

        var context = DecompressionContextPool.Rent();
        try
        {
            unsafe
            {
                var dctx = (ZSTD_DCtx_s*)context;
                fixed (byte* input = source)
                fixed (byte* output = destination)
                {
                    var written = Methods.ZSTD_decompressDCtx(dctx, output, (nuint)destination.Length, input,
                        (nuint)source.Length);
                    return EnsureSuccess(written, "ZSTD block decompression");
                }
            }
        }
        finally
        {
            DecompressionContextPool.Return(context);
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
    /// Any non-zero return from the header read means the buffer does not hold a parseable frame header, whether
    /// because it is shorter than the header or because the magic is wrong, and is left to the decode call, which
    /// reports it as the malformed frame it is. Only a zero return fills the header fields, so no window is ever read
    /// from an unfilled struct.
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
    /// <paramref name="expectedSize"/> (the message carries the zstd error name), or produces fewer than
    /// the declared count. <para>帧无效，或所需解码字节多于 <paramref name="expectedSize"/>（消息携带 zstd 的错误名），
    /// 或产出的字节少于声明数量。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedSize"/> is negative.
    /// <para><paramref name="expectedSize"/> 为负数。</para></exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize)
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
    /// Applies one compression parameter to an owned context and reports the zstd error name when it is rejected.
    /// <para>向自有上下文应用一个压缩参数，被拒绝时报告 zstd 的错误名。</para>
    /// </summary>
    /// <param name="context">Compression context. <para>压缩上下文。</para></param>
    /// <param name="parameter">Parameter to set. <para>要设置的参数。</para></param>
    /// <param name="value">Parameter value. <para>参数值。</para></param>
    /// <exception cref="CompressionException">The context rejected the value.
    /// <para>上下文拒绝了该值。</para></exception>
    private static unsafe void SetCompressionParameter(ZSTD_CCtx_s* context, ZSTD_cParameter parameter, int value)
    {
        var code = Methods.ZSTD_CCtx_setParameter(context, parameter, value);
        if (Methods.ZSTD_isError(code))
        {
            var error = Methods.ZSTD_getErrorName(code);
            throw new CompressionException(
                $"ZSTD rejected compression parameter {parameter} = {value}: {error}. "
                + $"ZSTD 拒绝了压缩参数 {parameter} = {value}：{error}。");
        }
    }

    /// <summary>
    /// Converts a zstd result code into a byte count, translating an error code into the codec failure.
    /// <para>把 zstd 的结果码转换为字节数，并把错误码转换为编解码失败。</para>
    /// </summary>
    /// <param name="code">Result code returned by a zstd entry point. <para>zstd 入口返回的结果码。</para></param>
    /// <param name="operation">Operation name used in the failure message. <para>失败消息中使用的操作名。</para></param>
    /// <returns>The byte count carried by a successful code. <para>成功码携带的字节数。</para></returns>
    /// <exception cref="CompressionException">The code reports a zstd error.
    /// <para>该结果码报告了 zstd 错误。</para></exception>
    private static int EnsureSuccess(nuint code, string operation)
    {
        if (Methods.ZSTD_isError(code))
        {
            var error = Methods.ZSTD_getErrorName(code);
            throw new CompressionException($"{operation} failed: {error}. {operation} 失败：{error}。");
        }

        return (int)code;
    }

    /// <summary>
    /// Pools idle compression contexts per level for the block primitives, so a per-cluster loop reuses one context
    /// instead of building one per block.
    /// <para>为块原语按级别池化空闲的压缩上下文，使逐 cluster 的循环复用同一上下文而不必每块新建。</para>
    /// </summary>
    /// <remarks>
    /// A context is created with its compression level already applied, because the level is the pool key and every
    /// caller that rents that context wants the same one. The list is unbounded: renting removes the newest entry
    /// under the lock and hands out an exclusive owner, while a caller arriving with no idle entry creates its own, so
    /// no caller ever waits on another. Contexts are parked rather than freed, matching the wrapper contexts below,
    /// which the array and stream facade keeps using because a wrapper owns a native context this type cannot reach.
    /// <para>上下文在创建时就已经应用压缩级别，因为级别正是池的键，且每个借用该上下文的调用方要的都是同一个级别。列表不设
    /// 上限：借用会在锁内移除最新条目并给出独占所有者，而没有空闲条目的调用方自行新建，因此不会有调用方等待他人。上下文只是停放
    /// 而不释放，与下文的包装器上下文一致；数组与流门面继续使用后者，因为包装器持有本类型无法访问的原生上下文。</para>
    /// </remarks>
    internal static class CompressionContextPool
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<int, List<IntPtr>> Idle = [];

        /// <summary>
        /// Rents a context for the given level, creating one when none is parked.
        /// <para>借用给定级别的上下文；没有停放实例时新建一个。</para>
        /// </summary>
        /// <param name="level">Compression level already valid for zstd. <para>已经合法的 zstd 压缩级别。</para></param>
        /// <returns>An exclusively owned context. <para>被独占持有的上下文。</para></returns>
        /// <exception cref="CompressionException">The zstd library could not create or configure a context.
        /// <para>zstd 库无法创建或配置上下文。</para></exception>
        internal static IntPtr Rent(int level)
        {
            lock (Gate)
            {
                if (Idle.TryGetValue(level, out var idleList))
                {
                    var last = idleList.Count - 1;
                    var context = idleList[last];
                    idleList.RemoveAt(last);
                    if (idleList.Count == 0)
                    {
                        Idle.Remove(level);
                    }

                    return context;
                }
            }

            unsafe
            {
                var created = (IntPtr)Methods.ZSTD_createCCtx();
                if (created == IntPtr.Zero)
                {
                    throw new CompressionException(
                        "ZSTD could not create a compression context. ZSTD 无法创建压缩上下文。");
                }

                try
                {
                    SetCompressionParameter((ZSTD_CCtx_s*)created, ZSTD_cParameter.ZSTD_c_compressionLevel, level);
                    return created;
                }
                catch
                {
                    Methods.ZSTD_freeCCtx((ZSTD_CCtx_s*)created);
                    throw;
                }
            }
        }

        /// <summary>
        /// Parks a context for later reuse by the same level.
        /// <para>把上下文停放供同一级别后续复用。</para>
        /// </summary>
        /// <param name="level">Level the context was created with. <para>该上下文创建时使用的级别。</para></param>
        /// <param name="context">Context being returned. <para>归还的上下文。</para></param>
        internal static void Return(int level, IntPtr context)
        {
            lock (Gate)
            {
                if (!Idle.TryGetValue(level, out var idleList))
                {
                    idleList = [];
                    Idle[level] = idleList;
                }

                idleList.Add(context);
            }
        }
    }

    /// <summary>
    /// Pools one idle decompression context for the block primitives, with the same exclusive-rental contract as
    /// <see cref="CompressionContextPool"/>.
    /// <para>为块原语池化一个空闲解压上下文，独占借用契约与 <see cref="CompressionContextPool"/> 相同。</para>
    /// </summary>
    internal static class DecompressionContextPool
    {
        private static readonly Lock Gate = new();
        private static IntPtr _idle;

        /// <summary>
        /// Rents the idle context, or creates one when none is parked.
        /// <para>借用空闲上下文；没有停放实例时新建一个。</para>
        /// </summary>
        /// <returns>The exclusively rented context. <para>被独占借用的上下文。</para></returns>
        /// <exception cref="CompressionException">The zstd library could not create a context.
        /// <para>zstd 库无法创建上下文。</para></exception>
        internal static IntPtr Rent()
        {
            lock (Gate)
            {
                var idle = _idle;
                if (idle != IntPtr.Zero)
                {
                    _idle = IntPtr.Zero;
                    return idle;
                }
            }

            unsafe
            {
                var created = (IntPtr)Methods.ZSTD_createDCtx();
                return created != IntPtr.Zero
                    ? created
                    : throw new CompressionException(
                        "ZSTD could not create a decompression context. ZSTD 无法创建解压上下文。");
            }
        }

        /// <summary>
        /// Parks a context for later reuse; a context already parked is freed.
        /// <para>停放一个上下文供后续复用；已停放的那个上下文会被释放。</para>
        /// </summary>
        /// <param name="context">Context being returned. <para>归还的上下文。</para></param>
        internal static void Return(IntPtr context)
        {
            IntPtr displaced;
            lock (Gate)
            {
                displaced = _idle;
                _idle = context;
            }

            if (displaced != IntPtr.Zero)
            {
                unsafe
                {
                    Methods.ZSTD_freeDCtx((ZSTD_DCtx_s*)displaced);
                }
            }
        }
    }

    /// <summary>
    /// Caches idle zstd compression contexts per level so per-cluster loops and parallel chunk batches reuse
    /// contexts instead of rebuilding one for every block.
    /// <para>按级别缓存空闲的 zstd 压缩上下文，使逐 cluster 的循环与并行分块批次复用上下文而不必每块重建。</para>
    /// </summary>
    /// <remarks>
    /// A zstd context carries the level's window state, so constructing one per block repeats that setup for every
    /// cluster of an image. The cache keeps an unbounded idle list per level: renting removes the newest entry under
    /// the lock and hands out an exclusive owner, while a caller arriving with no idle entry constructs its own, so
    /// no caller ever waits on another. A returned context is only parked, never disposed, and parallel workers that
    /// return in the same batch are all still available to the next batch. A context is never shared concurrently
    /// and is therefore never observed in a half-consumed state.
    /// <para>zstd 上下文承载该级别的窗口状态，每块新建就会让镜像的每个 cluster 重复一次搭建。缓存为每个级别保留
    /// 不设上限的空闲列表：借用会在锁内移除最新条目并给出独占所有者，而没有空闲条目的调用方自行新建，因此不会有
    /// 调用方等待他人。归还的上下文只是停放而不释放，同批并行工作者归还的上下文对下一批仍然可用。上下文从不并发
    /// 共享，也就不会出现处于半消费状态被观测到的情况。</para>
    /// </remarks>
    internal static class CompressorCache
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<int, List<Holder>> Idle = [];

        /// <summary>
        /// Rents the idle wrapper compressor for one level, or creates one when none is parked.
        /// <para>借用某级别的空闲包装压缩器；没有停放实例时新建一个。</para>
        /// </summary>
        /// <param name="level">Compression level of the rented compressor. <para>借用的压缩器的压缩级别。</para></param>
        /// <returns>The exclusively rented compressor. <para>被独占借用的压缩器。</para></returns>
        internal static ZstdSharp.Compressor Rent(int level)
        {
            lock (Gate)
            {
                if (Idle.TryGetValue(level, out var idleList))
                {
                    var last = idleList.Count - 1;
                    var holder = idleList[last];
                    idleList.RemoveAt(last);
                    if (idleList.Count == 0)
                    {
                        Idle.Remove(level);
                    }

                    return holder.Value;
                }
            }

            return new ZstdSharp.Compressor(level);
        }

        /// <summary>
        /// Parks one wrapper compressor for later reuse by the same level.
        /// <para>停放一个包装压缩器供同一级别后续复用。</para>
        /// </summary>
        /// <param name="level">Level the compressor was created with. <para>该压缩器创建时使用的级别。</para></param>
        /// <param name="compressor">Compressor being returned. <para>归还的压缩器。</para></param>
        internal static void Return(int level, ZstdSharp.Compressor compressor)
        {
            lock (Gate)
            {
                if (!Idle.TryGetValue(level, out var idleList))
                {
                    idleList = [];
                    Idle[level] = idleList;
                }

                idleList.Add(new Holder(compressor));
            }
        }

        private sealed class Holder(ZstdSharp.Compressor value)
        {
            internal ZstdSharp.Compressor Value { get; } = value;
        }
    }

    /// <summary>
    /// Reuses one zstd decompression context across calls, with the same exclusive-rental contract as
    /// <see cref="CompressorCache"/>.
    /// <para>跨调用复用同一个 zstd 解压上下文，独占借用契约与 <see cref="CompressorCache"/> 相同。</para>
    /// </summary>
    public static class DecompressorCache
    {
        private static readonly Lock Gate = new();
        private static ZstdSharp.Decompressor? Idle;

        /// <summary>
        /// Rents the idle decompression context, or creates one when none is parked.
        /// <para>借用空闲解压上下文；没有停放实例时新建一个。</para>
        /// </summary>
        /// <returns>The exclusively rented decompressor. <para>被独占借用的解压器。</para></returns>
        public static ZstdSharp.Decompressor Rent()
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

            return new ZstdSharp.Decompressor();
        }

        /// <summary>
        /// Parks one decompression context for later reuse; a context already parked is disposed.
        /// <para>停放一个解压上下文供后续复用；已停放的那个上下文会被释放。</para>
        /// </summary>
        /// <param name="decompressor">Context being returned; null is ignored. <para>归还的上下文；null 会被忽略。</para></param>
        public static void Return(ZstdSharp.Decompressor decompressor)
        {
            ZstdSharp.Decompressor? displaced;
            lock (Gate)
            {
                displaced = Idle;
                Idle = decompressor;
            }

            displaced?.Dispose();
        }
    }
}