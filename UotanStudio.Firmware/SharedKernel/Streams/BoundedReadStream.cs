namespace UotanStudio.Firmware.SharedKernel.Streams;

/// <summary>
/// Read-only view over one absolute byte window of a seekable stream. Reads stop at the window end
/// and return 0 there, positions are relative to the window start, and every seek outside the
/// window is rejected. Seeking the underlying stream is performed on demand, so several windows may
/// share one stream when they coordinate through the same <c>syncRoot</c>.
/// <para>对可定位流上一个绝对字节窗口的只读视图。读取到窗口末尾即停止并在该处返回 0，位置均相对于窗口
/// 起点，超出窗口的定位会被拒绝。底层流的定位按需执行，因此多个窗口在通过同一个 <c>syncRoot</c> 协调时
/// 可以共用一条流。</para>
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly object? _syncRoot;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Creates a bounded read stream beginning at the current stream position.
    /// <para>创建从当前流位置开始的有界只读流。</para>
    /// </summary>
    /// <param name="inner">Readable, seekable stream whose current position becomes the window start.
    /// <para>可读且可定位的流，其当前位置即窗口起点。</para></param>
    /// <param name="length">Window size in bytes; zero yields a stream that always reads as exhausted.
    /// <para>窗口大小（字节）；为 0 时读取始终视为已结束。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this view leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本视图后 <paramref name="inner"/> 仍保持打开。</para></param>
    /// <param name="syncRoot">Lock object shared with other views over the same stream, or <see langword="null"/> to move the underlying position unguarded.
    /// <para>与同一底层流上其他视图共用的锁对象；为 <see langword="null"/> 时直接移动底层位置而不加保护。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be read or cannot seek.
    /// <para><c>inner</c> 不可读或不可定位。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>length</c> is negative, or the window reaches past the end of <c>inner</c>.
    /// <para><c>length</c> 为负数，或窗口超出 <c>inner</c> 的末尾。</para></exception>
    public BoundedReadStream(Stream inner, long length, bool leaveOpen = false, object? syncRoot = null)
        : this(inner, inner?.Position ?? throw new ArgumentNullException(nameof(inner)), length, leaveOpen, syncRoot)
    {
    }

    /// <summary>
    /// Creates a bounded read stream at the specified absolute offset and length.
    /// <para>创建指定绝对偏移和长度的有界只读流。</para>
    /// </summary>
    /// <param name="inner">Readable, seekable stream supplying the window bytes.
    /// <para>提供窗口字节的可读且可定位的流。</para></param>
    /// <param name="offset">Absolute offset of the window start within <paramref name="inner"/>; must be non-negative.
    /// <para>窗口起点在 <paramref name="inner"/> 中的绝对偏移，必须非负。</para></param>
    /// <param name="length">Window size in bytes; zero yields a stream that always reads as exhausted.
    /// <para>窗口大小（字节），必须非负；为 0 时读取始终视为已结束。</para></param>
    /// <param name="leaveOpen">When <see langword="true"/>, disposing this view leaves <paramref name="inner"/> open.
    /// <para>为 <see langword="true"/> 时，释放本视图后 <paramref name="inner"/> 仍保持打开。</para></param>
    /// <param name="syncRoot">Lock object shared with other views over the same stream, or <see langword="null"/> to move the underlying position unguarded.
    /// <para>与同一目标流上其他视图共用的锁对象；为 <see langword="null"/> 时直接移动底层位置而不加保护。</para></param>
    /// <exception cref="ArgumentNullException"><c>inner</c> is <see langword="null"/>.
    /// <para><c>inner</c> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><c>inner</c> cannot be read or cannot seek.
    /// <para><c>inner</c> 不可读或不可定位。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>offset</c> or <c>length</c> is negative, or <c>offset</c> plus <c>length</c> exceeds the length of <c>inner</c>.
    /// <para><c>offset</c> 或 <c>length</c> 为负数，或两者之和超出 <c>inner</c> 的长度。</para></exception>
    public BoundedReadStream(Stream inner, long offset, long length, bool leaveOpen = false, object? syncRoot = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > inner.Length || length > inner.Length - offset) throw new ArgumentOutOfRangeException(nameof(length), "The range exceeds the stream length.");

        _inner = inner;
        _start = offset;
        _length = length;
        _leaveOpen = leaveOpen;
        _syncRoot = syncRoot;
        if (_syncRoot is not null)
        {
            lock (_syncRoot)
            {
                _inner.Position = offset;
            }
            return;
        }
        _inner.Position = offset;
    }

    /// <summary>The window is readable until the view is disposed.
    /// <para>窗口在视图释放前均可读。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanRead => !_disposed;

    /// <summary>The window is seekable until the view is disposed; every seek stays inside the window.
    /// <para>窗口在视图释放前均可定位；每次定位都限制在窗口内。</para></summary>
    /// <value><c>true</c> while undisposed. <para>未释放时为 <c>true</c>。</para></value>
    public override bool CanSeek => !_disposed;

    /// <summary>Always <c>false</c>: the window is a read-only view.
    /// <para>恒为 <c>false</c>：窗口是只读视图。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanWrite => false;

    /// <summary>The fixed byte-window length chosen at construction.
    /// <para>构造时确定的固定字节窗口长度。</para></summary>
    /// <value>The window size in bytes; reading it after disposal throws <see cref="ObjectDisposedException"/>.
    /// <para>窗口大小（字节）；释放后读取会抛出 <see cref="ObjectDisposedException"/>。</para></value>
    public override long Length { get { ThrowIfDisposed(); return _length; } }

    /// <summary>The current offset within the window, relative to the window start rather than the inner stream.
    /// <para>窗口内的当前偏移，相对于窗口起点而非底层流。</para></summary>
    /// <value>Getting returns the window-relative position; setting is equivalent to seeking from
    /// <see cref="SeekOrigin.Begin"/>, so a target outside the window throws <see cref="IOException"/>.
    /// <para>读取时返回窗口内位置；设置等同于从 <see cref="SeekOrigin.Begin"/> 定位，
    /// 因此超出窗口的目标会抛出 <see cref="IOException"/>。</para></value>
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set { Seek(value, SeekOrigin.Begin); }
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        ThrowIfDisposed();
        var requested = (int)Math.Min(count, _length - _position);
        if (requested == 0) return 0;
        if (_syncRoot is not null)
        {
            lock (_syncRoot)
            {
                _inner.Position = checked(_start + _position);
                var read = _inner.Read(buffer, offset, requested);
                _position = checked(_position + read);
                return read;
            }
        }
        _inner.Position = checked(_start + _position);
        var r = _inner.Read(buffer, offset, requested);
        _position = checked(_position + r);
        return r;
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested == 0) return 0;
        if (_syncRoot is not null)
        {
            lock (_syncRoot)
            {
                _inner.Position = checked(_start + _position);
                var read = _inner.Read(buffer[..requested]);
                _position = checked(_position + read);
                return read;
            }
        }
        _inner.Position = checked(_start + _position);
        var r = _inner.Read(buffer[..requested]);
        _position = checked(_position + r);
        return r;
    }

    /// <summary>
    /// Reads bytes from the stream asynchronously.
    /// <para>异步从流中读取字节。</para>
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested == 0) return 0;
        if (_syncRoot is not null)
        {
            lock (_syncRoot)
            {
                _inner.Position = checked(_start + _position);
            }

            var read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                _position = checked(_position + read);
            }
            return read;
        }
        _inner.Position = checked(_start + _position);
        var r = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        _position = checked(_position + r);
        return r;
    }

    /// <summary>
    /// Sets the stream position relative to the specified origin.
    /// <para>相对于指定起点设置流位置。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > _length) throw new IOException("Attempted to seek outside the bounded range.");
        if (_syncRoot is not null)
        {
            lock (_syncRoot)
            {
                _position = target;
                _inner.Position = checked(_start + target);
            }
            return target;
        }
        _position = target;
        _inner.Position = checked(_start + target);
        return target;
    }

    /// <summary>
    /// Flushes buffered data to the underlying stream.
    /// <para>将缓冲数据刷新到底层流。</para>
    /// </summary>
    public override void Flush() => ThrowIfDisposed();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("The buffer range is invalid.");
    }
}