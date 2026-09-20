using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;
using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;

public sealed class SparseFileRangeProvider : ISparseDataProvider
{
    private readonly SparseFile _owner;
    private readonly long _start;

    /// <summary>
    /// Initializes a new range provider over the owner's logical image.
    /// <para>初始化所有者逻辑镜像之上的新范围提供程序。</para>
    /// </summary>
    public SparseFileRangeProvider(SparseFile owner, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var logicalLength = (long)owner.Header.TotalBlocks * owner.Header.BlockSize;
        if (start < 0 || start > logicalLength)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "The range start must be within the logical image size.");
        }
        if (length < 0 || length > logicalLength - start)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The range must not exceed the logical image size.");
        }

        _owner = owner;
        _start = start;
        Length = length;
    }

    public long Length { get; }

    /// <summary>
    /// Writes the content to the destination stream.
    /// <para>将内容写入目标流。</para>
    /// </summary>
    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(stream));
        }
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = ReadLogical(_start + (Length - remaining), buffer.AsSpan(0, toRead));
                if (read <= 0)
                {
                    throw new EndOfStreamException("Sparse logical range ended before the declared length.");
                }

                stream.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes the content to the destination stream asynchronously.
    /// <para>异步将内容写入目标流。</para>
    /// </summary>
    public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteToAsyncCore(stream, cancellationToken);
    }

    private async Task WriteToAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = Length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = ReadLogical(_start + (Length - remaining), buffer.AsSpan(0, toRead));
                if (read <= 0)
                {
                    throw new EndOfStreamException("Sparse logical range ended before the declared length.");
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, Length - offset);
        return ReadLogical(_start + offset, buffer[..toRead]);
    }

    /// <summary>
    /// Creates a data provider for the specified range.
    /// <para>创建指定范围的数据提供器。</para>
    /// </summary>
    public ISparseDataProvider GetSubProvider(long offset, long length) => new SparseFileRangeProvider(_owner, _start + offset, length);

    /// <summary>
    /// Dispose is a no-op: the owner manages the lifetime of its source streams.
    /// <para>释放为空操作：所有者管理其源流的生命周期。</para>
    /// </summary>
    public void Dispose() { }

    /// <summary>
    /// Fills buffer with logical bytes starting at an absolute logical offset, expanding chunks as needed.
    /// <para>从绝对逻辑偏移开始填充 buffer，按需展开数据块；未覆盖区域（空隙/尾部补齐）零填充。</para>
    /// </summary>
    private int ReadLogical(long absoluteOffset, Span<byte> buffer)
    {
        var blockSize = (long)_owner.Header.BlockSize;
        var requestedStart = absoluteOffset;
        var requestedEnd = checked(absoluteOffset + buffer.Length);
        var written = 0;

        var chunks = _owner.Chunks;
        var startBlock = requestedStart / blockSize;
        int lo = 0, hi = chunks.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var endBlock = (long)chunks[mid].StartBlock + chunks[mid].Header.ChunkSize;
            if (endBlock > startBlock)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        for (var i = lo; i < chunks.Count && written < buffer.Length; i++)
        {
            var chunk = chunks[i];
            var chunkStart = chunk.StartBlock * blockSize;
            var chunkEnd = chunkStart + chunk.Header.ChunkSize * blockSize;
            if (chunkStart >= requestedEnd)
            {
                break;
            }

            var overlapStart = Math.Max(requestedStart, chunkStart);
            var overlapEnd = Math.Min(requestedEnd, chunkEnd);
            if (overlapStart >= overlapEnd)
            {
                continue;
            }

            var destination = buffer[(int)(overlapStart - requestedStart)..(int)(overlapEnd - requestedStart)];
            FillChunk(chunk, overlapStart - chunkStart, destination);
            written += destination.Length;
        }


        if (written < buffer.Length)
        {
            buffer[written..].Clear();
            written = buffer.Length;
        }

        return written;
    }

    private static void FillChunk(SparseChunk chunk, long chunkLocal, Span<byte> destination)
    {
        switch ((ChunkType)chunk.Header.ChunkType)
        {
            case ChunkType.Raw when chunk.DataProvider is not null:
                {
                    var filled = 0;
                    while (filled < destination.Length)
                    {
                        var read = chunk.DataProvider.Read(chunkLocal + filled, destination[filled..]);
                        if (read <= 0)
                        {
                            break;
                        }

                        filled += read;
                    }


                    destination[filled..].Clear();
                    break;
                }

            case ChunkType.Fill:
                {
                    Span<byte> pattern = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(pattern, chunk.FillValue);
                    var offset = (int)(chunkLocal % 4);
                    var i = 0;
                    if (offset != 0)
                    {
                        var head = Math.Min(4 - offset, destination.Length);
                        pattern.Slice(offset, head).CopyTo(destination);
                        i = head;
                    }
                    while (i + 4 <= destination.Length)
                    {
                        pattern.CopyTo(destination.Slice(i));
                        i += 4;
                    }
                    if (i < destination.Length)
                    {
                        pattern[..(destination.Length - i)].CopyTo(destination[i..]);
                    }
                    break;
                }

            default:
                destination.Clear();
                break;
        }
    }
}