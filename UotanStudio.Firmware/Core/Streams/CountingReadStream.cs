namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Counts bytes read from the underlying stream and reports each read through an optional callback.
/// Owns the child stream unless <c>leaveOpen</c> is set. Non-seekable by design.
/// <para>统计从底层流读出的字节数，并通过可选回调上报每次读取；除非 <c>leaveOpen</c> 被设置，
/// 否则在释放时一并释放底层流。设计上不可定位。</para>
/// </summary>
/// <remarks>Mirrors <see cref="CountingWriteStream"/> on the read side: the running count is compared
/// against a known total to derive progress, for example Brotli compression reporting against the
/// source file length.
/// <para>在读侧与 <see cref="CountingWriteStream"/> 对称：把已读字节与已知总长换算成进度，例如 Brotli
/// 压缩按源文件长度上报。</para></remarks>
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
    /// <param name="inner">Readable stream the bytes are taken from.
    /// <para>读取字节的来源流。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/> 仍保持打开。</para></param>
    /// <param name="cancellationToken">Token checked before each read and after each accepted byte count, including reads issued through <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/>.
    /// <para>在每次读取前以及每次确认到的字节数后检查的令牌，经由 <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/> 发起的读取同样检查。</para></param>
    /// <param name="bytesRead">Optional callback invoked with the size of each read that returned at least one byte.
    /// <para>可选回调，对每次返回至少一个字节的读取收到本次的字节数。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be read.
    /// <para><c>inner</c> 不可读。</para></exception>
    public CountingReadStream(Stream inner, bool leaveOpen = false, CancellationToken cancellationToken = default, Action<int>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        _bytesRead = bytesRead;
    }

    /// <summary>Total bytes read so far.
    /// <para>已累计读取的字节数。</para></summary>
    /// <value>The cumulative count handed to callers since construction.
    /// <para>自构造以来累计交给调用方的字节数。</para></value>
    public long BytesRead { get; private set; }

    /// <summary>The wrapper is readable until it is disposed.
    /// <para>包装器在释放前均可读。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanRead => !_disposed;

    /// <summary>Always <c>false</c>: the wrapper is sequential by design and never repositions the source.
    /// <para>恒为 <c>false</c>：包装器按设计为顺序访问，永不重定位来源流。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanSeek => false;

    /// <summary>Always <c>false</c>: the wrapper only counts bytes taken from the source.
    /// <para>恒为 <c>false</c>：包装器只统计从来源流取出的字节。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanWrite => false;

    /// <summary>Not supported: the wrapper does not query the source length.
    /// <para>不受支持：包装器不查询来源流长度。</para></summary>
    /// <value>Reading always throws <see cref="NotSupportedException"/>.
    /// <para>读取始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Length => throw new NotSupportedException();

    /// <summary>The read position, expressed through the running byte counter.
    /// <para>读取位置，通过累计字节计数器体现。</para></summary>
    /// <value>Getting returns the cumulative <see cref="BytesRead"/>; setting always throws
    /// <see cref="NotSupportedException"/>.
    /// <para>读取时返回累计的 <see cref="BytesRead"/>；设置始终抛出 <see cref="NotSupportedException"/>。</para></value>
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    /// <summary>
    /// Reads bytes from the stream.
    /// <para>从流中读取字节。</para>
    /// </summary>
    /// <param name="buffer">Destination array receiving the bytes read from the wrapped stream.
    /// <para>接收被包装流读取字节的目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where the copied bytes start.
    /// <para><paramref name="buffer"/> 中复制字节的起始索引。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes returned by the wrapped stream, added to the running counter;
    /// <c>0</c> at end of stream. <para>被包装流返回的字节数，会计入累计计数；流结束时为 <c>0</c>。</para></returns>
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
    /// <param name="buffer">Destination span to fill; an empty span reads nothing and leaves the counter
    /// unchanged. <para>要填充的目标区间；空区间不读取且不改动计数。</para></param>
    /// <returns>The number of bytes returned by the wrapped stream, added to the running counter;
    /// <c>0</c> at end of stream. <para>被包装流返回的字节数，会计入累计计数；流结束时为 <c>0</c>。</para></returns>
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

    /// <summary>Throws: a read-only counting wrapper has nothing to flush.
    /// <para>抛出异常：只读计数包装器没有可刷新的内容。</para></summary>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>Throws because the counting wrapper is read-only.
    /// <para>计数包装器为只读，调用时抛出异常。</para></summary>
    /// <param name="buffer">Ignored. <para>被忽略。</para></param>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="count">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Throws because the wrapper does not reposition the source stream.
    /// <para>包装器不会重定位来源流，调用时抛出异常。</para></summary>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="origin">Ignored. <para>被忽略。</para></param>
    /// <returns>Never returns. <para>永不返回。</para></returns>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Throws because the wrapper never resizes the source stream.
    /// <para>包装器不会调整来源流大小，调用时抛出异常。</para></summary>
    /// <param name="value">Ignored. <para>被忽略。</para></param>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
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