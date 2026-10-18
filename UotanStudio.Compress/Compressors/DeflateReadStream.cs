using UotanStudio.Compress.Internal;
using SCCompressionMode = SharpCompress.Compressors.CompressionMode;
using SCDeflateStream = SharpCompress.Compressors.Deflate.DeflateStream;

namespace UotanStudio.Compress.Compressors;

///     /// <summary>Read-only raw DEFLATE stream that reports how many compressed input bytes the inflater consumed, so a caller can locate the payload that follows the member it just decoded.
///     /// <para>只读的原始 DEFLATE 流，会报告解压器实际消耗的压缩输入字节数，便于调用方定位刚解完的成员之后的后续数据。</para> </summary>
///     /// <remarks>The inflater is the SharpCompress zlib port, which counts the input bytes it took from the source, unlike the built-in <see cref="System.IO.Compression.DeflateStream"/> that exposes no such counter and unlike the <see cref="DeflateCompressor"/> strict reader that hands back a plain <see cref="Stream"/>. The counter is the reason this reader exists: a caller scanning a container for members has to know the exact byte length of the member it decoded, and the source position is not that number because the reader pulls input in chunks. Damage is reported as <see cref="CompressionException"/> with the codec failure kept as <see cref="Exception.InnerException"/>, so no codec-specific exception type reaches the caller. Decoded output is unbounded; callers handling untrusted input must bound their own reads.
///     /// <para>解压器采用 SharpCompress 的 zlib 移植，它会统计从源流取走的输入字节，而内置 <see cref="System.IO.Compression.DeflateStream"/> 没有这样的计数器， <see cref="DeflateCompressor"/> 的严格读取器返回的也只是普通 <see cref="Stream"/>。该计数器正是本读取器存在的理由：在容器里逐个定位成员的调用方必须知道已解成员的确切字节长度， 而源流位置并不是这个数，因为读取器是按块拉取输入的。损坏会以 <see cref="CompressionException"/> 上报，编解码故障保留为 <see cref="Exception.InnerException"/>，因此调用方不会看到任何编解码器私有异常类型。解压输出不设上限， 处理不可信输入的调用方需自行界定读取。</para> </remarks>
///     /// <param name="input">Readable raw DEFLATE stream positioned at its first byte; ownership stays with the caller and disposing this stream leaves it open.
///     /// <para>定位在首字节处的可读原始 deflate 流；所有权归调用方，释放本流不会关闭它。</para> </param>
public sealed class DeflateReadStream(Stream input) : Stream
{
    private readonly SCDeflateStream _decoder = new(new NonDisposingStream(input), SCCompressionMode.Decompress);

    /// <summary>
    /// Number of compressed input bytes the inflater has taken from the source so far.
    /// <para>解压器迄今从源流取走的压缩输入字节数。</para>
    /// </summary>
    /// <value>
    /// The running input count, which stays at <c>0</c> until the first read pulls bytes. Bytes the inflater buffered
    /// beyond the terminating block are not credited, so for a payload carrying trailing data the value equals the
    /// length of the decoded member, which is what lets a scanner address the byte right after it.
    /// <para>累进的输入计数：首次读取取到字节之前保持为 <c>0</c>。解压器在结束块之后额外缓冲的字节不会被记入，
    /// 因此带尾随数据时该值恰好等于已解成员的长度，扫描器正是靠它定位紧随其后的字节。</para>
    /// </value>
    public long ConsumedInput => _decoder.TotalIn;

    /// <summary>
    /// Reads decoded bytes, reporting any codec damage as <see cref="CompressionException"/>.
    /// <para>读取解压后的字节，任何编解码故障都以 <see cref="CompressionException"/> 上报。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>解压数据的目标区间。</para></param>
    /// <returns>The number of bytes read, or zero at the end of the decoded member.
    /// <para>读取的字节数；到达解压成员末尾时为零。</para></returns>
    /// <exception cref="CompressionException">The payload is not a raw DEFLATE stream, or it is corrupt or truncated.
    /// <para>数据不是原始 DEFLATE 流，或已损坏、截断。</para></exception>
    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _decoder.Read(buffer);
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Reads decoded bytes into the array region. <para>将解压后的字节读入数组区域。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">Offset in <paramref name="buffer"/>. <para><paramref name="buffer"/> 中的偏移。</para></param>
    /// <param name="count">Maximum bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes read, or zero at the end of the decoded member.
    /// <para>读取的字节数；到达解压成员末尾时为零。</para></returns>
    /// <exception cref="CompressionException">The payload is not a raw DEFLATE stream, or it is corrupt or truncated.
    /// <para>数据不是原始 DEFLATE 流，或已损坏、截断。</para></exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _decoder.Read(buffer, offset, count);
        }
        catch (Exception ex)
        {
            throw new CompressionException("DEFLATE 解压失败", ex);
        }
    }

    /// <summary>
    /// Releases the inflater; the caller's input stream is left open.
    /// <para>释放解压器；调用方的输入流保持打开。</para></summary>
    /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.
    /// <para>由 <see cref="IDisposable.Dispose"/> 调用时为 true。</para></param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _decoder.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <summary>
    /// Always false; the decompressed output has no positional model.
    /// <para>始终为 false；解压输出没有位置模型。</para>
    /// </summary>
    /// <value>False. <para>始终为 false。</para></value>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override long Length => throw new NotSupportedException();

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <summary>Seeking is rejected; the stream is forward-only.
    /// <para>不支持定位；该流只能向前读取。</para></summary>
    /// <param name="offset">Ignored; the call is rejected before the value is used. <para>被忽略；调用在用到该值之前即被拒绝。</para></param>
    /// <param name="origin">Ignored together with the call. <para>随调用一并被忽略。</para></param>
    /// <returns>Never returns; the call always throws. <para>永不返回；该调用始终抛出。</para></returns>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Resizing is rejected; the decoded length is fixed by the compressed input.
    /// <para>不支持改变长度；解码长度由压缩输入决定。</para></summary>
    /// <param name="value">Requested length; ignored because the call is rejected. <para>请求的长度；调用被拒绝，故被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Not supported.
    /// <para>不支持。</para>
    /// </summary>
    /// <param name="buffer">Array never touched, since the stream is read-only.
    /// <para>从不接触的数组，因为该流只读。</para></param>
    /// <param name="offset">Index ignored together with the call.
    /// <para>随调用一并被忽略的索引。</para></param>
    /// <param name="count">Byte count ignored together with the call.
    /// <para>随调用一并被忽略的字节数。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}