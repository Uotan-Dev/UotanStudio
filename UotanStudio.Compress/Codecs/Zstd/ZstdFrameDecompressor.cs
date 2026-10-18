using System.Buffers;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Compress.Codecs.Zstd;

/// <summary>
/// Caller-owned Zstandard context that decodes frames through a streaming span interface, keeps its raw dictionary and
/// window bound across calls and hands back the status the engine reported instead of a collapse of it.
/// <para>调用方持有的 Zstandard 上下文：以流式 span 接口解码帧，跨调用保持原始字典与窗口上限，并原样交回引擎报告的状态，
/// 而不是把它折叠成更粗略的结果。</para>
/// </summary>
/// <remarks>
/// <para>How long one instance lives and which frames it is applied to belongs to the caller; this type keeps no cache.
/// Reuse is only sound when every frame re-establishes its session boundary, so the caller calls <see cref="Reset"/>
/// first and writes <see cref="MaximumWindowLog"/> afterwards: a frame abandoned part way through by cancellation,
/// truncation or invalid data leaves the session mid-flight, the reset is what returns it to a clean boundary, and the
/// window bound is a property of the session that is about to decode the next frame rather than of the one just
/// abandoned.</para>
/// <para>实例存活多久、用于哪些帧，都由调用方决定；本类型不持有缓存。复用只有在每帧都重新建立会话边界时才是可靠的：
/// 调用方先调用 <see cref="Reset"/>，之后再写入 <see cref="MaximumWindowLog"/>。被取消、截断或非法数据中途放弃的帧会把
/// 会话留在半途，重置正是把它恢复到干净的状态边界；而窗口上限属于即将解码下一帧的那个会话，不属于刚刚放弃的那个，
/// 所以要写在重置之后。</para>
/// <para>Formats that decode a whole frame in one step without owning a context stay on
/// <see cref="ZstdBlockCodec"/>, which builds or rents an engine context per block and returns the decoded length.</para>
/// <para>一次性整帧解码又不愿持有上下文的格式，继续使用 <see cref="ZstdBlockCodec"/>：它按块新建或借用引擎上下文，
/// 并返回解码后的长度。</para>
/// </remarks>
public sealed class ZstdFrameDecompressor : IDisposable
{
    private readonly Decompressor _decompressor;
    private int _maximumWindowLog;
    private bool _disposed;

    /// <summary>
    /// Builds a dictionary-free decoder for frames that were encoded without an external dictionary.
    /// <para>为不带外部字典编码的帧构建一个无字典解码器。</para>
    /// </summary>
    public ZstdFrameDecompressor()
    {
        _decompressor = Prepare(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds a decoder and copies the supplied raw dictionary into it, so every frame decoded by this context may
    /// use that dictionary.
    /// <para>构建解码器并把给定的原始字典复制进去，使本上下文解码的每一帧都能使用该字典。</para>
    /// </summary>
    /// <param name="dictionary">Exact raw dictionary bytes registered with the compressor, copied here. An empty span is accepted and
    /// behaves like the parameterless constructor: no dictionary call is made.
    /// <para>与压缩器登记的完全一致的原始字典字节，在此复制。空区间也可以接受，行为与无参构造函数一致：不发起任何 字典加载调用。</para></param>
    /// <exception cref="CompressionException">Zstandard rejected the dictionary, which the engine reports as its own error and this library re-reports as a compression failure with the engine message as the inner exception. The half-prepared engine context is released before this exception leaves the constructor, so a failed construction leaks nothing. <para>Zstandard 拒绝该字典；引擎会报告自己的错误，本库把它重新报为压缩失败，并以引擎消息作为内部异常。 抛出该异常前，已部分准备的引擎上下文会被释放，因此构造失败不会泄漏。</para></exception>
    public ZstdFrameDecompressor(ReadOnlySpan<byte> dictionary)
    {
        _decompressor = Prepare(dictionary);
    }

    /// <summary>
    /// Largest window a frame decoded by this context may declare, written as the engine's decode window bound.
    /// <para>本上下文所解码帧可以声明的最大窗口，以引擎的解码窗口上限参数写入。</para>
    /// </summary>
    /// <value>
    /// The bound last written through this property, or <c>0</c> while no bound has been written and the engine default
    /// still stands. Reading never touches the engine; writing applies the value immediately.
    /// <para>最近一次经由本属性写入的上限；尚未写入任何上限时为 <c>0</c>，此时引擎默认上限仍然有效。读取不会接触引擎，
    /// 写入则立即应用该取值。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">
    /// This context has been released. <para>本上下文已被释放。</para>
    /// </exception>
    /// <exception cref="CompressionException">
    /// Zstandard rejected the bound, which happens outside the range the engine accepts for the window log; the engine
    /// message is preserved as the inner exception. The value is not recorded, so reading the property still reports the
    /// bound that is actually in force.
    /// <para>Zstandard 拒绝该上限，即取值落在引擎为窗口对数接受的区间之外；引擎消息作为内部异常保留。该取值不会被记录，
    /// 因此读取本属性仍然给出实际生效的上限。</para>
    /// </exception>
    public int MaximumWindowLog
    {
        get => _maximumWindowLog;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, value);
            }
            catch (ZstdException ex)
            {
                throw new CompressionException("ZSTD decode window bound was rejected. ZSTD 解码窗口上限被拒绝。", ex);
            }

            _maximumWindowLog = value;
        }
    }

    /// <summary>
    /// Discards the session left behind by the previous frame so the next one starts from a clean boundary. The
    /// dictionary stays registered; the window bound is a parameter and is therefore written again after this call.
    /// <para>丢弃上一帧遗留的会话，使下一帧从干净的状态边界开始。字典保持已登记状态；窗口上限属于参数，所以要在本调用
    /// 之后重新写入。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// This context has been released. <para>本上下文已被释放。</para>
    /// </exception>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decompressor.ResetStream();
    }

    /// ///
    /// <summary>Feeds part of a frame to the decoder and takes back whatever plaintext the decoder produced. ///
    /// <para>把帧的一部分喂给解码器，并取回解码器产出的明文。</para></summary>
    /// ///
    /// <param name="source">Untrusted frame bytes that have not been handed over yet; a complete frame usually needs several calls. ///
    /// <para>尚未交付的、按不可信输入处理的帧字节；一个完整的帧通常需要多次调用才能喂完。</para></param>
    /// ///
    /// <param name="destination">Span written from its start with the plaintext produced by this call. ///
    /// <para>从起始处写入本次调用产出明文的目标 span。</para></param>
    /// ///
    /// <param name="consumed">Bytes taken from the front of <paramref name="source"/>. ///
    /// <para>从 <paramref name="source"/> 头部取走的字节数。</para></param>
    /// ///
    /// <param name="written">Bytes produced into the front of <paramref name="destination"/>. ///
    /// <para>写入 <paramref name="destination"/> 起始处的产出字节数。</para></param>
    /// ///
    /// <returns>The engine's own status for this call: <see cref="OperationStatus.Done"/> ends a frame, <see cref="OperationStatus.NeedMoreData"/> and <see cref="OperationStatus.DestinationTooSmall"/> mean the caller should continue feeding or drain what was produced, and <see cref="OperationStatus.InvalidData"/> means the bytes are not decodable as given. Every one of them reaches the caller unchanged, because a decoding loop has to tell "not yet" from "not valid". ///
    /// <para>引擎针对本次调用给出的状态：<see cref="OperationStatus.Done"/> 表示一帧结束，<see cref="OperationStatus.NeedMoreData"/> 与 <see cref="OperationStatus.DestinationTooSmall"/> 表示调用方应当继续投喂或把已产出的内容取走，<see cref="OperationStatus.InvalidData"/> 表示给定字节无法按其解码；四态全部原样交给调用方，因为解码循环必须能分辨「还没完」与「根本不合法」。</para></returns>
    /// ///
    /// <exception cref="ObjectDisposedException">This context has been released. ///
    /// <para>本上下文已被释放。</para></exception>
    /// ///
    /// <exception cref="CompressionException">Zstandard reported a hard error rather than a status, which is how an over-large window in untrusted input surfaces; the engine message is preserved as the inner exception. The context is deliberately kept alive, because only its owner can decide whether to reset and continue or release it. ///
    /// <para>Zstandard 给出的是硬错误而不是状态，不可信输入里过大的窗口就是这样浮现的；引擎消息作为内部异常保留。 上下文刻意保持存活，因为只有它的属主能决定重置后继续还是将其释放。</para></exception>
    public OperationStatus Decompress(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        out int consumed,
        out int written)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _decompressor.UnwrapStream(source, destination, out consumed, out written);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException("ZSTD frame decompression failed. ZSTD 帧解压失败。", ex);
        }
    }

    /// <summary>
    /// Releases the engine context. A second call does nothing.
    /// <para>释放引擎上下文，重复调用不产生任何效果。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decompressor.Dispose();
    }

    /// <summary>
    /// Builds the engine context and loads the raw dictionary into it, converting a rejected dictionary into a
    /// <see cref="CompressionException"/> after the half-built context has been released.
    /// <para>构建引擎上下文并把原始字典载入其中；一旦字典被拒绝，先释放已部分构建的上下文，再把失败转为
    /// <see cref="CompressionException"/>。</para>
    /// </summary>
    /// <param name="dictionary">
    /// Raw dictionary bytes to copy in, or empty. <para>要复制进来的原始字典字节，空表示无字典。</para>
    /// </param>
    /// <returns>
    /// A live engine context that carries the dictionary when one was supplied.
    /// <para>在给定字典时已携带该字典的活跃引擎上下文。</para>
    /// </returns>
    private static Decompressor Prepare(ReadOnlySpan<byte> dictionary)
    {
        Decompressor? decompressor = null;
        try
        {
            decompressor = new Decompressor();
            if (!dictionary.IsEmpty)
            {
                decompressor.LoadDictionary(dictionary);
            }

            return decompressor;
        }
        catch (ZstdException ex)
        {
            decompressor?.Dispose();
            throw new CompressionException(
                "ZSTD frame decompression context could not be prepared. ZSTD 帧解压上下文无法准备就绪。", ex);
        }
    }
}