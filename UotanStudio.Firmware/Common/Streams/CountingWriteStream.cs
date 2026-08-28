namespace UotanStudio.Firmware.Common.Streams;

/// <summary>
/// 统计写入字节并拒绝超过指定上限的写入流包装器。
/// <para>Wraps a writable stream, counts bytes, and rejects writes exceeding a specified limit.</para>
/// </summary>
public sealed class CountingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;

    /// <summary>
    /// 创建受限计数写入流。
    /// <para>Creates a limited counting write stream.</para>
    /// </summary>
    /// <param name="inner">可写基础流。<para>The writable underlying stream.</para></param>
    /// <param name="maximumBytes">允许写入的最大字节数。<para>The maximum number of bytes that may be written.</para></param>
    /// <param name="leaveOpen">释放后是否保持基础流打开。<para>Whether to leave the underlying stream open after disposal.</para></param>
    /// <param name="cancellationToken">每次写入前检查的取消令牌。<para>The cancellation token checked before each write.</para></param>
    public CountingWriteStream(Stream inner, long maximumBytes, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        _inner = inner;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
    }

    /// <summary>已写入的字节数。<para>Gets the number of bytes written.</para></summary>
    public long BytesWritten { get; private set; }
    /// <summary>允许写入的最大字节数。<para>Gets the maximum number of bytes allowed.</para></summary>
    public long MaximumBytes => _maximumBytes;
    /// <inheritdoc />
    public override bool CanRead => false;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => !_disposed;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

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
        _cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(buffer.Length);
        _inner.Write(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(buffer.Length);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    /// <inheritdoc />
    public override void Flush() { ThrowIfDisposed(); _inner.Flush(); }
    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) { ThrowIfDisposed(); return _inner.FlushAsync(cancellationToken); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !_leaveOpen) _inner.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }

    private void EnsureAllowed(int count)
    {
        long next;
        try { next = checked(BytesWritten + count); }
        catch (OverflowException) { throw new IOException("The maximum output byte count was exceeded."); }
        if (next > _maximumBytes) throw new IOException($"The maximum output byte count of {_maximumBytes} was exceeded.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}