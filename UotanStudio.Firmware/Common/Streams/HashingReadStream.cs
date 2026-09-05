using System.Security.Cryptography;

namespace UotanStudio.Firmware.Common.Streams;

/// <summary>
/// 在读取字节时增量计算 SHA-256 的只读流包装器。
/// <para>A read-only stream wrapper that incrementally hashes SHA-256 as bytes are read.</para>
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;
    private bool _disposed;

    /// <summary>
    /// 创建哈希读取流包装器。
    /// <para>Creates a hashing read stream wrapper.</para>
    /// </summary>
    /// <param name="inner">可读基础流。<para>The readable underlying stream.</para></param>
    /// <param name="leaveOpen">释放后是否保持基础流打开。<para>Whether to leave the underlying stream open after disposal.</para></param>
    public HashingReadStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>已读取的字节数。<para>Gets the number of bytes read.</para></summary>
    public long BytesRead => _bytesRead;

    /// <summary>
    /// 返回当前累积数据的 SHA-256 十六进制摘要。
    /// <para>Returns the SHA-256 hex digest of the data read so far.</para>
    /// </summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>
    /// 返回当前累积数据的 SHA-256 原始字节摘要。
    /// <para>Returns the raw SHA-256 digest bytes of the data read so far.</para>
    /// </summary>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

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

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
}
