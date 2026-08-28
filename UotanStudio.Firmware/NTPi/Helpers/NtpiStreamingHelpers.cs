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

/// <summary>
/// <para>在读取字节时增量计算 SHA-256 的只读流包装器(不拥有底层流)。</para>
/// A read-only stream wrapper that incrementally hashes SHA-256 as bytes are read
/// (does not own the underlying stream).
/// </summary>
internal sealed class NtpiHashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;
    private bool _disposed;

    /// <summary>
    /// <para>包装一个可读流用于单次读取时的流式哈希。</para>
    /// Wraps a readable stream for streaming hash computation during a single read pass.
    /// </summary>
    public NtpiHashingReadStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (!inner.CanRead)
        {
            throw new ArgumentException("The underlying stream must be readable.", nameof(inner));
        }
    }

    /// <summary>
    /// <para>返回当前累积数据的 SHA-256 十六进制摘要并重置内部状态。</para>
    /// Returns the SHA-256 hex digest of the data read so far and resets the internal state.
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public long BytesRead => _bytesRead;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _inner.Length;
    /// <inheritdoc />
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
            _bytesRead = checked(_bytesRead + read);
        }
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            _hash.AppendData(buffer[..read]);
            _bytesRead = checked(_bytesRead + read);
        }
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
            _hash.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// <para>在写入字节时增量计算 SHA-256 的写入流包装器。</para>
/// A write stream wrapper that incrementally hashes SHA-256 as bytes are written.
/// </summary>
internal sealed class NtpiHashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;
    private bool _disposed;

    /// <summary>
    /// <para>包装一个可写流用于写出时的流式哈希。</para>
    /// Wraps a writable stream for streaming hash computation while writing.
    /// </summary>
    public NtpiHashingWriteStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (!inner.CanWrite)
        {
            throw new ArgumentException("The underlying stream must be writable.", nameof(inner));
        }
        _leaveOpen = leaveOpen;
    }

    /// <summary>已写入的字节数。<para>The number of bytes written.</para></summary>
    public long BytesWritten => _position;

    /// <summary>
    /// <para>返回已写入数据的 SHA-256 十六进制摘要并重置内部状态。</para>
    /// Returns the SHA-256 hex digest of the data written so far and resets the internal state.
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => !_disposed;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.Length > 0)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
            _position = checked(_position + buffer.Length);
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
            _hash.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}