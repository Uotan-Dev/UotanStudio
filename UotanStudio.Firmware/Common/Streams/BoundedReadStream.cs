namespace UotanStudio.Firmware.Common.Streams;

/// <summary>
/// 将可定位流的指定范围公开为只读子流。
/// <para>Exposes a specified range of a seekable stream as a read-only substream.</para>
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// 创建从当前流位置开始的有界只读流。
    /// <para>Creates a bounded read stream beginning at the current stream position.</para>
    /// </summary>
    /// <param name="inner">可读且可定位的基础流。<para>The readable, seekable underlying stream.</para></param>
    /// <param name="length">范围长度（字节）。<para>The range length, in bytes.</para></param>
    /// <param name="leaveOpen">释放后是否保持基础流打开。<para>Whether to leave the underlying stream open after disposal.</para></param>
    public BoundedReadStream(Stream inner, long length, bool leaveOpen = false)
        : this(inner, inner?.Position ?? throw new ArgumentNullException(nameof(inner)), length, leaveOpen)
    {
    }

    /// <summary>
    /// 创建指定绝对偏移和长度的有界只读流。
    /// <para>Creates a bounded read stream at the specified absolute offset and length.</para>
    /// </summary>
    /// <param name="inner">可读且可定位的基础流。<para>The readable, seekable underlying stream.</para></param>
    /// <param name="offset">基础流中的绝对起始偏移。<para>The absolute starting offset in the underlying stream.</para></param>
    /// <param name="length">范围长度（字节）。<para>The range length, in bytes.</para></param>
    /// <param name="leaveOpen">释放后是否保持基础流打开。<para>Whether to leave the underlying stream open after disposal.</para></param>
    public BoundedReadStream(Stream inner, long offset, long length, bool leaveOpen = false)
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
        _inner.Position = offset;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;
    /// <inheritdoc />
    public override bool CanSeek => !_disposed;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length { get { ThrowIfDisposed(); return _length; } }
    /// <inheritdoc />
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set { Seek(value, SeekOrigin.Begin); }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        ThrowIfDisposed();
        var requested = (int)Math.Min(count, _length - _position);
        if (requested == 0) return 0;
        _inner.Position = checked(_start + _position);
        var read = _inner.Read(buffer, offset, requested);
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested == 0) return 0;
        _inner.Position = checked(_start + _position);
        var read = _inner.Read(buffer[..requested]);
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested == 0) return 0;
        _inner.Position = checked(_start + _position);
        var read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc />
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
        _position = target;
        _inner.Position = checked(_start + target);
        return target;
    }

    /// <inheritdoc />
    public override void Flush() => ThrowIfDisposed();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
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