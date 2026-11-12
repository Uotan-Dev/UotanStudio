using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Frame-level Zstd block primitives over spans, built on the managed zstd engine the runtime ships in
/// <see cref="System.IO.Compression"/>, for filesystem formats that store a whole zstd frame per block rather than a
/// container with headers and checksums.
/// <para>基于运行时随 <see cref="System.IO.Compression"/> 内置的托管 zstd 引擎实现的帧级 Zstd 块原语，以 span 交换数据，
/// 供按块存储完整 zstd 帧而非带头部与校验和容器的文件系统格式使用。</para>
/// </summary>
/// <remarks>
/// Both directions are span-based and run on encoder and decoder instances this type owns and pools, so every frame
/// starts from a fresh session: the encoder is reset, the source length is declared, and the whole block is written
/// as one final chunk, which lets the engine right-size the declared window and record the content size. A pooled
/// encoder keeps the compression level and window it was created with, so no frame inherits another frame's
/// parameters, and an empty payload produces no frame at all.
/// The decode side cannot rely on a configured window ceiling because the managed engine does not consult one while
/// decoding, so the declared window is read out of the frame header and refused before any decoding starts.
/// <para>两个方向都以 span 交换数据，并在本类型自有且池化的编码器与解码器实例上运行，因此每一帧都从全新会话开始：
/// 编码器先重置、声明源长度，再把整块作为最后分片一次写出，使引擎得以收窄声明的窗口并记录内容尺寸。池中的编码器
/// 保留其创建时的压缩级别与窗口，因此没有帧会继承其他帧的参数；空载荷不会产生任何帧。
/// 解码侧不能依赖配置的窗口上限，因为托管引擎在解码过程中不查看它，因此先从帧头读出声明的窗口并在任何解码开始前
/// 拒绝超限帧。</para>
/// </remarks>
public static class ZstdBlockCodec
{
    private const int MinimumLevel = -5;
    private const int MaximumLevel = 22;
    private const int DefaultLevel = 3;
    private const int SmallSourceThreshold = 128 << 10;
    private const int MinimumWindowLog = 10;
    private const int UnknownSizeInitialBytes = 64 << 10;

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
    /// encoder is used for it.
    /// <para>待压缩的块载荷；空载荷不生成任何帧，也不会为其使用编码器。</para></param>
    /// <param name="destination">Destination span; the frame is written from its start and no byte goes past its
    /// length, so a span too small to hold the frame is reported as a failure.
    /// <para>目标 span；帧从其起始处写入且不会越过长度边界，容量不足以容纳整帧时按失败处理。</para></param>
    /// <param name="level">Compression level, clamped into the zstd range from <c>-5</c> to <c>22</c> before the
    /// encoder is rented; the default matches the level the array-level compressor uses when no options are given.
    /// <para>压缩级别，租借编码器前先夹取到 zstd 的 <c>-5</c> 至 <c>22</c> 区间；默认值与无选项时数组级压缩器使用的级别一致。</para></param>
    /// <param name="windowLog">Window log written into the frame header when it is at least <c>10</c>, the smallest
    /// window zstd encodes; <c>0</c> keeps the engine default window, so the emitted bytes stay
    /// destination-independent. <para>不小于 <c>10</c>（zstd 可编码的最小窗口）时写入帧头的窗口对数；<c>0</c> 保持引擎默认窗口，
    /// 因此产出的字节与目标无关。</para></param>
    /// <returns>Number of frame bytes written into <paramref name="destination"/>, or <c>0</c> for an empty source.
    /// <para>写入 <paramref name="destination"/> 的帧字节数；源为空时返回 <c>0</c>。</para></returns>
    /// <exception cref="CompressionException">The frame did not fit <paramref name="destination"/>.
    /// <para>帧无法容纳于 <paramref name="destination"/>。</para></exception>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level = DefaultLevel, int windowLog = 0)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        var clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
        var windowLog2 = windowLog >= MinimumWindowLog
            ? windowLog
            : ZstandardCompressionOptions.DefaultWindowLog2;
        var encoder = CompressorCache.Rent(clamped, windowLog2);
        try
        {
            encoder.Reset();
            encoder.SetSourceLength(source.Length);
            var status = encoder.Compress(source, destination, out var consumed, out var written, isFinalBlock: true);
            if (status != OperationStatus.Done || consumed != source.Length)
            {
                throw new CompressionException(
                    $"ZSTD block compression did not fit {destination.Length} bytes. "
                    + $"ZSTD 块压缩无法装入 {destination.Length} 字节。");
            }

            return written;
        }
        finally
        {
            CompressorCache.Return(clamped, windowLog2, encoder);
        }
    }

    /// <summary>
    /// Decompresses one standalone zstd frame, or a concatenation of frames, into a caller-owned span and reports
    /// the produced byte count.
    /// <para>把一个独立的 zstd 帧或帧的串联解压到调用方持有的 span，并报告实际产出的字节数。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes; an empty source produces nothing and returns <c>0</c> without
    /// touching a decoder. <para>完整的帧字节；空源不产出内容，也不接触解码器，直接返回 <c>0</c>。</para></param>
    /// <param name="destination">Destination span written from its start; it must be large enough for the whole
    /// decoded frame, and the caller uses the returned length rather than <paramref name="destination"/>'s length
    /// because the span carries no size hint. <para>从起始处写入的目标 span；其容量必须容纳整个解码帧，调用方应以返回长度
    /// 而非 <paramref name="destination"/> 的长度为准，因为 span 不携带尺寸提示。</para></param>
    /// <returns>The number of decoded bytes written into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解码字节数。</para></returns>
    /// <exception cref="CompressionException">The frame is malformed, truncated or the destination is too small.
    /// <para>帧无效、被截断或目标容量不足。</para></exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        => Decompress(source, destination, maximumWindowLog: 0);

    /// <summary>
    /// Decompresses one standalone zstd frame into a caller-owned span while bounding the window the frame declares,
    /// and reports the produced byte count.
    /// <para>在限制帧所声明窗口的前提下，把一个独立的 zstd 帧解压到调用方持有的 span，并报告实际产出的字节数。</para>
    /// </summary>
    /// <param name="source">Complete frame bytes; an empty source produces nothing and returns <c>0</c> without
    /// touching a decoder. <para>完整的帧字节；空源不产出内容，也不接触解码器，直接返回 <c>0</c>。</para></param>
    /// <param name="destination">Destination span written from its start; it must be large enough for the whole
    /// decoded frame. <para>从起始处写入的目标 span；其容量必须容纳整个解码帧。</para></param>
    /// <param name="maximumWindowLog">Ceiling on the window the frame may declare, expressed as a power of two and
    /// read from the frame header before any decoding starts; <c>0</c> keeps the unbounded path, and a value of
    /// <c>64</c> or above cannot be exceeded by any declared window. An untrusted frame declaring a larger window
    /// fails here instead of reaching the engine, which bounds nothing on this path: the managed engine does not
    /// consult a window ceiling while decoding.
    /// <para>帧可声明窗口的上限，以 2 的幂表示，在任何解码开始前从帧头读取；<c>0</c> 保持不设限的路径，而
    /// <c>64</c> 及以上不可能被任何声明的窗口超出。声明更大窗口的不可信帧在此失败而不进入引擎：托管引擎在解码
    /// 过程中不查看窗口上限。</para></param>
    /// <returns>The number of decoded bytes written into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解码字节数。</para></returns>
    /// <exception cref="CompressionException">The frame declares a window above <paramref name="maximumWindowLog"/>,
    /// which is reported without an engine error, or it is malformed, truncated or needs more bytes than
    /// <paramref name="destination"/> holds.
    /// <para>帧声明的窗口超出 <paramref name="maximumWindowLog"/>（此情形不带引擎错误），或帧无效、被截断、
    /// 所需字节多于目标容量。</para></exception>
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

        var decoder = DecompressorCache.Rent();
        try
        {
            decoder.Reset();
            var consumed = 0;
            var written = 0;
            while (true)
            {
                var status = decoder.Decompress(
                    source[consumed..],
                    destination[written..],
                    out var stepConsumed,
                    out var stepWritten);
                consumed += stepConsumed;
                written += stepWritten;
                switch (status)
                {
                    case OperationStatus.Done:
                        if (consumed == source.Length)
                        {
                            return written;
                        }

                        if (stepConsumed == 0)
                        {
                            throw new CompressionException(
                                "ZSTD block carries unparsable data behind the frames. ZSTD 块在帧之后携带了无法解析的数据。");
                        }

                        decoder.Reset();
                        break;
                    case OperationStatus.NeedMoreData:
                        if (consumed == 0)
                        {
                            throw new CompressionException(
                                "ZSTD block decompression failed: the frame is truncated. ZSTD 块解压失败：帧被截断。");
                        }

                        break;
                    case OperationStatus.DestinationTooSmall:
                        throw new CompressionException(
                            $"ZSTD block needs more than the {destination.Length} decode bytes provided. "
                            + $"ZSTD 块所需的解码空间超出了提供的 {destination.Length} 字节。");
                    case OperationStatus.InvalidData:
                        throw new CompressionException(
                            "ZSTD block decompression failed: the frame is malformed. ZSTD 块解压失败：帧无效。");
                    default:
                        throw new CompressionException(
                            $"Unexpected Zstd decoder status: {status}. Zstd 解码器返回了意外的状态 {status}。");
                }
            }
        }
        finally
        {
            DecompressorCache.Return(decoder);
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
    /// <paramref name="expectedSize"/>, or produces fewer than the declared count.
    /// <para>帧无效、所需解码字节多于 <paramref name="expectedSize"/>，或产出的字节少于声明数量。</para></exception>
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
    /// Decompresses one standalone zstd frame, or a concatenation of frames, into a fresh array without knowing the
    /// decoded length up front.
    /// <para>在不预知解码长度的前提下，把一个独立 zstd 帧或帧的串联解压到新建数组。</para>
    /// </summary>
    /// <param name="source">Frame bytes, treated as untrusted. <para>帧字节，按不可信输入处理。</para></param>
    /// <returns>A new array holding exactly the decoded bytes. <para>恰好保存解码字节的新数组。</para></returns>
    /// <remarks>
    /// When the leading frame records its content size the buffer starts at that declared length; a frame without a
    /// content size starts from a fixed initial buffer. Both paths pump into a buffer that grows with the plaintext
    /// the frames actually yield, so the allocation follows the output instead of the declaration alone.
    /// <para>首帧记录了内容尺寸时缓冲从该声明长度起步；未记录内容尺寸的帧从固定初始缓冲起步。两条路径都泵入一个
    /// 随各帧实际明文产出而增长的缓冲，使分配跟随产出而非仅跟随声明。</para>
    /// </remarks>
    /// <exception cref="CompressionException">The frames are malformed or truncated.
    /// <para>帧无效或被截断。</para></exception>
    internal static byte[] DecompressAuto(ReadOnlySpan<byte> source)
    {
        var initialBytes = UnknownSizeInitialBytes;
        if (ZstandardDecoder.TryGetMaxDecompressedLength(source, out var declared)
            && declared > 0
            && declared <= int.MaxValue)
        {
            initialBytes = checked((int)declared);
        }

        var decoder = DecompressorCache.Rent();
        try
        {
            decoder.Reset();
            var buffer = new byte[initialBytes];
            var consumed = 0;
            var written = 0;
            while (true)
            {
                var status = decoder.Decompress(
                    source[consumed..],
                    buffer.AsSpan(written),
                    out var stepConsumed,
                    out var stepWritten);
                consumed += stepConsumed;
                written += stepWritten;
                switch (status)
                {
                    case OperationStatus.Done:
                        if (consumed == source.Length)
                        {
                            var result = new byte[written];
                            buffer.AsSpan(0, written).CopyTo(result);
                            return result;
                        }

                        if (stepConsumed == 0)
                        {
                            throw new CompressionException(
                                "ZSTD block carries unparsable data behind the frames. ZSTD 块在帧之后携带了无法解析的数据。");
                        }

                        decoder.Reset();
                        break;
                    case OperationStatus.DestinationTooSmall:
                        var grown = new byte[checked(Math.Max(buffer.Length * 2, written + UnknownSizeInitialBytes))];
                        buffer.AsSpan(0, written).CopyTo(grown);
                        buffer = grown;
                        break;
                    case OperationStatus.NeedMoreData:
                        if (consumed == 0)
                        {
                            throw new CompressionException(
                                "ZSTD block decompression failed: the frame is truncated. ZSTD 块解压失败：帧被截断。");
                        }

                        break;
                    case OperationStatus.InvalidData:
                        throw new CompressionException(
                            "ZSTD block decompression failed: the frame is malformed. ZSTD 块解压失败：帧无效。");
                    default:
                        throw new CompressionException(
                            $"Unexpected Zstd decoder status: {status}. Zstd 解码器返回了意外的状态 {status}。");
                }
            }
        }
        finally
        {
            DecompressorCache.Return(decoder);
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
    /// A header too short to parse or not carrying the zstd magic is left to the decode call, which reports it as
    /// the malformed frame it is. Only a complete header fills the window, so no window is ever read from an
    /// unfilled parse.
    /// <para>短于可解析长度或不带 zstd 魔数的帧头交由解码调用按其真实情况报为无效帧。只有完整的帧头才会给出窗口，
    /// 因此绝不会从未完成的解析里读出窗口。</para>
    /// </remarks>
    /// <exception cref="CompressionException">The declared window is larger than the ceiling.
    /// <para>所声明的窗口大于上限。</para></exception>
    private static void RefuseOversizedWindow(ReadOnlySpan<byte> source, int maximumWindowLog)
    {
        if (ZstdFrameHeader.TryReadWindow(source, out var declaredWindow)
            != ZstdFrameHeader.ParseResult.Parsed)
        {
            return;
        }

        var ceiling = maximumWindowLog >= 64 ? long.MaxValue : 1L << maximumWindowLog;
        if (declaredWindow > ceiling)
        {
            throw new CompressionException(
                $"ZSTD block declares a {declaredWindow} byte window above the {ceiling} byte ceiling. "
                + $"ZSTD 块声明的窗口为 {declaredWindow} 字节，超出 {ceiling} 字节的上限。");
        }
    }

    /// <summary>
    /// Pools idle zstd encoders per level and window pair for the block primitives and the array-level compressor,
    /// so a per-cluster loop reuses one encoder instead of building one per block.
    /// <para>为块原语与数组级压缩器按级别与窗口组合池化空闲的 zstd 编码器，使逐 cluster 的循环复用同一编码器
    /// 而不必每块新建。</para>
    /// </summary>
    /// <remarks>
    /// An encoder is created with its compression level and window already applied, because the pair is the pool key
    /// and every caller that rents that encoder wants the same one. The list is unbounded: renting removes the newest
    /// entry under the lock and hands out an exclusive owner, while a caller arriving with no idle entry creates its
    /// own, so no caller ever waits on another. Encoders are parked rather than freed and every user resets the
    /// encoder before writing a frame, so no frame inherits another frame's session.
    /// <para>编码器在创建时就已经应用压缩级别与窗口，因为该组合正是池的键，且每个借用该编码器的调用方要的都是
    /// 同一个组合。列表不设上限：借用会在锁内移除最新条目并给出独占所有者，而没有空闲条目的调用方自行新建，因此
    /// 不会有调用方等待他人。编码器只是停放而不释放，且每个使用者在写帧前都会重置编码器，因此没有帧会继承
    /// 其他帧的会话。</para>
    /// </remarks>
    internal static class CompressorCache
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<(int Level, int WindowLog2), List<ZstandardEncoder>> Idle = [];

        /// <summary>
        /// Rents an encoder for the given level and window pair, creating one when none is parked.
        /// <para>借用给定级别与窗口组合的编码器；没有停放实例时新建一个。</para>
        /// </summary>
        /// <param name="level">Compression level already valid for zstd. <para>已经合法的 zstd 压缩级别。</para></param>
        /// <param name="windowLog2">Window log the encoder was configured with. <para>编码器配置的窗口对数。</para></param>
        /// <returns>An exclusively owned encoder. <para>被独占持有的编码器。</para></returns>
        internal static ZstandardEncoder Rent(int level, int windowLog2)
        {
            lock (Gate)
            {
                if (Idle.TryGetValue((level, windowLog2), out var idleList))
                {
                    var last = idleList.Count - 1;
                    var encoder = idleList[last];
                    idleList.RemoveAt(last);
                    if (idleList.Count == 0)
                    {
                        Idle.Remove((level, windowLog2));
                    }

                    return encoder;
                }
            }

            return new ZstandardEncoder(new ZstandardCompressionOptions
            {
                Quality = level,
                WindowLog2 = windowLog2,
            });
        }

        /// <summary>
        /// Parks an encoder for later reuse by the same level and window pair.
        /// <para>把编码器停放供同一级别与窗口组合后续复用。</para>
        /// </summary>
        /// <param name="level">Level the encoder was created with. <para>该编码器创建时使用的级别。</para></param>
        /// <param name="windowLog2">Window log the encoder was configured with. <para>编码器配置的窗口对数。</para></param>
        /// <param name="encoder">Encoder being returned. <para>归还的编码器。</para></param>
        internal static void Return(int level, int windowLog2, ZstandardEncoder encoder)
        {
            lock (Gate)
            {
                if (!Idle.TryGetValue((level, windowLog2), out var idleList))
                {
                    idleList = [];
                    Idle[(level, windowLog2)] = idleList;
                }

                idleList.Add(encoder);
            }
        }
    }

    /// <summary>
    /// Reuses one idle zstd decoder across calls, with the same exclusive-rental contract as
    /// <see cref="CompressorCache"/>.
    /// <para>跨调用复用同一个空闲 zstd 解码器，独占借用契约与 <see cref="CompressorCache"/> 相同。</para>
    /// </summary>
    internal static class DecompressorCache
    {
        private static readonly Lock Gate = new();
        private static ZstandardDecoder? Idle;

        /// <summary>
        /// Rents the idle decoder, or creates one when none is parked.
        /// <para>借用空闲解码器；没有停放实例时新建一个。</para>
        /// </summary>
        /// <returns>The exclusively rented decoder. <para>被独占借用的解码器。</para></returns>
        internal static ZstandardDecoder Rent()
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

            return new ZstandardDecoder();
        }

        /// <summary>
        /// Parks a decoder for later reuse; a decoder already parked is disposed.
        /// <para>停放一个解码器供后续复用；已停放的那个解码器会被释放。</para>
        /// </summary>
        /// <param name="decoder">Decoder being returned; null is ignored. <para>归还的解码器；null 会被忽略。</para></param>
        internal static void Return(ZstandardDecoder decoder)
        {
            ZstandardDecoder? displaced;
            lock (Gate)
            {
                displaced = Idle;
                Idle = decoder;
            }

            displaced?.Dispose();
        }
    }
}