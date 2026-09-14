using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal sealed class LockedBoundedReadStream : Stream
{
    private readonly Stream _source;
    private readonly object _syncRoot;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// Initializes a bounded view without taking ownership of the shared stream.
    /// <para>初始化有界视图，但不取得共享流的所有权。</para>
    /// </summary>
    internal LockedBoundedReadStream(Stream source, object syncRoot, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syncRoot);
        if (!source.CanRead || !source.CanSeek || start < 0 || length < 0)
        {
            throw new PartitionExtractionException("Payload stream must be readable and seekable and bounds must be non-negative", string.Empty);
        }

        _source = source;
        _syncRoot = syncRoot;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _length - _position);
        if (count == 0)
        {
            return 0;
        }

        int read;
        lock (_syncRoot)
        {
            _source.Seek(checked(_start + _position), SeekOrigin.Begin);
            read = _source.Read(buffer[..count]);
        }
        _position = checked(_position + read);
        return read;
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public override void Flush() { }
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}