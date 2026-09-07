namespace UotanStudio.Firmware.SharedKernel.Streams;

public sealed class CountingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;

    /// <summary>
    /// Creates a limited counting write stream.
    /// <para>创建受限计数写入流。</para>
    /// </summary>
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

    public long BytesWritten { get; private set; }
    public long MaximumBytes => _maximumBytes;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

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
        _cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(buffer.Length);
        _inner.Write(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    /// <summary>
    /// Writes bytes to the stream asynchronously.
    /// <para>异步向流写入字节。</para>
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(buffer.Length);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    /// <summary>
    /// Flushes buffered data to the underlying stream.
    /// <para>将缓冲数据刷新到底层流。</para>
    /// </summary>
    public override void Flush() { ThrowIfDisposed(); _inner.Flush(); }
    /// <summary>
    /// Flushes buffered data asynchronously.
    /// <para>异步刷新缓冲数据。</para>
    /// </summary>
    public override Task FlushAsync(CancellationToken cancellationToken) { ThrowIfDisposed(); return _inner.FlushAsync(cancellationToken); }
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
