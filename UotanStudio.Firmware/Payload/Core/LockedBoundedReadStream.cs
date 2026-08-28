using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Exposes a bounded logical view of a shared seekable stream and locks every positioned read.
/// <para>公开共享可定位流的有界逻辑视图，并对每次定位读取加锁。</para>
/// </summary>
internal sealed class LockedBoundedReadStream : Stream
{
    private readonly Stream _source;
    private readonly object _syncRoot;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// Initializes a bounded view without taking ownership of the shared stream.
    /// <para>初始化有界视图，但不取得共享流的所有权。</para>
    /// </summary>
    /// <param name="source">Shared seekable source stream.<para>共享的可定位源流。</para></param>
    /// <param name="syncRoot">Lock protecting source positioning and reads.<para>保护源流定位和读取的锁。</para></param>
    /// <param name="start">Absolute start offset.<para>绝对起始偏移。</para></param>
    /// <param name="length">Logical view length.<para>逻辑视图长度。</para></param>
    internal LockedBoundedReadStream(Stream source, object syncRoot, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syncRoot);
        if (!source.CanRead || !source.CanSeek || start < 0 || length < 0)
        {
            throw new PartitionExtractionException("Payload stream must be readable and seekable and bounds must be non-negative", string.Empty);
        }

        _source = source;
        _syncRoot = syncRoot;
        _start = start;
        _length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _length;
    /// <inheritdoc />
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min((long)buffer.Length, _length - _position);
        if (count == 0)
        {
            return 0;
        }

        int read;
        lock (_syncRoot)
        {
            _source.Seek(checked(_start + _position), SeekOrigin.Begin);
            read = _source.Read(buffer[..count]);
        }
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}