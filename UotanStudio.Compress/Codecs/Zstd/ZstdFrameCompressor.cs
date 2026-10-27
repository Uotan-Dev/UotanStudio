using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Caller-owned Zstandard context that encodes one complete frame per call and keeps its level, content-checksum
/// setting and raw dictionary across calls.
/// <para>调用方持有的 Zstandard 上下文：每次调用编码一个完整的帧，并在跨调用之间保持级别、内容校验和设置与原始字典。</para>
/// </summary>
/// <remarks>
/// Reuse policy deliberately belongs to the caller and this type keeps no static cache, because Zstandard
/// advances an internal hash salt on every session reset whenever the selected level's strategy is <c>greedy</c>,
/// <c>lazy</c> or <c>lazy2</c>, and a salted reused context may then emit bytes that a freshly created context does
/// not. Levels whose strategy keeps the row match finder disabled — <c>dfast</c>, the strategy behind the default
/// level <c>3</c> — return to the same state on every session reset, so a reused context and a freshly created one
/// agree byte for byte there and one instance may serve a whole encoding loop. A caller that selects a level outside
/// that equivalence buys reuse at the price of frames that depend on more than the block payload alone, and it still
/// owns disposal of this instance.
/// A format that compresses block by block without wanting to hold a context of its own stays on
/// <see cref="ZstdBlockCodec"/>, which builds and releases one engine context per block.
/// <para>复用策略刻意留给调用方，本类型不做任何静态缓存：只要所选级别的策略是 <c>greedy</c>、<c>lazy</c> 或 <c>lazy2</c>，
/// Zstandard 就会在每次会话重置时推进内部哈希盐值，被加盐的复用上下文因此可能产出新建上下文不会产出的字节。策略不启用行匹配器
/// 的级别（<c>dfast</c>，即默认级别 <c>3</c> 背后的策略）每次会话重置都回到同一状态，因此复用与新建上下文在该级别上逐字节
/// 一致，一个实例足以跑完整个编码循环。选择该等价性之外级别的调用方，等于以“帧取决于该块自身载荷之外的状态”为代价换取复用，
/// 并且本实例的释放责任仍归它自己。
/// 逐块压缩又不愿自己持有上下文的格式，继续使用 <see cref="ZstdBlockCodec"/>：它每块新建并释放一个引擎上下文。</para>
/// </remarks>
public sealed class ZstdFrameCompressor : IDisposable
{
    private readonly Compressor _compressor;
    private bool _disposed;

    /// <summary>Prepares a context at the requested level, optionally stamps every frame with a content checksum and copies the supplied raw dictionary into the context before the constructor returns.
    /// <para>按请求的级别准备上下文，可选地为每帧盖上内容校验和，并在构造函数返回前把给定的原始字典复制进上下文。</para> </summary>
    /// <param name="level">Zstandard compression level handed to the engine; <c>3</c> is the engine default and its strategy is <c>dfast</c>.
    /// <para>交给引擎的 Zstandard 压缩级别；<c>3</c> 为引擎默认级别，其策略是 <c>dfast</c>。</para> </param>
    /// <param name="contentChecksum">Whether each frame carries a content checksum. A caller that must fail loudly on a wrong or missing dictionary passes <see langword="true"/>, because the checksum is what turns silently wrong plaintext into a reported decode error.
    /// <para>每帧是否附带内容校验和。字典用错或缺失时必须明确失败的调用方传入 <see langword="true"/>， 因为正是校验和把静默错误的明文变成可见的解压错误。</para> </param>
    /// <param name="dictionary">Exact raw dictionary bytes, copied into the context here; an empty span leaves the context dictionary-free and no dictionary call is made.
    /// <para>完全一致的原始字典字节，在此即复制进上下文；空区间使上下文保持无字典， 也不发起任何字典加载调用。</para> </param>
    /// <exception cref="CompressionException">Zstandard refused a context setting or the dictionary, which the engine reports as its own error and this library re-reports as a compression failure with the engine message as the inner exception. The half-prepared engine context is released before this exception leaves the constructor, so a failed construction leaks nothing.
    /// <para>Zstandard 拒绝某项上下文设置或字典；引擎会报告自己的错误，本库把它重新报为压缩失败，并以引擎消息作为 内部异常。抛出该异常前，已部分准备的引擎上下文会被释放，因此构造失败不会泄漏。</para> </exception>
    public ZstdFrameCompressor(int level, bool contentChecksum, ReadOnlySpan<byte> dictionary)
    {
        _compressor = Prepare(level, contentChecksum, dictionary);
    }

    /// <summary>Encodes the source into exactly one complete frame written from the start of the destination.
    /// <para>把源数据编码为一个完整的帧，并写入目标区间的起始处。</para> </summary>
    /// <param name="source">Frame plaintext. An empty source still produces a valid empty frame rather than a zero-length result.
    /// <para>帧的明文。空源仍然产出合法的空调帧，而不是零长度结果。</para> </param>
    /// <param name="destination">Destination span, which must already offer at least <see cref="MaximumCompressedSize(int)"/> bytes for <paramref name="source"/>; no byte is written past its length.
    /// <para>目标 span，</para><para>其容量必须已经不小于按 <paramref name="source"/> 算出的 <see cref="MaximumCompressedSize(int)"/> 字节数；</para><para>不会有任何字节越过其长度边界。</para> </param>
    /// <returns>The number of frame bytes written at the start of <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 起始处的帧字节数。</para> </returns>
    /// <exception cref="ObjectDisposedException">This context has been released.
    /// <para>本上下文已被释放。</para> </exception>
    /// <exception cref="CompressionException">The frame did not fit <paramref name="destination"/> or Zstandard refused the session; the engine message is preserved as the inner exception. The context is deliberately kept alive, because only its owner can decide whether to keep encoding with it or release it.
    /// <para>帧无法容纳于 <paramref name="destination"/>，或 Zstandard 拒绝该会话；引擎消息作为内部异常保留。 上下文刻意保持存活，因为只有它的属主能决定继续用它编码还是将其释放。</para> </exception>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _compressor.Wrap(source, destination);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD frame compression failed. ZSTD 帧压缩失败。", ex);
        }
    }

    /// <summary>
    /// Reports the largest byte count one frame of the given plaintext can occupy, which is the room a caller must
    /// reserve before calling <see cref="Compress"/>.
    /// <para>给出给定明文长度的一帧最多可能占用的字节数，也就是调用方在调用 <see cref="Compress"/> 前必须预留的容量。</para>
    /// </summary>
    /// <param name="sourceLength">Length in bytes of the frame plaintext. It must already be a non-negative value bounded by the calling
    /// format, and the arithmetic here does not probe for overflow.
    /// <para>帧明文的字节长度；调用方须保证其为非负且受自身格式上限约束的值，此处的算术不做溢出探测。</para></param>
    /// <returns>The engine's own compression bound for that length, which already covers frame headers, block boundaries and
    /// incompressible payloads.
    /// <para>引擎针对该长度给出的压缩上界，其中已涵盖帧头部、块边界与无法压缩的载荷。</para></returns>
    public static int MaximumCompressedSize(int sourceLength) => Compressor.GetCompressBound(sourceLength);

    /// <summary>
    /// Releases the engine context. Frames produced earlier stay valid and a second call does nothing.
    /// <para>释放引擎上下文。已经产出的帧依然有效，重复调用不产生任何效果。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compressor.Dispose();
    }

    /// <summary>
    /// Builds the engine context and applies the settings that Zstandard is able to reject, converting a rejection into
    /// a <see cref="CompressionException"/> after the half-built context has been released.
    /// <para>构建引擎上下文并应用 Zstandard 可能拒绝的各项设置；一旦遭到拒绝，先释放已部分构建的上下文，再把失败转为
    /// <see cref="CompressionException"/>。</para>
    /// </summary>
    /// <param name="level">
    /// Compression level handed to the engine. <para>交给引擎的压缩级别。</para>
    /// </param>
    /// <param name="contentChecksum">
    /// Whether the content-checksum parameter is enabled. <para>是否启用内容校验和参数。</para>
    /// </param>
    /// <param name="dictionary">
    /// Raw dictionary bytes to copy in, or empty. <para>要复制进来的原始字典字节，空表示无字典。</para>
    /// </param>
    /// <returns>
    /// A live engine context already carrying every requested setting.
    /// <para>已带有全部所请求设置的活跃引擎上下文。</para>
    /// </returns>
    private static Compressor Prepare(int level, bool contentChecksum, ReadOnlySpan<byte> dictionary)
    {
        Compressor? compressor = null;
        try
        {
            compressor = new Compressor(level);
            if (contentChecksum)
            {
                compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            }

            if (!dictionary.IsEmpty)
            {
                compressor.LoadDictionary(dictionary);
            }

            return compressor;
        }
        catch (ZstdException ex)
        {
            compressor?.Dispose();
            throw new CompressionException(
                "ZSTD frame compression context could not be prepared. ZSTD 帧压缩上下文无法准备就绪。", ex);
        }
    }
}