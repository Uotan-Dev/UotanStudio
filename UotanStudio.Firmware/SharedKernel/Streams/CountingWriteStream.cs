namespace UotanStudio.Firmware.SharedKernel.Streams;

public sealed class CountingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;
    private readonly Action<int>? _bytesWritten;

    /// <summary>
    /// Creates a limited counting write stream.
    /// <para>创建受限计数写入流。</para>
    /// </summary>
    public CountingWriteStream(Stream inner, long maximumBytes, bool leaveOpen = false, CancellationToken cancellationToken = default, Action<int>? bytesWritten = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        _inner = inner;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _bytesWritten = bytesWritten;
    }

    public long BytesWritten { get; private set; }
    public long MaximumBytes => _maximumBytes;
    public override bool CanRead => false;
    // 镜像构建（EXT4/EROFS）会随机定位写入，计数流必须把定位操作透传到底层可寻址流，
    // 否则进度路径会在首次 WriteAt 时抛出 NotSupportedException。
    // Random-access image builders seek before writing; forward positioning to the underlying
    // seekable stream so a progress-enabled build can still place blocks anywhere.
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => !_disposed;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

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
        _bytesWritten?.Invoke(buffer.Length);
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
        _bytesWritten?.Invoke(buffer.Length);
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
    /// Repositions the underlying stream while continuing to count writes.
    /// <para>定位到底层流位置并继续累计写入字节。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <summary>
    /// Resizes the underlying stream while continuing to count writes.
    /// <para>调整底层流大小并继续累计写入字节。</para>
    /// </summary>
    public override void SetLength(long value) => _inner.SetLength(value);

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