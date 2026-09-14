namespace UotanStudio.Firmware.SharedKernel.Streams;

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
    public BoundedReadStream(Stream inner, long length, bool leaveOpen = false, object? syncRoot = null)
        : this(inner, inner?.Position ?? throw new ArgumentNullException(nameof(inner)), length, leaveOpen, syncRoot)
    {
    }

    /// <summary>
    /// Creates a bounded read stream at the specified absolute offset and length.
    /// <para>创建指定绝对偏移和长度的有界只读流。</para>
    /// </summary>
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
        // Position 设置必须与其它并发使用者的读写互斥（syncRoot 存在时），
        // 否则构造期间的定位会破坏在飞读取的数据。
        // The position set must be mutually exclusive with concurrent users' reads/writes
        // (when a syncRoot exists); otherwise positioning during construction corrupts in-flight reads.
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

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return _length; } }
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