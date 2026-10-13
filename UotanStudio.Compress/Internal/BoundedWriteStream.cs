namespace UotanStudio.Compress.Internal;

/// <summary>
/// Write-through stream wrapper that accumulates the forwarded byte count and refuses any write whose total would
/// exceed a fixed budget. The check happens before the inner write, so the limit is never overshot.
/// <para>透传写入的流包装器，累计转发的字节数，并拒绝总量超出固定预算的写入。上限检查发生在写入底层流之前，
/// 因此不会超出限制。</para>
/// </summary>
/// <remarks>
/// The streaming decompression entry point applies one inclusive output cap to every codec through this wrapper, so
/// a decompression bomb is refused before any byte beyond the budget reaches the destination. The firmware shared
/// kernel exposes a wider counting stream, but this project sits below that kernel and must stay independent of it;
/// only the bounded-write behaviour this API needs is modelled here, and the over-budget failure is reported as
/// <see cref="IOException"/> exactly as the callers of the decompression entry point already expect.
/// <para>流式解压入口通过本包装器对每种编解码器施加同一个含上限，因此解压炸弹在任何超出预算的字节落地前就被拒绝。
/// 固件共享内核提供了更完整的计数流，但本项目位于该内核之下、必须保持独立；这里只建模本 API 所需的有界写入行为，
/// 超预算失败按解压入口调用方既有预期以 <see cref="IOException"/> 上报。</para>
/// </remarks>
internal sealed class BoundedWriteStream : Stream
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
    /// <param name="inner">
    /// Writable stream the bytes are forwarded to.
    /// <para>字节转发到的可写流。</para>
    /// </param>
    /// <param name="maximumBytes">
    /// Cumulative byte budget; zero allows no writes at all and must be non-negative.
    /// <para>累计字节预算；为 0 表示不允许任何写入，取值必须非负。</para>
    /// </param>
    /// <param name="leaveOpen">
    /// When <see langword="true"/>, disposing this wrapper leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本包装器后 <paramref name="inner"/> 仍保持打开。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked by every synchronous write and before the budget test.
    /// <para>每次同步写入以及预算校验前都会检查的取消令牌。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.
    /// <para><paramref name="inner"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="inner"/> cannot be written.
    /// <para><paramref name="inner"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.
    /// <para><paramref name="maximumBytes"/> 为负数。</para></exception>
    public BoundedWriteStream(Stream inner, long maximumBytes, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(inner));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        _inner = inner;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
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

    /// <summary>
    /// Whether the wrapper can be read from.
    /// <para>包装器是否可读。</para>
    /// </summary>
    /// <value>Always <see langword="false"/>; the wrapper only forwards writes.
    /// <para>恒为 <see langword="false"/>；包装器只转发写入。</para></value>
    public override bool CanRead => false;

    /// <summary>
    /// Whether the wrapper supports seeking; the byte counter does not restrict the inner stream.
    /// <para>包装器是否支持定位；字节计数器不对底层流作额外限制。</para>
    /// </summary>
    /// <value><see langword="true"/> when the inner stream is seekable.
    /// <para>底层流可定位时为 <see langword="true"/>。</para></value>
    public override bool CanSeek => _inner.CanSeek;

    /// <summary>
    /// Whether the wrapper can still be written to.
    /// <para>包装器是否仍可写入。</para>
    /// </summary>
    /// <value><see langword="true"/> while undisposed. <para>未释放时为 <see langword="true"/>。</para></value>
    public override bool CanWrite => !_disposed;

    /// <summary>
    /// The inner stream's current length; the write budget is tracked separately in <see cref="MaximumBytes"/>.
    /// <para>底层流的当前长度；写入预算由 <see cref="MaximumBytes"/> 单独记录。</para>
    /// </summary>
    /// <value>The value reported by the inner stream, which throws when that stream has no length support.
    /// <para>由底层流报告的值；当该流不支持长度时会抛出异常。</para></value>
    public override long Length => _inner.Length;

    /// <summary>
    /// The position within the inner stream; moving it does not change <see cref="BytesWritten"/>.
    /// <para>底层流中的位置；移动位置不会改变 <see cref="BytesWritten"/>。</para>
    /// </summary>
    /// <value>Getting and setting forward directly to the inner stream, with the same seekability limits.
    /// <para>读写均直接转发到底层流，并遵循其相同的可定位性限制。</para></value>
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    /// <param name="buffer">
    /// Source span forwarded to the inner stream; an empty span still tests the budget but transfers nothing.
    /// <para>转发给底层流的源区间；空区间仍会校验预算但不传输任何字节。</para>
    /// </param>
    /// <exception cref="IOException">
    /// Adding <paramref name="buffer"/> to <see cref="BytesWritten"/> would overflow or exceed
    /// <see cref="MaximumBytes"/>; nothing is forwarded in that case.
    /// <para>将 <paramref name="buffer"/> 累加到 <see cref="BytesWritten"/> 后溢出或超出 <see cref="MaximumBytes"/>；
    /// 此时不会转发任何字节。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><see cref="CancellationToken"/> was signaled.
    /// <para><see cref="CancellationToken"/> 已被触发。</para></exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(buffer.Length);
        _inner.Write(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

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
    /// <exception cref="IOException">The write would exceed <see cref="MaximumBytes"/>.
    /// <para>本次写入将超出 <see cref="MaximumBytes"/>。</para></exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }

        Write(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Flushes buffered data to the underlying stream.
    /// <para>将缓冲数据刷新到底层流。</para>
    /// </summary>
    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.Flush();
    }

    /// <summary>
    /// Throws because the counting wrapper is read-only.
    /// <para>计数包装器为只读，调用时抛出异常。</para>
    /// </summary>
    /// <param name="buffer">Ignored. <para>被忽略。</para></param>
    /// <param name="offset">Ignored. <para>被忽略。</para></param>
    /// <param name="count">Ignored. <para>被忽略。</para></param>
    /// <returns>Never returns. <para>永不返回。</para></returns>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Repositions the underlying stream while continuing to count writes.
    /// <para>定位到底层流位置并继续累计写入字节。</para>
    /// </summary>
    /// <param name="offset">Displacement interpreted through <paramref name="origin"/> and handed to the inner stream unchanged.
    /// <para>按 <paramref name="origin"/> 解释并原样交给底层流的偏移量。</para></param>
    /// <param name="origin">Reference point of the displacement. <para>偏移量的参照点。</para></param>
    /// <returns>The new position reported by the inner stream; <see cref="BytesWritten"/> is untouched.
    /// <para>底层流报告的新位置；<see cref="BytesWritten"/> 保持不变。</para></returns>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <summary>
    /// Resizes the underlying stream while continuing to count writes.
    /// <para>调整底层流大小并继续累计写入字节。</para>
    /// </summary>
    /// <param name="value">
    /// New length of the inner stream in bytes; <see cref="MaximumBytes"/> and <see cref="BytesWritten"/> stay unchanged.
    /// <para>底层流的新长度（字节）；<see cref="MaximumBytes"/> 与 <see cref="BytesWritten"/> 均保持不变。</para>
    /// </param>
    public override void SetLength(long value) => _inner.SetLength(value);

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

    private void EnsureAllowed(int count)
    {
        long next;
        try
        {
            next = checked(BytesWritten + count);
        }
        catch (OverflowException)
        {
            throw new IOException("The maximum output byte count was exceeded.");
        }

        if (next > _maximumBytes)
        {
            throw new IOException($"The maximum output byte count of {_maximumBytes} was exceeded.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}