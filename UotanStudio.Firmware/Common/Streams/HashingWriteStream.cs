using System.Security.Cryptography;

namespace UotanStudio.Firmware.Common.Streams;

/// <summary>
/// 在写入字节时增量计算 SHA-256 的写入流包装器。
/// <para>A write stream wrapper that incrementally hashes SHA-256 as bytes are written.</para>
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesWritten;
    private bool _disposed;

    /// <summary>
    /// 创建哈希写入流包装器。
    /// <para>Creates a hashing write stream wrapper.</para>
    /// </summary>
    /// <param name="inner">可写基础流。<para>The writable underlying stream.</para></param>
    /// <param name="leaveOpen">释放后是否保持基础流打开。<para>Whether to leave the underlying stream open after disposal.</para></param>
    public HashingWriteStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>已写入的字节数。<para>Gets the number of bytes written.</para></summary>
    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// 返回已写入数据的 SHA-256 十六进制摘要。
    /// <para>Returns the SHA-256 hex digest of the data written so far.</para>
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>
    /// 返回已写入数据的 SHA-256 原始字节摘要。
    /// <para>Returns the raw SHA-256 digest bytes of the data written so far.</para>
    /// </summary>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => !_disposed;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("The buffer range is invalid.");
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
            _bytesWritten = checked(_bytesWritten + buffer.Length);
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override void Flush() { ThrowIfDisposed(); _inner.Flush(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
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
