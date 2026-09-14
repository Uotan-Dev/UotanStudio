using System.Security.Cryptography;

namespace UotanStudio.Firmware.SharedKernel.Streams;

public sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesWritten;
    private bool _disposed;

    /// <summary>
    /// Creates a hashing write stream wrapper.
    /// <para>创建哈希写入流包装器。</para>
    /// </summary>
    public HashingWriteStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// Returns the SHA-256 hex digest of the data written so far.
    /// <para>返回已写入数据的 SHA-256 十六进制摘要。</para>
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>
    /// Returns the raw SHA-256 digest bytes of the data written so far.
    /// <para>返回已写入数据的 SHA-256 原始字节摘要。</para>
    /// </summary>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("The buffer range is invalid.");
        Write(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.Length > 0)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
            _bytesWritten = checked(_bytesWritten + buffer.Length);
        }
    }

    /// <summary>
    /// Writes bytes to the stream asynchronously.
    /// <para>异步向流写入字节。</para>
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.Length > 0)
        {
            _hash.AppendData(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytesWritten = checked(_bytesWritten + buffer.Length);
        }
    }

    /// <summary>
    /// Flushes buffered data to the underlying stream.
    /// <para>将缓冲数据刷新到底层流。</para>
    /// </summary>
    public override void Flush() { ThrowIfDisposed(); _inner.Flush(); }
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _hash.Dispose();
            if (!_leaveOpen) _inner.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}