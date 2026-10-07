using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Firmware.OtaDelta.Diff;

/// <summary>
/// Read-only, non-seekable stream that decompresses a Brotli stream with strict framing: trailing
/// bytes after the final block, a truncated stream, or a corrupt stream all throw
/// <see cref="InvalidDataException"/>, unlike the tolerant <see cref="BrotliStream"/> decoder.
/// <para>只读且不可 seek 的流，以严格帧语义解压 Brotli 流：最终块之后的尾随字节、被截断的流或损坏的流
/// 均抛出 <see cref="InvalidDataException"/>，与宽容的 <see cref="BrotliStream"/> 解码器不同。</para>
/// </summary>
/// <param name="input">
/// Source stream containing exactly one complete Brotli stream.
/// <para>恰好包含一个完整 Brotli 流的源流。</para>
/// </param>
public sealed class StrictBrotliReadStream(Stream input) : Stream
{
    private BrotliDecoder _decoder;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _offset;
    private int _count;
    private bool _finished;

    /// <summary>
    /// Reads decompressed bytes, rejecting trailing bytes, truncation and corruption.
    /// <para>读取解压后的字节，拒绝尾随字节、截断与损坏数据。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>解压数据的目标区间。</para></param>
    /// <returns>The number of bytes read, or zero at the end of the stream.
    /// <para>读取的字节数；流结束时为零。</para></returns>
    /// <exception cref="InvalidDataException">Thrown when the stream is corrupt, truncated, or has
    /// trailing bytes. <para>流损坏、被截断或含尾随字节时抛出。</para></exception>
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _finished) return 0;
        while (true)
        {
            if (_count == 0) { _offset = 0; _count = input.Read(_buffer); }
            var status = _decoder.Decompress(_buffer.AsSpan(_offset, _count), buffer, out var consumed, out var written);
            _offset += consumed; _count -= consumed;
            if (status == OperationStatus.InvalidData) throw new InvalidDataException("Invalid Brotli patch stream.");
            if (status == OperationStatus.Done)
            {
                if (_count != 0 || input.ReadByte() != -1) throw new InvalidDataException("Trailing bytes in Brotli patch stream.");
                _finished = true; return written;
            }
            if (written > 0) return written;
            if (consumed == 0) throw new InvalidDataException("Truncated Brotli patch stream.");
        }
    }
    /// <summary>
    /// Reads decompressed bytes into the array region. <para>将解压后的字节读入数组区域。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">Offset in <paramref name="buffer"/>. <para><paramref name="buffer"/> 中的偏移。</para></param>
    /// <param name="count">Maximum bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes read, or zero at the end of the stream.
    /// <para>读取的字节数；流结束时为零。</para></returns>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>
    /// Releases the underlying Brotli decoder. <para>释放底层 Brotli 解码器。</para>
    /// </summary>
    /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.
    /// <para>由 <see cref="IDisposable.Dispose"/> 调用时为 true。</para></param>
    protected override void Dispose(bool disposing) { if (disposing) _decoder.Dispose(); base.Dispose(disposing); }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <summary>
    /// Always false; the stream is not seekable. <para>始终为 false；该流不可 seek。</para>
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
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <param name="offset">Displacement ignored; the Brotli output has no positional model. <para>被忽略的偏移量；Brotli 解压输出没有位置模型。</para></param>
    /// <param name="origin">Reference point ignored together with the call. <para>随调用一并被忽略的参照点。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <param name="value">Requested length ignored; the stream never resizes its source. <para>被忽略的请求长度；该流从不改变源流大小。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <param name="buffer">Array never touched, since the stream is read-only. <para>从不接触的数组，因为该流只读。</para></param>
    /// <param name="offset">Index ignored together with the call. <para>随调用一并被忽略的索引。</para></param>
    /// <param name="count">Byte count ignored together with the call. <para>随调用一并被忽略的字节数。</para></param>
    /// <exception cref="NotSupportedException">Always thrown. <para>始终抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}