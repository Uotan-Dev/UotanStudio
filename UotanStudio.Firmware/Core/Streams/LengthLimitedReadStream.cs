namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Read-only, forward-only view over the next <paramref name="length"/> bytes of the inner stream without seeking it.
/// Reads stop at the declared length; when <paramref name="throwOnEarlyEnd"/> is set, the inner stream ending before the
/// declared length is reached raises <see cref="EndOfStreamException"/> instead of a silent short read. The wrapper
/// disposes the inner stream only when <paramref name="leaveOpen"/> is <see langword="false"/>.
/// <para>对内层流接下来 <paramref name="length"/> 字节的只读、只前进视图，不对其执行定位。读取到声明长度即停止；
/// 设置 <paramref name="throwOnEarlyEnd"/> 时，内层流在到达声明长度前结束会抛出 <see cref="EndOfStreamException"/>，
/// 而不是静默短读。仅当 <paramref name="leaveOpen"/> 为 <see langword="false"/> 时才释放内层流。</para>
/// </summary>
/// <param name="inner">Inner stream read from its current position. <para>从当前位置开始读取的内层流。</para></param>
/// <param name="length">Maximum number of bytes readable through this view. <para>本视图最多可读的字节数。</para></param>
/// <param name="leaveOpen">When <see langword="true"/>, disposing this view leaves <paramref name="inner"/> open.
/// <para>为 <see langword="true"/> 时，释放本视图后 <paramref name="inner"/> 仍保持打开。</para></param>
/// <param name="throwOnEarlyEnd">When <see langword="true"/>, an inner stream that ends before the declared length throws.
/// <para>为 <see langword="true"/> 时，内层流提前结束会抛出异常。</para></param>
internal sealed class LengthLimitedReadStream(Stream inner, long length, bool leaveOpen = false, bool throwOnEarlyEnd = false) : Stream
{
    private long _position;
    private bool _disposed;

    /// <inheritdoc/>
    public override bool CanRead => !_disposed && inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => length;

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.", nameof(buffer));
        }

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)Math.Min(buffer.Length, length - _position);
        if (count == 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..count]);
        if (read == 0 && throwOnEarlyEnd)
        {
            throw new EndOfStreamException($"Source ended after {_position} bytes; {length} bytes were declared.");
        }

        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)Math.Min(buffer.Length, length - _position);
        if (count == 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        if (read == 0 && throwOnEarlyEnd)
        {
            throw new EndOfStreamException($"Source ended after {_position} bytes; {length} bytes were declared.");
        }

        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}