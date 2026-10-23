using System.Buffers;

namespace UotanStudio.Firmware.OtaDelta.Diff;

/// <summary>
/// Growable write-only buffer backed by <see cref="ArrayPool{Byte}"/> so a decoded blob can be materialized
/// without a <see cref="MemoryStream"/> repeatedly copying its managed array.
/// <para>由 <see cref="ArrayPool{Byte}"/> 支撑的可增长只写缓冲，使已解码数据块无需 <see cref="MemoryStream"/>
/// 反复复制托管数组即可物化。</para>
/// </summary>
internal sealed class PooledBufferStream : Stream
{
    private readonly int _maximumCapacity;
    private byte[] _buffer;
    private int _length;
    private bool _disposed;

    /// <summary>Creates a pooled write buffer.
    /// <para>创建池化写缓冲。</para>
    /// </summary>
    /// <param name="initialCapacity">Rented initial capacity in bytes; must be positive.
    /// <para>初始租用容量（字节）；必须为正。</para></param>
    /// <param name="maximumCapacity">Growable upper bound in bytes; must be at least <paramref name="initialCapacity"/>.
    /// <para>可增长上限（字节）；不得小于 <paramref name="initialCapacity"/>。</para></param>
    internal PooledBufferStream(int initialCapacity, int maximumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCapacity, initialCapacity);
        _maximumCapacity = maximumCapacity;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Gets the bytes written so far.
    /// <para>获取迄今已写入的字节。</para>
    /// </summary>
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

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

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        EnsureCapacity(checked(_length + buffer.Length));
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required > _maximumCapacity)
        {
            throw new IOException($"The maximum output byte count of {_maximumCapacity} was exceeded.");
        }

        if (required <= _buffer.Length)
        {
            return;
        }

        var capacity = Math.Min(_maximumCapacity, Math.Max(required, checked(_buffer.Length * 2)));
        var replacement = ArrayPool<byte>.Shared.Rent(capacity);
        _buffer.AsSpan(0, _length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}