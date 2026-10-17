using System.Buffers;

namespace UotanStudio.Compress.Internal;

/// <summary>
/// Write-only stream over a pooled buffer whose capacity grows on demand and prefers the caller's soft limit over
/// a plain doubling, so a chunk encoder can write its result without overshooting the useful bound.
/// <para>基于池化缓冲的只写流；容量按需增长，并优先停在调用方给出的软上限而不是继续倍增，使块编码器写出结果时
/// 不会越过有用上界。</para>
/// </summary>
internal sealed class PooledWriteStream : Stream
{
    private const int InitialCapacity = 16 * 1024;

    private readonly int _softLimit;
    private byte[] _buffer;
    private int _length;

    /// <summary>
    /// Initializes a stream holding a pooled buffer sized for short outputs.
    /// <para>初始化持有池化缓冲的流，缓冲初始容量按短输出设定。</para>
    /// </summary>
    /// <param name="softLimit">Capacity the growth policy aims at once the initial buffer is exceeded; a limit
    /// below the initial capacity is raised to it, and a write that needs more still grows past it.
    /// <para>初始缓冲被超越后增长策略瞄准的容量；小于初始容量时提升到初始容量，写入需要更多空间时仍会越过它增长。</para>
    /// </param>
    internal PooledWriteStream(int softLimit)
    {
        _softLimit = Math.Max(softLimit, InitialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    }

    /// <summary>
    /// Gets the buffer holding the bytes written so far; only the leading <see cref="WrittenLength"/> bytes are live.
    /// <para>获取保存已写字节的缓冲区；只有前 <see cref="WrittenLength"/> 字节有效。</para>
    /// </summary>
    /// <value>The rented buffer, which the caller must not retain past this stream's lifetime.
    /// <para>借用的缓冲区，调用方不得在本流生命周期结束后继续持有。</para></value>
    internal byte[] Buffer => _buffer;

    /// <summary>
    /// Gets the number of bytes written to the stream.
    /// <para>获取写入该流的字节数。</para>
    /// </summary>
    /// <value>A non-negative byte count.
    /// <para>非负的字节数。</para></value>
    internal int WrittenLength => _length;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        base.Dispose(disposing);
    }

    private void EnsureCapacity(int additional)
    {
        if (additional > int.MaxValue - _length)
        {
            throw new InvalidDataException("The chunk output exceeds the supported size. 块输出超出受支持的大小。");
        }

        var required = _length + additional;
        if (required <= _buffer.Length)
        {
            return;
        }

        var target = Math.Max(required, Math.Min(_softLimit, _buffer.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(target);
        _buffer.AsSpan(0, _length).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
}