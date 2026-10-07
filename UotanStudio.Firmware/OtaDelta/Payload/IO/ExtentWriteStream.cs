using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.IO;

internal sealed class ExtentWriteStream : Stream
{
    private readonly Stream _output;
    private readonly (long Offset, long Length)[] _extents;
    private readonly bool _requireExactLength;
    private readonly object _syncRoot;
    private readonly long _length;
    private readonly PartitionByteProgress? _byteProgress;
    private int _extentIndex;
    private long _extentPosition;
    private long _position;

    /// <summary>
    /// Initializes an extent-mapped, strictly bounded write stream.
    /// <para>初始化按区间映射且严格有界的写入流。</para>
    /// </summary>
    internal ExtentWriteStream(Stream output, IEnumerable<Extent> extents, uint blockSize, bool requireExactLength = false, object? syncRoot = null, PartitionByteProgress? byteProgress = null)
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
        _byteProgress = byteProgress;
        try
        {
            _extents = [.. extents.Select(extent =>
                (checked((long)extent.StartBlock * blockSize),
                 checked((long)extent.NumBlocks * blockSize)))];
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

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Verifies that the required logical output length was written.
    /// <para>验证是否已写入所需的逻辑输出长度。</para>
    /// </summary>
    internal void Complete()
    {
        if (_requireExactLength && _position != _length)
        {
            throw new PartitionExtractionException(
                $"Operation output length mismatch: expected {_length} bytes, wrote {_position} bytes",
                string.Empty);
        }
    }

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    /// <param name="buffer">Source array whose bytes are routed into the mapped extents.
    /// <para>其字节被路由进映射区间的源数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> of the first byte written.
    /// <para><paramref name="buffer"/> 中首个写入字节的索引。</para></param>
    /// <param name="count">Number of bytes written; the total accepted never passes the summed extent
    /// length. <para>写入的字节数；累计接受量不会超出各区间长度之和。</para></param>
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

    /// <summary>
    /// Writes bytes to the stream.
    /// <para>向流写入字节。</para>
    /// </summary>
    /// <param name="buffer">Source span copied into the current extent and continued into the next one
    /// when the span crosses the extent end; an empty span writes nothing.
    /// <para>复制到当前区间的源区间，跨越区间末尾时继续写入下一个区间；空区间不写入任何字节。</para></param>
    /// <exception cref="PartitionExtractionException">The span would pass the summed extent length.
    /// <para>该区间跨度会超出各区间长度之和。</para></exception>
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
            var count = (int)Math.Min(buffer.Length, extent.Length - _extentPosition);
            lock (_syncRoot)
            {
                _output.Seek(checked(extent.Offset + _extentPosition), SeekOrigin.Begin);
                _output.Write(buffer[..count]);
            }
            _byteProgress?.Add(count);
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

    /// <summary>
    /// Flushes buffered data to the underlying stream.
    /// <para>将缓冲数据刷新到底层流。</para>
    /// </summary>
    public override void Flush() => _output.Flush();

    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="buffer">Array never touched, since the stream only writes into the destination extents.
    /// <para>从不接触的数组，因为该流只向目标区段写入。</para></param>
    /// <param name="offset">Index ignored together with the call. <para>随调用一并被忽略的索引。</para></param>
    /// <param name="count">Byte count ignored together with the call. <para>随调用一并被忽略的字节数。</para></param>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Throws because this operation is not supported.
    /// <para>此操作不受支持，调用时抛出异常。</para>
    /// </summary>
    /// <param name="value">Requested length ignored; the writable size always comes from the extent list.
    /// <para>被忽略的请求长度；可写大小始终由区段列表决定。</para></param>
    public override void SetLength(long value) => throw new NotSupportedException();
}