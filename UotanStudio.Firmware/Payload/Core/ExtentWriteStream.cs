using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Maps a contiguous logical write stream onto the physical ranges described by destination extents.
/// <para>将连续的逻辑写入流映射到目标区间描述的多个物理范围。</para>
/// </summary>
internal sealed class ExtentWriteStream : Stream
{
    private readonly Stream _output;
    private readonly (long Offset, long Length)[] _extents;
    private readonly bool _requireExactLength;
    private readonly object _syncRoot;
    private readonly long _length;
    private int _extentIndex;
    private long _extentPosition;
    private long _position;

    /// <summary>
    /// Initializes an extent-mapped, strictly bounded write stream.
    /// <para>初始化按区间映射且严格有界的写入流。</para>
    /// </summary>
    /// <param name="output">The seekable physical output stream.<para>可定位的物理输出流。</para></param>
    /// <param name="extents">Destination extents in logical order.<para>按逻辑顺序排列的目标区间。</para></param>
    /// <param name="blockSize">Size of one block in bytes.<para>单个块的字节大小。</para></param>
    /// <param name="requireExactLength">Whether <see cref="Complete"/> requires every destination byte to have been written.<para><see cref="Complete"/> 是否要求写满所有目标字节。</para></param>
    /// <param name="syncRoot">Lock used to make physical seek and write atomic.<para>用于保证物理定位与写入原子性的锁。</para></param>
    /// <exception cref="PartitionExtractionException">Extent arithmetic overflows or the output cannot be positioned.<para>区间算术溢出或输出无法定位时抛出。</para></exception>
    internal ExtentWriteStream(Stream output, IEnumerable<Extent> extents, uint blockSize, bool requireExactLength = false, object? syncRoot = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(extents);
        if (!output.CanWrite || !output.CanSeek)
        {
            throw new PartitionExtractionException("Extent output stream must be writable and seekable", string.Empty);
        }

        _output = output;
        _requireExactLength = requireExactLength;
        _syncRoot = syncRoot ?? new object();
        try
        {
            _extents = extents.Select(extent =>
                (checked((long)extent.StartBlock * (long)blockSize),
                 checked((long)extent.NumBlocks * (long)blockSize))).ToArray();
            _length = _extents.Aggregate(0L, (total, extent) => checked(total + extent.Length));
            for (var i = 0; i < _extents.Length; i++)
            {
                var end = checked(_extents[i].Offset + _extents[i].Length);
                for (var j = i + 1; j < _extents.Length; j++)
                {
                    var otherEnd = checked(_extents[j].Offset + _extents[j].Length);
                    if (_extents[i].Length > 0 && _extents[j].Length > 0 &&
                        _extents[i].Offset < otherEnd && _extents[j].Offset < end)
                    {
                        throw new PartitionExtractionException("Destination extents overlap.", string.Empty);
                    }
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new PartitionExtractionException("Destination extent size or offset exceeds Int64 range", string.Empty, ex);
        }
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Verifies that the required logical output length was written.
    /// <para>验证是否已写入所需的逻辑输出长度。</para>
    /// </summary>
    /// <exception cref="PartitionExtractionException">Exact length was required but the output is short.<para>要求精确长度但输出不足时抛出。</para></exception>
    internal void Complete()
    {
        if (_requireExactLength && _position != _length)
        {
            throw new PartitionExtractionException(
                $"Operation output length mismatch: expected {_length} bytes, wrote {_position} bytes",
                string.Empty);
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("Offset and count exceed the buffer length.");
        }

        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > _length - _position)
        {
            throw new PartitionExtractionException(
                $"Operation output exceeds destination extents ({_length} bytes)",
                string.Empty);
        }

        while (!buffer.IsEmpty)
        {
            SkipEmptyExtents();
            var extent = _extents[_extentIndex];
            var count = (int)Math.Min((long)buffer.Length, extent.Length - _extentPosition);
            lock (_syncRoot)
            {
                _output.Seek(checked(extent.Offset + _extentPosition), SeekOrigin.Begin);
                _output.Write(buffer[..count]);
            }
            buffer = buffer[count..];
            _extentPosition = checked(_extentPosition + count);
            _position = checked(_position + count);
        }
    }

    private void SkipEmptyExtents()
    {
        while (_extentIndex < _extents.Length && _extentPosition == _extents[_extentIndex].Length)
        {
            _extentIndex++;
            _extentPosition = 0;
        }
    }

    /// <inheritdoc />
    public override void Flush() => _output.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}