using System.Buffers.Binary;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Provides a read-only, seekable random-access stream over the logical (unsparsified)
/// image of a <see cref="SparseFile"/>. Reads are served lazily from the sparse chunks,
/// expanding Fill / DontCare chunks on demand without ever materializing the whole image.
/// <para>以只读、可寻址的随机访问流形式暴露 SparseFile 的逻辑（非稀疏）镜像。
/// 惰性读取稀疏数据块，按需展开 Fill / DontCare 块，绝不物化整张镜像。</para>
/// </summary>
/// <remarks>
/// Creates a new <see cref="SparseReadStream"/> that takes ownership of the supplied
/// <see cref="SparseFile"/> and releases it when the stream is disposed
/// (<see cref="SparseFile.Dispose"/> is idempotent).
/// <para>创建新的 SparseReadStream，取得传入 SparseFile 的所有权，并在流被释放时释放它
/// （SparseFile.Dispose 是幂等的）。</para>
/// </remarks>
public sealed class SparseReadStream : Stream
{
    private readonly SparseFile _sparseFile;
    private readonly object _lock = new();
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="SparseReadStream"/> over the given sparse file.
    /// <para>在给定稀疏文件之上初始化新的 SparseReadStream。</para>
    /// </summary>
    /// <param name="sparseFile">The sparse file whose logical image is exposed.
    /// <para>要暴露逻辑镜像的稀疏文件。</para></param>
    public SparseReadStream(SparseFile sparseFile)
    {
        ArgumentNullException.ThrowIfNull(sparseFile);
        _sparseFile = sparseFile;
    }

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading. Always true.
    /// <para>获取指示当前流是否支持读取的值。始终为 true。</para>
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Gets a value indicating whether the current stream supports seeking. Always true.
    /// <para>获取指示当前流是否支持查找的值。始终为 true。</para>
    /// </summary>
    public override bool CanSeek => true;

    /// <summary>
    /// Gets a value indicating whether the current stream supports writing. Always false.
    /// <para>获取指示当前流是否支持写入的值。始终为 false。</para>
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the length of the logical (unsparsified) image in bytes.
    /// <para>获取逻辑（非稀疏）镜像的字节长度。</para>
    /// </summary>
    public override long Length => (long)_sparseFile.Header.TotalBlocks * _sparseFile.Header.BlockSize;

    /// <summary>
    /// Gets or sets the current position within the logical image.
    /// <para>获取或设置逻辑镜像中的当前位置。</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a negative position is assigned.
    /// <para>当赋值为负数时抛出。</para></exception>
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
    /// Reads up to <paramref name="buffer"/>.Length bytes starting at the current position
    /// and advances the position by the number of bytes actually read. The read may span
    /// multiple chunks; regions without backing data (DontCare / Crc32 / unknown chunks,
    /// gaps, and trailing padding) are returned as zeros.
    /// <para>从当前位置读取最多 buffer.Length 个字节，并将位置前进实际读取的字节数。
    /// 读取可能跨多个数据块；无底层数据的区域（DontCare / Crc32 / 未知块、空隙和尾部补齐）
    /// 以零填充返回。</para>
    /// </summary>
    /// <param name="buffer">The buffer to receive the bytes. <para>接收字节的缓冲区。</para></param>
    /// <returns>The total number of bytes read, or 0 when the end of the logical image is reached.
    /// <para>实际读取的总字节数；到达逻辑镜像末尾时返回 0。</para></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the stream has been disposed.
    /// <para>当流已被释放时抛出。</para></exception>
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
    /// Reads up to <paramref name="count"/> bytes starting at the current position and
    /// advances the position by the number of bytes actually read. The read may span
    /// multiple chunks; regions without backing data are returned as zeros.
    /// <para>从当前位置读取最多 count 个字节，并将位置前进实际读取的字节数。
    /// 读取可能跨多个数据块；无底层数据的区域以零填充返回。</para>
    /// </summary>
    /// <param name="buffer">The buffer to receive the bytes. <para>接收字节的缓冲区。</para></param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to begin writing.
    /// <para>缓冲区中开始写入的从零开始的字节偏移。</para></param>
    /// <param name="count">The maximum number of bytes to read. <para>要读取的最大字节数。</para></param>
    /// <returns>The total number of bytes read, or 0 when the end of the logical image is reached.
    /// <para>实际读取的总字节数；到达逻辑镜像末尾时返回 0。</para></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the stream has been disposed.
    /// <para>当流已被释放时抛出。</para></exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Sets the position within the logical image relative to the specified origin.
    /// <para>设置逻辑镜像中相对于指定参考点的位置。</para>
    /// </summary>
    /// <param name="offset">A byte offset relative to <paramref name="origin"/>.
    /// <para>相对于 origin 的字节偏移。</para></param>
    /// <param name="origin">The reference point used to obtain the new position.
    /// <para>用于获得新位置的参考点。</para></param>
    /// <returns>The new position within the logical image. <para>逻辑镜像中的新位置。</para></returns>
    /// <exception cref="IOException">Thrown when the new position would fall before the start
    /// of the logical image. <para>当新位置落在逻辑镜像起点之前时抛出。</para></exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="origin"/> is invalid.
    /// <para>当 origin 无效时抛出。</para></exception>
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
    /// Throws <see cref="NotSupportedException"/> because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void Flush() => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    public override void SetLength(long value) => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Releases the owned <see cref="SparseFile"/>. Safe to call multiple times.
    /// <para>释放所拥有的 SparseFile。可安全地多次调用。</para>
    /// </summary>
    /// <param name="disposing">True to release managed resources. <para>是否释放托管资源。</para></param>
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
    /// Fills <paramref name="buffer"/> with logical bytes starting at an absolute logical
    /// offset, expanding chunks as needed. Regions not covered by any chunk (gaps,
    /// DontCare / Crc32 / unknown chunks, trailing padding) are zero-filled.
    /// <para>从绝对逻辑偏移开始填充 buffer，按需展开数据块；未被任何数据块覆盖的区域
    /// （空隙、DontCare / Crc32 / 未知块、尾部补齐）以零填充。</para>
    /// </summary>
    private int ReadLogical(long absoluteOffset, Span<byte> buffer)
    {
        var blockSize = (long)_sparseFile.Header.BlockSize;
        var chunks = _sparseFile.Chunks;
        var requestedStart = absoluteOffset;
        var requestedEnd = checked(absoluteOffset + buffer.Length);
        var written = 0;
        var cursor = requestedStart;

        // 二分查找第一个可能在 requestedStart 处重叠的块（Chunks 按 StartBlock 升序）。
        // Binary-search the first chunk that may overlap requestedStart (Chunks are ordered
        // by StartBlock ascending).
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
            var chunkStart = (long)chunk.StartBlock * blockSize;
            var chunkEnd = chunkStart + (long)chunk.Header.ChunkSize * blockSize;

            if (chunkStart >= requestedEnd)
            {
                break;
            }

            // 块与块之间的空隙（gap）以零填充，保持逻辑偏移连续。
            // Zero-fill any gap between the current cursor and this chunk's start.
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

        // 最后一块之后（含镜像尾部）未被任何块覆盖的区域一律零填充。
        // Regions after the last chunk (the image tail included) are zero.
        if (written < buffer.Length)
        {
            buffer[written..].Clear();
            written = buffer.Length;
        }

        return written;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from a single chunk, starting at the chunk-relative
    /// offset <paramref name="chunkLocal"/>. Raw chunks read through their data provider;
    /// Fill chunks expand the 4-byte pattern; every other chunk type is zero-filled.
    /// <para>从单个数据块填充 destination，从块内相对偏移 chunkLocal 开始。Raw 块经数据
    /// 提供器读取；Fill 块展开 4 字节模式；其余任何块类型一律零填充。</para>
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

                    // 提供器可能短于块的逻辑跨度；剩余部分零填充。
                    // The provider may be shorter than the chunk's logical span; zero-pad the rest.
                    destination[filled..].Clear();
                    break;
                }

            case ChunkType.Fill:
                {
                    Span<byte> pattern = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(pattern, chunk.FillValue);
                    var offset = (int)(chunkLocal % 4);
                    for (var i = 0; i < destination.Length; i++)
                    {
                        destination[i] = pattern[(offset + i) % 4];
                    }
                    break;
                }

            default:
                destination.Clear();
                break;
        }
    }
}
