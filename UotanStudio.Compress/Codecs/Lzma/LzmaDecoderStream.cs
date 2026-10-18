using SCLzmaStream = SharpCompress.Compressors.LZMA.LzmaStream;

namespace UotanStudio.Compress.Codecs.Lzma;

/// <summary>
/// LZMA decode stream over the compression engine in the two shapes vendor containers need: LZMA1 with the coder
/// properties, the compressed size and the decompressed size the header declares, or LZMA2 with the single
/// dictionary byte its chunk framing carries.
/// <para>LZMA 解码流，基于压缩引擎实现，覆盖厂商容器需要的两种形态：LZMA1 由头部声明的编码器属性、压缩大小与
/// 解压大小共同确定；LZMA2 只由其分块框架携带的单个字典字节确定。</para>
/// </summary>
/// <remarks>
/// <para>The properties and sizes are forwarded to the engine untouched, so the framing rules a format was written
/// against stay in the format: reading a header, bounding the input to the declared payload and rejecting an output
/// length that disagrees with the header all remain the caller's checks.</para>
/// <para>属性与大小都原样转发给引擎，因此写入时依据的封装规则仍留在格式层：读取头部、把输入限制到声明的
/// 负载长度，以及拒绝与头部不一致的输出长度，这些都由调用方自行校验。</para>
/// <para>Reading produces plaintext and ends when the engine reaches the declared size, the LZMA2 end marker, or the
/// end of the bounded input; a stream that consumed fewer bytes than declared is observable on the caller's own
/// bounded input stream, never here.</para>
/// <para>读取产出明文，并在引擎到达声明大小、LZMA2 结束标记或有界输入末尾时结束；消耗字节数少于声明值的情况
/// 只能在调用方自己的有界输入流上观察到，本类型不做该检查。</para>
/// </remarks>
public sealed class LzmaDecoderStream : Stream
{
    private readonly SCLzmaStream _coder;

    /// <summary>
    /// Creates an LZMA1 decode stream over a payload whose sizes are already known.
    /// <para>在大小已知的 LZMA1 载荷上创建解码流。</para>
    /// </summary>
    /// <param name="coderProperties">The five coder-properties byte stored outside the payload: the lc/lp/pb packing in the first byte and the
    /// little-endian dictionary size that follows.
    /// <para>存于载荷之外的 5 字节编码器属性：首字节的 lc/lp/pb 打包，随后是小端字典大小。</para></param>
    /// <param name="input">Stream positioned at the first encoded byte and left open; the payload follows the properties the caller
    /// already consumed.
    /// <para>定位在首个编码字节处的流，调用后保持打开；调用方已消费属性字节，其后续即载荷。</para></param>
    /// <param name="compressedSize">Encoded byte count the caller declared, which bounds how much of <paramref name="input"/> the engine
    /// consumes.
    /// <para>调用方声明的编码字节数，用于限定引擎从 <paramref name="input"/> 消耗多少内容。</para></param>
    /// <param name="decompressedSize">Plaintext byte count the caller declared, which bounds how much the engine produces for a payload written
    /// without an end marker.
    /// <para>调用方声明的明文字节数，用于限定引擎为不含结束标记的载荷产出多少内容。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="coderProperties"/> or <paramref name="input"/> is <see langword="null"/>. <para><paramref name="coderProperties"/> 或 <paramref name="input"/> 为 <see langword="null"/>。</para></exception>
    public LzmaDecoderStream(byte[] coderProperties, Stream input, long compressedSize, long decompressedSize)
    {
        ArgumentNullException.ThrowIfNull(coderProperties);
        ArgumentNullException.ThrowIfNull(input);
        _coder = SCLzmaStream.Create(coderProperties, input, compressedSize, decompressedSize);
    }

    /// <summary>
    /// Creates a decode stream whose framing is carried by the payload itself.
    /// <para>创建封装由载荷自身携带的解码流。</para>
    /// </summary>
    /// <param name="dictionaryProperties">The single dictionary-size byte an LZMA2 stream declares, which selects the
    /// chunked framing; the array is forwarded verbatim, so its length is what the engine reads.
    /// <para>LZMA2 流声明的单个字典大小字节，用于选择分块封装；该数组原样转发，因此引擎读取的就是它的长度。</para></param>
    /// <param name="input">Stream positioned at the first payload byte and left open.
    /// <para>定位在首个载荷字节处的流，调用后保持打开。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="dictionaryProperties"/> or <paramref name="input"/> is
    /// <see langword="null"/>.
    /// <para><paramref name="dictionaryProperties"/> 或 <paramref name="input"/> 为 <see langword="null"/>。</para></exception>
    public LzmaDecoderStream(byte[] dictionaryProperties, Stream input)
    {
        ArgumentNullException.ThrowIfNull(dictionaryProperties);
        ArgumentNullException.ThrowIfNull(input);
        _coder = SCLzmaStream.Create(dictionaryProperties, input);
    }

    /// <summary>Read support of the wrapped decoder. <para>被包装解码器的可读性。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanRead => _coder.CanRead;

    /// <summary>Seek support of the wrapped decoder. <para>被包装解码器的可定位性。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanSeek => _coder.CanSeek;

    /// <summary>Write support of the wrapped decoder, which the engine reports as unwritable.
    /// <para>被包装解码器的可写性，引擎将其报告为不可写。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanWrite => _coder.CanWrite;

    /// <summary>Length reported by the wrapped decoder stream.
    /// <para>被包装解码流报告的长度。</para></summary>
    /// <value>Forwarded to the engine, whose decoded length may be unknown until the payload is read.
    /// <para>直接转发给引擎，而引擎在载荷读完之前可能无法确定解压长度。</para></value>
    public override long Length => _coder.Length;

    /// <summary>Position reported or imposed by the wrapped decoder stream.
    /// <para>被包装解码流报告或被要求设置的位置。</para></summary>
    /// <value>Both accessors are forwarded to the engine. <para>两个访问器都直接转发给引擎。</para></value>
    public override long Position
    {
        get => _coder.Position;
        set => _coder.Position = value;
    }

    /// <summary>
    /// Flushes the wrapped decoder stream.
    /// <para>刷新被包装的解码流。</para>
    /// </summary>
    public override void Flush() => _coder.Flush();

    /// <summary>
    /// Reads decoded plaintext bytes.
    /// <para>读取解码得到的明文字节。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> to fill from.
    /// <para><paramref name="buffer"/> 中开始填充的索引。</para></param>
    /// <param name="count">Requested byte count. <para>请求的字节数。</para></param>
    /// <returns>The number of plaintext bytes stored at <paramref name="buffer"/>[<paramref name="offset"/>], or
    /// <c>0</c> once the payload is exhausted. <para>写入 <paramref name="buffer"/>[<paramref name="offset"/>] 的明文字节数；
    /// 载荷读完后为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count) => _coder.Read(buffer, offset, count);

    /// <summary>
    /// Reads decoded plaintext bytes.
    /// <para>读取解码得到的明文字节。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>目标区间。</para></param>
    /// <returns>The number of plaintext bytes stored at the start of <paramref name="buffer"/>, or <c>0</c> once the
    /// payload is exhausted. <para>写入 <paramref name="buffer"/> 起始处的明文字节数；载荷读完后为 <c>0</c>。</para></returns>
    public override int Read(Span<byte> buffer) => _coder.Read(buffer);

    /// <summary>Writing is rejected; a decoder stream cannot be written.
    /// <para>不支持写入；解码流不能写入。</para></summary>
    /// <param name="buffer">Source array.
    /// <para>源数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> to read from.
    /// <para><paramref name="buffer"/> 中开始读取的索引。</para></param>
    /// <param name="count">Byte count.
    /// <para>字节数。</para></param>
    public override void Write(byte[] buffer, int offset, int count) => _coder.Write(buffer, offset, count);

    /// <summary>
    /// Seeks the decoder; the engine rejects the call.
    /// <para>定位解码器；引擎会拒绝该调用。</para>
    /// </summary>
    /// <param name="offset">Offset to apply. <para>要应用的偏移。</para></param>
    /// <param name="origin">Origin the offset is relative to. <para>偏移所依据的起点。</para></param>
    /// <returns>The position reported by the engine. <para>引擎返回的位置。</para></returns>
    public override long Seek(long offset, SeekOrigin origin) => _coder.Seek(offset, origin);

    /// <summary>Resizing is rejected; the engine refuses it on a decoder stream.
    /// <para>不支持改变长度；引擎会拒绝对解码流提出该调用。</para></summary>
    /// <param name="value">Requested length; ignored because the call is rejected. <para>请求的长度；调用被拒绝，故被忽略。</para></param>
    public override void SetLength(long value) => _coder.SetLength(value);

    /// <summary>
    /// Releases the decoder and its buffers, leaving the input stream the caller owns open.
    /// <para>释放解码器及其缓冲区，调用方拥有的输入流保持打开。</para>
    /// </summary>
    /// <param name="disposing">Set by <see cref="Stream.Dispose()"/> when the instance is released deliberately rather than finalized.
    /// <para>由 <see cref="Stream.Dispose()"/> 在实例被主动释放（而非由终结器回收）时设置。</para></param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coder.Dispose();
        }

        base.Dispose(disposing);
    }
}