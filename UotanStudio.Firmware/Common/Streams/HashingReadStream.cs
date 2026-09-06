using System.Security.Cryptography;

namespace UotanStudio.Firmware.Common.Streams;

public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;
    private bool _disposed;

    /// <summary>
    /// Creates a hashing read stream wrapper.
    /// <para>创建哈希读取流包装器。</para>
    /// </summary>
    public HashingReadStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public long BytesRead => _bytesRead;

    /// <summary>
    /// Returns the SHA-256 hex digest of the data read so far.
    /// <para>返回当前累积数据的 SHA-256 十六进制摘要。</para>
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>
    /// Returns the raw SHA-256 digest bytes of the data read so far.
    /// <para>返回当前累积数据的 SHA-256 原始字节摘要。</para>
    /// </summary>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
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

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
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

    /// <summary>
    /// Reads bytes from the stream asynchronously.
    /// <para>异步从流中读取字节。</para>
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer[..read].Span);
            _bytesRead = checked(_bytesRead + read);
        }
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
}
