using System.Security.Cryptography;

namespace UotanStudio.Firmware.NTPi.Helpers;

/// <summary>
/// <para>限制读取字节数的非可定位只读流。</para>
/// A non-seekable read-only stream that limits reads to a fixed byte count.
/// </summary>
/// <remarks>
/// <para>用于在未知总长度的流(例如 CryptoStream)上限定解码器可读取的确切字节数。</para>
/// Used to bound the exact number of bytes a decoder may consume from a stream whose
/// total length is unknown (for example a CryptoStream).
/// </remarks>
internal sealed class ReadLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// <para>使用指定上限包装一个可读流(不拥有底层流)。</para>
    /// Wraps a readable stream with a byte limit without taking ownership of the underlying stream.
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

    /// <inheritdoc />
    public override bool CanRead => !_disposed;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _length;
    /// <inheritdoc />
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
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

// NtpiHashingReadStream 和 NtpiHashingWriteStream 已迁移至 Common/Streams/HashingReadStream.cs 和 Common/Streams/HashingWriteStream.cs。
// NtpiHashingReadStream and NtpiHashingWriteStream have been migrated to Common/Streams/HashingReadStream.cs and Common/Streams/HashingWriteStream.cs.