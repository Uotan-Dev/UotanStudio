using SCLzmaEncoderProperties = SharpCompress.Compressors.LZMA.LzmaEncoderProperties;
using SCLzmaStream = SharpCompress.Compressors.LZMA.LzmaStream;

namespace UotanStudio.Compress.Codecs.Lzma;

/// <summary>
/// LZMA1 encoder stream over the compression engine: plaintext written to it is encoded into the stream given to the
/// constructor, and the coder properties the engine chose stay readable through <see cref="Properties"/> so a
/// container can record them alongside the payload instead of guessing them back.
/// <para>LZMA1 编码器流，基于压缩引擎实现：写入其中的明文会被编码并写入构造时给定的流，而引擎选定的编码器属性
/// 仍可通过 <see cref="Properties"/> 读取，因此容器可以把它们与载荷一起记录，而不必事后反推。</para>
/// </summary>
/// <remarks>
/// <para>The engine's default coder properties are used unchanged, because a container whose bytes are already on
/// disk was written with them; this type adds no level, dictionary or thread knob of its own.</para>
/// <para>引擎自身的默认编码器属性不做改动，因为磁盘上已有的容器字节就是用它们写入的；本类型不额外提供级别、
/// 字典或线程参数。</para>
/// <para>Nothing is framed here: the five property bytes are exposed for the caller to write where its container
/// wants them, and disposal is what flushes the encoder into the output stream.</para>
/// <para>本类型不做封装：5 个属性字节暴露给调用方，由其写到容器需要的位置；只有释放编码器才会把数据刷入输出流。</para>
/// </remarks>
public sealed class LzmaEncoderStream : Stream
{
    private readonly SCLzmaStream _coder;

    /// <summary>
    /// Creates an LZMA1 encoder stream over the destination that receives the encoded bytes.
    /// <para>在接收编码字节的目标流上创建 LZMA1 编码器流。</para>
    /// </summary>
    /// <param name="output">Writable destination fed at its current position; it is left open, and the encoded bytes
    /// only reach it once this instance is disposed. Ownership stays with the caller, which is why a container that
    /// reads the produced length afterwards can still query the stream after disposal.
    /// <para>在其当前位置接收写入的目标流，调用后保持打开；只有本实例被释放后编码字节才全部到达该流。
    /// 所有权仍归调用方，因此需要在释放之后读取产出长度的容器仍可查询该流。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    public LzmaEncoderStream(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _coder = SCLzmaStream.Create(SCLzmaEncoderProperties.Default, false, output);
    }

    /// <summary>
    /// The coder properties the engine encoded with, in the five-byte LZMA-alone layout.
    /// <para>引擎实际编码所用的编码器属性，采用 5 字节的 LZMA-alone 布局。</para>
    /// </summary>
    /// <value>The property bytes as reported by the engine, available as soon as the encoder is constructed and
    /// unchanged by writing or disposal, so a container may record them before the payload is flushed.
    /// <para>引擎报告的属性字节：编码器构造完成后即可读取，写入与释放都不会改变它，因此容器可以在载荷刷出之前就把它记录下来。</para></value>
    public byte[] Properties => _coder.Properties;

    /// <summary>Read support of the wrapped encoder, which the engine reports as unreadable.
    /// <para>被包装编码器的可读性，引擎将其报告为不可读。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanRead => _coder.CanRead;

    /// <summary>Seek support of the wrapped encoder. <para>被包装编码器的可定位性。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanSeek => _coder.CanSeek;

    /// <summary>Write support of the wrapped encoder. <para>被包装编码器的可写性。</para></summary>
    /// <value>Whatever the wrapped engine reports. <para>取值为被包装引擎所报告的结果。</para></value>
    public override bool CanWrite => _coder.CanWrite;

    /// <summary>Length reported by the wrapped encoder stream. <para>被包装编码器流报告的长度。</para></summary>
    /// <value>Forwarded to the engine, which rejects the query on an encoder stream.
    /// <para>直接转发给引擎，而引擎会拒绝对编码器流提出该查询。</para></value>
    public override long Length => _coder.Length;

    /// <summary>Position reported or imposed by the wrapped encoder stream.
    /// <para>被包装编码器流报告或被要求设置的位置。</para></summary>
    /// <value>Both accessors are forwarded to the engine, which rejects them on an encoder stream.
    /// <para>两个访问器都直接转发给引擎，而引擎会拒绝对编码器流提出该操作。</para></value>
    public override long Position
    {
        get => _coder.Position;
        set => _coder.Position = value;
    }

    /// <summary>
    /// Flushes the encoder into the destination stream without closing it.
    /// <para>把编码器数据刷入目标流，但不关闭该流。</para></summary>
    public override void Flush() => _coder.Flush();

    /// <summary>
    /// Reads bytes; the engine rejects the call on an encoder stream.
    /// <para>读取字节；引擎会拒绝对编码器流提出该调用。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> to fill from.
    /// <para><paramref name="buffer"/> 中开始填充的索引。</para></param>
    /// <param name="count">Requested byte count. <para>请求的字节数。</para></param>
    /// <returns>The count reported by the engine. <para>引擎返回的字节数。</para></returns>
    public override int Read(byte[] buffer, int offset, int count) => _coder.Read(buffer, offset, count);

    /// <summary>
    /// Reads bytes; the engine rejects the call on an encoder stream.
    /// <para>读取字节；引擎会拒绝对编码器流提出该调用。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>目标区间。</para></param>
    /// <returns>The count reported by the engine. <para>引擎返回的字节数。</para></returns>
    public override int Read(Span<byte> buffer) => _coder.Read(buffer);

    /// <summary>
    /// Writes plaintext bytes to be encoded.
    /// <para>写入待编码的明文字节。</para></summary>
    /// <param name="buffer">Source array.
    /// <para>源数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> to read from.
    /// <para><paramref name="buffer"/> 中开始读取的索引。</para></param>
    /// <param name="count">Byte count to encode.
    /// <para>待编码的字节数。</para></param>
    public override void Write(byte[] buffer, int offset, int count) => _coder.Write(buffer, offset, count);

    /// <summary>
    /// Writes plaintext bytes to be encoded.
    /// <para>写入待编码的明文字节。</para>
    /// </summary>
    /// <param name="buffer">Source span.
    /// <para>源区间。</para></param>
    public override void Write(ReadOnlySpan<byte> buffer) => _coder.Write(buffer);

    /// <summary>
    /// Seeks the encoder; the engine rejects the call.
    /// <para>定位编码器；引擎会拒绝该调用。</para>
    /// </summary>
    /// <param name="offset">Offset to apply. <para>要应用的偏移。</para></param>
    /// <param name="origin">Origin the offset is relative to. <para>偏移所依据的起点。</para></param>
    /// <returns>The position reported by the engine. <para>引擎返回的位置。</para></returns>
    public override long Seek(long offset, SeekOrigin origin) => _coder.Seek(offset, origin);

    /// <summary>
    /// Resizes the encoder; the engine rejects the call.
    /// <para>调整编码器长度；引擎会拒绝该调用。</para>
    /// </summary>
    /// <param name="value">Requested length.
    /// <para>请求的长度。</para></param>
    public override void SetLength(long value) => _coder.SetLength(value);

    /// <summary>
    /// Disposes the encoder, which is what writes the tail of the LZMA1 payload into the destination stream.
    /// <para>释放编码器，也只有此时 LZMA1 载荷的尾部才会写入目标流。</para>
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