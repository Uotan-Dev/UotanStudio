namespace UotanStudio.Compress.Internal;

/// <summary>
/// Counts bytes read from the underlying stream and reports each read through an optional callback. Owns the child
/// stream unless <c>leaveOpen</c> is set. Non-seekable by design.
/// <para>统计从底层流读出的字节数，并通过可选回调上报每次读取；除非 <c>leaveOpen</c> 被设置，
/// 否则在释放时一并释放底层流。设计上不可定位。</para>
/// </summary>
/// <remarks>
/// The file-level compression API turns these byte counts into progress against a known total. The firmware shared
/// kernel offers a wider counting stream, but this project sits below that kernel and must stay independent of it,
/// so only the read-side counting this API needs is modelled here.
/// <para>文件级压缩 API 把这些字节计数换算成相对已知总长的进度。固件共享内核提供了更完整的计数流，但本项目位于该内核
/// 之下、必须保持独立，因此这里只建模本 API 所需的读侧计数。</para>
/// </remarks>
internal sealed class ByteCountingReadStream : Stream
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
    /// <param name="inner">
    /// Readable stream the bytes are taken from.
    /// <para>读取字节的来源流。</para>
    /// </param>
    /// <param name="leaveOpen">
    /// When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/> 仍保持打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before each read and after each accepted byte count.
    /// <para>在每次读取前以及每次确认到的字节数后检查的令牌。</para>
    /// </param>
    /// <param name="bytesRead">
    /// Optional callback invoked with the size of each read that returned at least one byte.
    /// <para>可选回调，对每次返回至少一个字节的读取收到本次的字节数。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.
    /// <para><paramref name="inner"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="inner"/> cannot be read.
    /// <para><paramref name="inner"/> 不可读。</para></exception>
    public ByteCountingReadStream(Stream inner, bool leaveOpen = false, CancellationToken cancellationToken = default, Action<int>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _bytesRead = bytesRead;
    }

    /// <summary>
    /// Total bytes read so far.
    /// <para>已累计读取的字节数。</para>
    /// </summary>
    /// <value>The cumulative count handed to callers since construction.
    /// <para>自构造以来累计交给调用方的字节数。</para></value>
    public long BytesRead { get; private set; }

    /// <summary>
    /// Whether the wrapper can still be read from.
    /// <para>包装器是否仍可读取。</para>
    /// </summary>
    /// <value><see langword="true"/> while undisposed. <para>未释放时为 <see langword="true"/>。</para></value>
    public override bool CanRead => !_disposed;

    /// <summary>
    /// Whether the wrapper supports seeking; it never repositions the source.
    /// <para>包装器是否支持定位；它永不重定位来源流。</para>
    /// </summary>
    /// <value>Always <see langword="false"/>. <para>恒为 <see langword="false"/>。</para></value>
    public override bool CanSeek => false;

    /// <summary>
    /// Whether the wrapper supports writing; it only counts bytes taken from the source.
    /// <para>包装器是否支持写入；它只统计从来源流取出的字节。</para>
    /// </summary>
    /// <value>Always <see langword="false"/>. <para>恒为 <see langword="false"/>。</para></value>
    public override bool CanWrite => false;

    /// <summary>
    /// The length of the underlying stream, which the wrapper does not query.
    /// <para>底层流的长度；包装器不查询该值。</para>
    /// </summary>
    /// <value>Reading always throws <see cref="NotSupportedException"/>.
    /// <para>读取始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Length => throw new NotSupportedException();

    /// <summary>
    /// The read position, expressed through the running byte counter.
    /// <para>读取位置，通过累计字节计数器体现。</para>
    /// </summary>
    /// <value>Getting returns the cumulative <see cref="BytesRead"/>; setting always throws <see cref="NotSupportedException"/>.
    /// <para>读取时返回累计的 <see cref="BytesRead"/>；设置始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the wrapped stream.
    /// <para>从被包装流读取字节。</para>
    /// </summary>
    /// <param name="buffer">Destination span to fill; an empty span reads nothing and leaves the counter unchanged.
    /// <para>要填充的目标区间；空区间不读取且不改动计数。</para></param>
    /// <returns>The number of bytes returned by the wrapped stream, added to the running counter; <c>0</c> at end of stream.
    /// <para>被包装流返回的字节数，会计入累计计数；流结束时为 <c>0</c>。</para></returns>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            Add(read);
        }

        return read;
    }

    /// <summary>
    /// Reads bytes from the wrapped stream.
    /// <para>从被包装流读取字节。</para>
    /// </summary>
    /// <param name="buffer">Destination array receiving the bytes read from the wrapped stream.
    /// <para>接收被包装流读取字节的目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where the copied bytes start.
    /// <para><paramref name="buffer"/> 中复制字节的起始索引。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes returned by the wrapped stream, added to the running counter; <c>0</c> at end of stream.
    /// <para>被包装流返回的字节数，会计入累计计数；流结束时为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }

        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Asynchronously reads bytes from the wrapped stream.
    /// <para>异步从被包装流读取字节。</para>
    /// </summary>
    /// <param name="buffer">Destination span to fill; an empty span reads nothing and leaves the counter unchanged.
    /// <para>要填充的目标区间；空区间不读取且不改动计数。</para></param>
    /// <param name="cancellationToken">Token forwarded to the underlying read.
    /// <para>传递给底层读取操作的令牌。</para></param>
    /// <returns>A task yielding the bytes returned by the wrapped stream, added to the running counter.
    /// <para>任务完成后给出被包装流返回的字节数，会计入累计计数。</para></returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            Add(read);
        }

        return read;
    }

    /// <summary>
    /// Flushes the wrapped stream.
    /// <para>刷新被包装流。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">Always; a read-only counting wrapper has nothing to flush.
    /// <para>始终抛出；只读计数包装器没有可刷新的内容。</para></exception>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>
    /// Writes to the wrapper.
    /// <para>向包装器写入。</para>
    /// </summary>
    /// <param name="buffer">Ignored. <para>被忽略。</para></param>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="count">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always; the counting wrapper is read-only.
    /// <para>始终抛出；计数包装器为只读。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Repositions the wrapper.
    /// <para>重定位包装器。</para>
    /// </summary>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="origin">Ignored. <para>被忽略。</para></param>
    /// <returns>Never returns. <para>永不返回。</para></returns>
    /// <exception cref="NotSupportedException">Always; the wrapper does not reposition the source stream.
    /// <para>始终抛出；包装器不会重定位来源流。</para></exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Resizes the wrapper.
    /// <para>调整包装器大小。</para>
    /// </summary>
    /// <param name="value">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always; the wrapper never resizes the source stream.
    /// <para>始终抛出；包装器不会调整来源流大小。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="IDisposable.Dispose"/>.
    /// <para>由 <see cref="IDisposable.Dispose"/> 调用时为 <see langword="true"/>。</para></param>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

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