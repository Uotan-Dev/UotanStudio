using System.Security.Cryptography;

namespace UotanStudio.Firmware.NTPi.Helpers;

internal sealed class ReadLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Wraps a readable stream with a byte limit without taking ownership of the underlying stream.
    /// <para>使用指定上限包装一个可读流(不拥有底层流)。</para>
    /// </summary>
    public ReadLimitedStream(Stream inner, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The underlying stream must be readable.", nameof(inner));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => !_disposed;
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
        ValidateBuffer(buffer, offset, count);
        ThrowIfDisposed();
        var requested = (int)Math.Min(count, _length - _position);
        if (requested == 0)
        {
            return 0;
        }
        var read = _inner.Read(buffer, offset, requested);
        _position = checked(_position + read);
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested == 0)
        {
            return 0;
        }
        var read = _inner.Read(buffer[..requested]);
        _position = checked(_position + read);
        return read;
    }

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
    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    public override void Flush() { }

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
    }
}
