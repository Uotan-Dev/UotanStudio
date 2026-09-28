using System.Security.Cryptography;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Write-only stream wrapper that folds every written byte range into an incremental SHA-256 digest
/// before handing it to the inner stream. Read, seek and length operations are unsupported.
/// <para>只写流包装器，在把写入范围交给底层流之前先将其并入增量 SHA-256 摘要。不支持读取、定位和
/// 长度操作。</para>
/// </summary>
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
    /// <param name="inner">Writable stream the bytes are forwarded to.
    /// <para>字节转发到的可写流。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器不会释放 <paramref name="inner"/>。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be written.
    /// <para><c>inner</c> 不可写。</para></exception>
    public HashingWriteStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Gets the total number of bytes forwarded to the inner stream since construction.
    /// <para>获取自构造以来已转发到底层流的字节总数。</para>
    /// </summary>
    /// <value>The cumulative forwarded byte count; it is not affected by digest reads.
    /// <para>累计转发的字节数；读取摘要不会使其重置。</para></value>
    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// Returns the SHA-256 hex digest of the data written so far.
    /// <para>返回已写入数据的 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <returns>The 64-character lowercase hexadecimal digest; the accumulated state is cleared, so later writes start a new digest.
    /// <para>64 位小写十六进制摘要；调用后累积状态被清空，之后的写入将从新的摘要重新开始。</para></returns>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>
    /// Returns the raw SHA-256 digest bytes of the data written so far.
    /// <para>返回已写入数据的 SHA-256 原始字节摘要。</para>
    /// </summary>
    /// <returns>The 32-byte digest; the accumulated state is cleared, so later writes start a new digest.
    /// <para>32 字节摘要；调用后累积状态被清空，之后的写入将从新的摘要重新开始。</para></returns>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    /// <summary>Always <c>false</c>: the wrapper only digests and forwards writes.
    /// <para>恒为 <c>false</c>：包装器只对写入做摘要并转发。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanRead => false;

    /// <summary>Always <c>false</c>: seeking would let writes skip the digest, so it is blocked.
    /// <para>恒为 <c>false</c>：定位会让写入绕过摘要计算，因此被禁止。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanSeek => false;

    /// <summary>Writable until the wrapper is disposed.
    /// <para>包装器在释放前均可写。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanWrite => !_disposed;

    /// <summary>Not supported: the wrapper never queries the inner stream's length.
    /// <para>不受支持：包装器从不查询底层流的长度。</para></summary>
    /// <value>Reading always throws <see cref="NotSupportedException"/>.
    /// <para>读取始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Length => throw new NotSupportedException();

    /// <summary>The write cursor, expressed through the running byte counter.
    /// <para>写入游标，通过累计字节计数器体现。</para></summary>
    /// <value>Getting returns the cumulative <see cref="BytesWritten"/>; setting always throws
    /// <see cref="NotSupportedException"/>.
    /// <para>读取时返回累计的 <see cref="BytesWritten"/>；设置始终抛出 <see cref="NotSupportedException"/>。</para></value>
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