namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Write-through stream wrapper that accumulates the forwarded byte count and refuses any write
/// whose total would exceed a fixed budget. The check happens before the inner write, so the limit
/// is never overshot.
/// <para>透传写入的流包装器，累计转发的字节数，并拒绝总量超出固定预算的写入。上限检查发生在写入底层
/// 流之前，因此不会超出限制。</para>
/// </summary>
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
    /// <param name="inner">Writable stream the bytes are forwarded to.
    /// <para>字节转发到的可写流。</para></param>
    /// <param name="maximumBytes">Cumulative byte budget; zero allows no writes at all and must be non-negative.
    /// <para>累计字节预算；为 0 表示不允许任何写入，取值必须非负。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/> 仍保持打开。</para></param>
    /// <param name="cancellationToken">Token checked by every synchronous write.
    /// <para>每次同步写入都会检查的取消令牌。</para></param>
    /// <param name="bytesWritten">Optional callback invoked with the size of each accepted write after it reaches <paramref name="inner"/>.
    /// <para>可选回调，在每次被接受的写入交给 <paramref name="inner"/> 之后收到本次写入的字节数。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be written.
    /// <para><c>inner</c> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>maximumBytes</c> is negative.
    /// <para><c>maximumBytes</c> 为负数。</para></exception>
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

    /// <summary>
    /// Gets the number of bytes forwarded so far.
    /// <para>获取迄今已转发的字节数。</para>
    /// </summary>
    /// <value>The cumulative accepted byte count, always within <see cref="MaximumBytes"/>.
    /// <para>累计被接受的字节数，始终不超过 <see cref="MaximumBytes"/>。</para></value>
    public long BytesWritten { get; private set; }
    /// <summary>
    /// Gets the cumulative write budget supplied at construction.
    /// <para>获取构造时给定的累计写入预算。</para>
    /// </summary>
    /// <value>The inclusive maximum number of bytes that may be forwarded.
    /// <para>允许转发的最大字节数（含该值）。</para></value>
    public long MaximumBytes => _maximumBytes;
    /// <summary>Always <c>false</c>: the wrapper only forwards writes to the inner stream.
    /// <para>恒为 <c>false</c>：包装器只把写入转发给底层流。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanRead => false;

    /// <summary>Reports whether the inner stream supports seeking; the byte counter does not restrict it.
    /// <para>反映底层流是否支持定位；字节计数器不对其作额外限制。</para></summary>
    /// <value><see langword="true"/> when the inner stream is seekable.
    /// <para>底层流可定位时为 <see langword="true"/>。</para></value>
    public override bool CanSeek => _inner.CanSeek;

    /// <summary>Writable until the wrapper is disposed.
    /// <para>包装器在释放前均可写。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanWrite => !_disposed;

    /// <summary>The inner stream's current length; the write budget is tracked separately in <see cref="MaximumBytes"/>.
    /// <para>底层流的当前长度；写入预算由 <see cref="MaximumBytes"/> 单独记录。</para></summary>
    /// <value>The value reported by the inner stream, which throws when that stream has no length support.
    /// <para>由底层流报告的值；当该流不支持长度时会抛出异常。</para></value>
    public override long Length => _inner.Length;

    /// <summary>The position within the inner stream; moving it does not change <see cref="BytesWritten"/>.
    /// <para>底层流中的位置；移动位置不会改变 <see cref="BytesWritten"/>。</para></summary>
    /// <value>Getting and setting forward directly to the inner stream, with the same seekability limits.
    /// <para>读写均直接转发到底层流，并遵循其相同的可定位性限制。</para></value>
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    /// <param name="buffer">Source array whose bytes are forwarded to the inner stream.
    /// <para>将其字节转发给底层流的源数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> of the first byte to write.
    /// <para><paramref name="buffer"/> 中待写入首字节的索引。</para></param>
    /// <param name="count">Number of bytes to write; the value counts against <see cref="MaximumBytes"/>.
    /// <para>待写入的字节数；该值会计入 <see cref="MaximumBytes"/> 预算。</para></param>
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
    /// <param name="buffer">Source span forwarded to the inner stream; an empty span still checks the
    /// write budget but transfers nothing.
    /// <para>转发给底层流的目标区间；空区间仍会检查写入预算但不传输任何字节。</para></param>
    /// <exception cref="IOException">Adding <paramref name="buffer"/> to <see cref="BytesWritten"/>
    /// overflows or exceeds <see cref="MaximumBytes"/>.
    /// <para>将 <paramref name="buffer"/> 累加到 <see cref="BytesWritten"/> 后溢出或超出 <see cref="MaximumBytes"/>。</para></exception>
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
    /// <param name="buffer">Bytes forwarded to the inner stream; an empty memory still counts as zero bytes
    /// and is passed through. <para>转发给底层流的字节；为空时按 0 字节计入预算并仍然转发。</para></param>
    /// <param name="cancellationToken">Token checked before the budget test and forwarded to the inner stream;
    /// an already cancelled token throws without writing any byte.
    /// <para>在预算校验前检查并转发给底层流的取消令牌；若令牌已被取消则直接抛出，不写入任何字节。</para></param>
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
    /// <param name="cancellationToken">Token handed to the inner stream; this wrapper only rejects a disposed
    /// counter and does not test the token itself. <para>交给底层流的取消令牌；本包装器只拒绝已释放的计数器，自身不检查该令牌。</para></param>
    public override Task FlushAsync(CancellationToken cancellationToken) { ThrowIfDisposed(); return _inner.FlushAsync(cancellationToken); }
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="buffer">Buffer never touched, since the wrapper forwards writes only.
    /// <para>从不接触的缓冲区，因为包装器只转发写入。</para></param>
    /// <param name="offset">Index rejected together with the call. <para>随调用一并被拒绝的索引。</para></param>
    /// <param name="count">Byte count rejected together with the call. <para>随调用一并被拒绝的字节数。</para></param>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <summary>
    /// Repositions the underlying stream while continuing to count writes.
    /// <para>定位到底层流位置并继续累计写入字节。</para>
    /// </summary>
    /// <param name="offset">Displacement interpreted through <paramref name="origin"/> and handed to the inner
    /// stream unchanged. <para>按 <paramref name="origin"/> 解释并原样交给底层流的偏移量。</para></param>
    /// <param name="origin">Reference point of the displacement. <para>偏移量的参照点。</para></param>
    /// <returns>The new position reported by the inner stream; <see cref="BytesWritten"/> is untouched.
    /// <para>底层流报告的新位置；<see cref="BytesWritten"/> 保持不变。</para></returns>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <summary>
    /// Resizes the underlying stream while continuing to count writes.
    /// <para>调整底层流大小并继续累计写入字节。</para>
    /// </summary>
    /// <param name="value">New length of the inner stream in bytes; the write budget exposed by
    /// <see cref="MaximumBytes"/> and the counter in <see cref="BytesWritten"/> stay unchanged.
    /// <para>底层流的新长度（字节）；<see cref="MaximumBytes"/> 表示的写入预算与 <see cref="BytesWritten"/> 计数均保持不变。</para></param>
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