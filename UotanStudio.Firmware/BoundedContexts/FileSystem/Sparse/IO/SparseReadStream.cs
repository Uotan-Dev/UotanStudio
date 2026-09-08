using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public sealed class SparseReadStream : Stream
{
    private readonly SparseFile _sparseFile;
    private readonly object _lock = new();
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new SparseReadStream over the given sparse file.
    /// <para>在给定稀疏文件之上初始化新的 SparseReadStream。</para>
    /// </summary>
    public SparseReadStream(SparseFile sparseFile)
    {
        ArgumentNullException.ThrowIfNull(sparseFile);
        _sparseFile = sparseFile;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => (long)_sparseFile.Header.TotalBlocks * _sparseFile.Header.BlockSize;

    public override long Position
    {
        get
        {
            lock (_lock)
            {
                return _position;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_lock)
            {
                _position = value;
            }
        }
    }

    /// <summary>
    /// Reads up to buffer.Length bytes starting at the current position and advances the position by the number of bytes actually read.
    /// <para>从当前位置读取最多 buffer.Length 个字节，并将位置前进实际读取的字节数。</para>
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_position >= Length)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, Length - _position);
            var read = ReadLogical(_position, buffer[..toRead]);
            _position += read;
            return read;
        }
    }

    /// <summary>
    /// Reads up to count bytes starting at the current position and advances the position by the number of bytes actually read.
    /// <para>从当前位置读取最多 count 个字节，并将位置前进实际读取的字节数。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Sets the position within the logical image relative to the specified origin.
    /// <para>设置逻辑镜像中相对于指定参考点的位置。</para>
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin is < SeekOrigin.Begin or > SeekOrigin.End)
        {
            throw new ArgumentException("Invalid seek origin.", nameof(origin));
        }

        lock (_lock)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                _ => checked(Length + offset),
            };
            if (target < 0)
            {
                throw new IOException("An attempt was made to move the position before the beginning of the stream.");
            }

            _position = target;
            return target;
        }
    }

    /// <summary>
    /// Throws NotSupportedException because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void Flush() => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Throws NotSupportedException because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Throws NotSupportedException because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void SetLength(long value) => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Releases the owned SparseFile.
    /// <para>释放所拥有的 SparseFile。</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _sparseFile.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Fills buffer with logical bytes starting at an absolute logical offset, expanding chunks as needed.
    /// <para>从绝对逻辑偏移开始填充 buffer，按需展开数据块；未被任何数据块覆盖的区域 （空隙、DontCare / Crc32 / 未知块、尾部补齐）以零填充。</para>
    /// </summary>
    private int ReadLogical(long absoluteOffset, Span<byte> buffer)
    {
        var blockSize = (long)_sparseFile.Header.BlockSize;
        var chunks = _sparseFile.Chunks;
        var requestedStart = absoluteOffset;
        var requestedEnd = checked(absoluteOffset + buffer.Length);
        var written = 0;
        var cursor = requestedStart;


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


            if (cursor < chunkStart)
            {
                var gap = (int)(Math.Min(chunkStart, requestedEnd) - cursor);
                if (gap > 0)
                {
                    buffer.Slice(written, gap).Clear();
                    written += gap;
                    cursor += gap;
                }
            }

            var overlapStart = Math.Max(cursor, chunkStart);
            var overlapEnd = Math.Min(requestedEnd, chunkEnd);
            if (overlapStart < overlapEnd)
            {
                var count = (int)(overlapEnd - overlapStart);
                FillChunk(chunk, overlapStart - chunkStart, buffer.Slice(written, count));
                written += count;
                cursor = overlapEnd;
            }
        }


        if (written < buffer.Length)
        {
            buffer[written..].Clear();
            written = buffer.Length;
        }

        return written;
    }

    /// <summary>
    /// Fills destination from a single chunk, starting at the chunk-relative offset chunkLocal.
    /// <para>从单个数据块填充 destination，从块内相对偏移 chunkLocal 开始。</para>
    /// </summary>
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
                    // 按 4 字节对齐段复制替代逐字节取模写。
                    // Copy in 4-byte aligned segments instead of per-byte modulo writes.
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
