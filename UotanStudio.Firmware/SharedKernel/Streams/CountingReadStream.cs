namespace UotanStudio.Firmware.SharedKernel.Streams;

/// <summary>
/// Counts bytes read from the underlying stream and reports each read through an optional callback.
/// Owns the child stream unless <c>leaveOpen</c> is set. Non-seekable by design.
/// <para>统计从底层流读出的字节数，并通过可选回调上报每次读取；除非 <c>leaveOpen</c> 被设置，
/// 否则在释放时一并释放底层流。设计上不可定位。</para>
/// </summary>
/// <remarks>正向镜像 <see cref="CountingWriteStream"/>：读取侧用于把“已读字节 vs 已知总长”
/// 换算成进度，比如 Brotli 压缩按源文件长度上报。</remarks>
public sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<int>? _bytesRead;
    private bool _disposed;

    /// <summary>
    /// Creates a counting read stream.
    /// <para>创建计数读取流。</para>
    /// </summary>
    /// <param name="inner">底层可读流。The readable inner stream.</param>
    /// <param name="leaveOpen">是否在 Dispose 时保留底层流。Whether to leave the inner stream open.</param>
    /// <param name="cancellationToken">取消令牌。Cancellation token.</param>
    /// <param name="bytesRead">每次读取完成后回调本次读取字节数。Callback invoked after each read with the byte count.</param>
    public CountingReadStream(Stream inner, bool leaveOpen = false, CancellationToken cancellationToken = default, Action<int>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _bytesRead = bytesRead;
    }

    /// <summary>Total bytes read so far. 已累计读取的字节数。</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream.
    /// <para>从流中读取字节。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("The buffer range is invalid.");
        var read = Read(buffer.AsSpan(offset, count));
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream.
    /// <para>从流中读取字节。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
        var read = _inner.Read(buffer);
        if (read > 0) Add(read);
        return read;
    }

    /// <summary>
    /// Reads bytes from the stream asynchronously.
    /// <para>异步从流中读取字节。</para>
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) Add(read);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
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

    private void Add(int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        BytesRead = checked(BytesRead + count);
        _bytesRead?.Invoke(count);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}