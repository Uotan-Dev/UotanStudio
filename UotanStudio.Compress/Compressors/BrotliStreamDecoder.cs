using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Compress.Compressors;

/// <summary>Buffered incremental Brotli decoder that pulls its compressed bytes from a caller-owned stream and hands the built-in decoder's status back to the caller without applying any framing policy of its own.
/// <para>带缓冲的增量 Brotli 解码器，从调用方持有的流拉取压缩字节，并把内置解码器的状态原样交回调用方，自身不施加任何帧策略。</para> </summary>
/// <remarks>The engine is the built-in <see cref="BrotliDecoder"/>, kept here so that no layer above the library constructs a generic codec; the 64 KiB pull buffer and the read-ahead offset bookkeeping travel with it. Deliberately absent is the single-frame rule: this type does not decide whether a trailing byte or a missing final block is an error, it only reports the status and the consumed byte count so that a container can enforce its own framing contract. Output is unbounded, so callers handling untrusted input must bound their own destination spans.
/// <para>引擎是内置 <see cref="BrotliDecoder"/>，放在这里是为了让库之上的层不再自行构造通用编解码器；64 KiB 拉取缓冲与预读的偏移记账随之一起收纳。刻意不包含的是单帧规则：本类型不判定尾随字节或缺少结束块是否算错误， 只回报状态与消耗字节数，由容器自己执行其帧契约。输出不设上限，处理不可信输入的调用方需自行限定目标区间。</para> </remarks>
/// <param name="input">Readable Brotli stream positioned at its first byte; ownership stays with the caller and disposing this decoder leaves it open.
/// <para>定位在首字节处的可读 Brotli 流；所有权归调用方，释放本解码器不会关闭它。</para> </param>
public sealed class BrotliStreamDecoder(Stream input) : IDisposable
{
    private BrotliDecoder _decoder;
    private readonly Stream _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _offset;
    private int _count;

    /// <summary>
    /// Decodes forward into the destination span, refilling the internal buffer from the source whenever it is drained.
    /// <para>向目标区间继续解码，内部缓冲耗尽时从源流补充。</para>
    /// </summary>
    /// <param name="destination">Span the decoded bytes are written into. <para>写入解压字节的区间。</para></param>
    /// <param name="consumed">Number of buffered input bytes the decoder took on this call; <c>0</c> together with zero
    /// <paramref name="written"/> means the decoder cannot advance.
    /// <para>本次调用中解码器取走的缓冲输入字节数；与 <c>0</c> 的 <paramref name="written"/> 同时出现表示解码器无法推进。</para></param>
    /// <param name="written">Number of decoded bytes produced into <paramref name="destination"/>.
    /// <para>写入 <paramref name="destination"/> 的解压字节数。</para></param>
    /// <returns>The status reported by the built-in decoder, untouched by this type.
    /// <para>内置解码器回报的状态，本类型不做改写。</para></returns>
    public OperationStatus Decode(Span<byte> destination, out int consumed, out int written)
    {
        if (_count == 0)
        {
            _offset = 0;
            _count = _input.Read(_buffer);
        }

        var status = _decoder.Decompress(_buffer.AsSpan(_offset, _count), destination, out consumed, out written);
        _offset += consumed;
        _count -= consumed;
        return status;
    }

    /// <summary>
    /// Reports whether the source carries data that the decoded frame did not consume.
    /// <para>报告源流上是否存在已解帧未消费的数据。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the internal buffer still holds bytes or the source yields another one; a
    /// single-frame caller treats either case as trailing data.
    /// <para>内部缓冲仍有剩余、或源流还能再给出一个字节时返回 <see langword="true"/>；要求单帧的调用方把这两种情况都视为尾随数据。</para></returns>
    public bool HasTrailingInput() => _count != 0 || _input.ReadByte() != -1;

    /// <summary>
    /// Releases the built-in Brotli decoder; the caller's input stream is left open.
    /// <para>释放内置 Brotli 解码器；调用方的输入流保持打开。</para>
    /// </summary>
    public void Dispose() => _decoder.Dispose();
}