using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BlockImage.Sparse.IO;

/// <summary>
/// Read-only, seekable view presenting a <see cref="SparseFile"/> as its fully expanded logical byte stream; holes, don't-care and unknown chunk types read back as zeros. The stream takes ownership of the model and disposes it with itself, and reads are serialized under an internal lock.
/// <para>只读、可寻址的视图，将 <see cref="SparseFile"/> 呈现为完全展开的逻辑字节流；空隙、don't-care 与未知块类型按零读取。流接管模型所有权并随自身一同释放，读取在内部锁下串行执行。</para>
/// </summary>
public sealed class SparseReadStream : Stream
{
    private readonly SparseFile _sparseFile;
    private readonly Lock _lock = new();
    private long _position;
    private bool _disposed;
    private readonly bool _requireExactRawData;

    /// <summary>
    /// Initializes a new SparseReadStream over the given sparse file.
    /// <para>在给定稀疏文件之上初始化新的 SparseReadStream。</para>
    /// </summary>
    /// <param name="sparseFile">Model to expose; ownership transfers to the stream and truncated RAW payloads read as zeros in the missing range.
    /// <para>要公开的模型；所有权转移给流，被截断的 RAW 载荷在缺失范围内按零读取。</para></param>
    public SparseReadStream(SparseFile sparseFile)
        : this(sparseFile, false)
    {
    }

    /// <summary>Creates a logical reader with optional rejection of truncated RAW backing data.
    /// <para>创建逻辑读取器，可选拒绝被截断的 RAW 支撑数据。</para></summary>
    /// <param name="sparseFile">Model to expose; ownership transfers to the stream.
    /// <para>要公开的模型；所有权转移给流。</para></param>
    /// <param name="requireExactRawData">Reject missing RAW bytes instead of padding partial blocks with zeros.
    /// <para>拒绝缺失的 RAW 字节，而不是为不完整的块补零。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="sparseFile"/> is <see langword="null"/>.
    /// <para><paramref name="sparseFile"/> 为 <see langword="null"/>。</para></exception>
    public SparseReadStream(SparseFile sparseFile, bool requireExactRawData)
    {
        ArgumentNullException.ThrowIfNull(sparseFile);
        _sparseFile = sparseFile;
        _requireExactRawData = requireExactRawData;
    }

    /// <summary>Always <c>true</c>: the view expands the sparse model into a logical byte stream.
    /// <para>恒为 <c>true</c>：该视图将稀疏模型展开为逻辑字节流。</para></summary>
    /// <value><c>true</c>. <para>始终为 <c>true</c>。</para></value>
    public override bool CanRead => true;

    /// <summary>Always <c>true</c>: reads address the logical image by absolute offset.
    /// <para>恒为 <c>true</c>：读取按绝对偏移寻址逻辑镜像。</para></summary>
    /// <value><c>true</c>. <para>始终为 <c>true</c>。</para></value>
    public override bool CanSeek => true;

    /// <summary>Always <c>false</c>: the stream exposes expanded image bytes and accepts no writes.
    /// <para>恒为 <c>false</c>：该流仅公开展开后的镜像字节，不接受写入。</para></summary>
    /// <value><c>false</c>. <para>始终为 <c>false</c>。</para></value>
    public override bool CanWrite => false;

    /// <summary>Logical size of the fully expanded image: the header's total block count times the block size.
    /// <para>完全展开后镜像的逻辑大小：头部总块数乘以块大小。</para></summary>
    /// <value>The full byte length of the logical stream, including holes and don't-care regions.
    /// <para>逻辑流的完整字节长度，包含空隙与 don't-care 区域。</para></value>
    public override long Length => (long)_sparseFile.Header.TotalBlocks * _sparseFile.Header.BlockSize;

    /// <summary>The current offset within the logical image, read and written under the internal lock.
    /// <para>逻辑镜像中的当前偏移，读写均在内部锁下进行。</para></summary>
    /// <value>Byte offset from the start of the logical stream. Setting rejects negative values but
    /// accepts offsets past the end, where subsequent reads return <c>0</c>.
    /// <para>距逻辑流起点的字节偏移。设置时拒绝负数，但允许超出末尾，此时后续读取返回 <c>0</c>。</para></value>
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
    /// <param name="buffer">Destination span to fill; an empty span reads nothing and leaves the position unchanged.
    /// <para>要填充的目标区间；空区间不读取任何字节，位置保持不变。</para></param>
    /// <returns>The number of bytes copied into <paramref name="buffer"/>, <c>0</c> once the position reaches the end of the logical image.
    /// <para>复制到 <paramref name="buffer"/> 的字节数；位置到达逻辑镜像末尾时为 <c>0</c>。</para></returns>
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
    /// <param name="buffer">Destination array receiving the logical bytes. <para>接收逻辑字节的目标数组。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where the copied bytes start.
    /// <para><paramref name="buffer"/> 中复制字节的起始索引。</para></param>
    /// <param name="count">Maximum number of bytes to read; holes and don't-care regions yield zeros.
    /// <para>最多读取的字节数；空隙与 don't-care 区域返回零。</para></param>
    /// <returns>The number of bytes written into <paramref name="buffer"/>, <c>0</c> once the position reaches the end of the logical image.
    /// <para>写入 <paramref name="buffer"/> 的字节数；位置到达逻辑镜像末尾时为 <c>0</c>。</para></returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Sets the position within the logical image relative to the specified origin.
    /// <para>设置逻辑镜像中相对于指定参考点的位置。</para>
    /// </summary>
    /// <param name="offset">Byte displacement added to the origin; negative values are accepted for
    /// <see cref="SeekOrigin.Current"/> and <see cref="SeekOrigin.End"/>, the addition rejects overflow,
    /// and a target before the start of the stream is rejected.
    /// <para>叠加在参考点上的字节位移；<see cref="SeekOrigin.Current"/> 与 <see cref="SeekOrigin.End"/>
    /// 允许负值，相加时拒绝溢出，落在流起点之前的目标会被拒绝。</para></param>
    /// <param name="origin">Reference point: <see cref="SeekOrigin.Begin"/> measures from the start of the
    /// logical stream, <see cref="SeekOrigin.Current"/> from the current position and
    /// <see cref="SeekOrigin.End"/> from <see cref="Length"/>; only these three values are accepted.
    /// <para>参考点：<see cref="SeekOrigin.Begin"/> 从逻辑流起点计算，<see cref="SeekOrigin.Current"/>
    /// 从当前位置计算，<see cref="SeekOrigin.End"/> 从 <see cref="Length"/> 计算；仅接受这三个值。</para></param>
    /// <returns>The new absolute position, which may exceed <see cref="Length"/>; reads at such a
    /// position return <c>0</c> bytes.
    /// <para>新的绝对位置，可以超出 <see cref="Length"/>；在该位置读取返回 <c>0</c> 字节。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not one of the three supported
    /// seek origins. <para><paramref name="origin"/> 不是三个受支持的寻址参考点之一。</para></exception>
    /// <exception cref="IOException">The computed target position lies before the beginning of the stream.
    /// <para>计算出的目标位置位于流起点之前。</para></exception>
    /// <exception cref="OverflowException">Adding <paramref name="offset"/> to the current position or to
    /// <see cref="Length"/> overflows <see cref="long"/>.
    /// <para><paramref name="offset"/> 与当前位置或 <see cref="Length"/> 相加时溢出 <see cref="long"/>。</para></exception>
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
    /// <param name="buffer">Ignored; no bytes are read from it. <para>被忽略；不会从中读取字节。</para></param>
    /// <param name="offset">Ignored index into <paramref name="buffer"/>. <para>被忽略的 <paramref name="buffer"/> 内索引。</para></param>
    /// <param name="count">Ignored byte count. <para>被忽略的字节数量。</para></param>
    /// <exception cref="NotSupportedException">Always thrown: the stream exposes expanded image bytes and accepts no writes.
    /// <para>总是抛出：该流仅公开展开后的镜像字节，不接受写入。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("SparseReadStream is read-only.");

    /// <summary>
    /// Throws NotSupportedException because the stream is read-only.
    /// <para>因为流是只读的而抛出 NotSupportedException。</para>
    /// </summary>
    /// <param name="value">Ignored length; the logical size always comes from the sparse header.
    /// <para>被忽略的长度；逻辑大小始终取自稀疏头部。</para></param>
    /// <exception cref="NotSupportedException">Always thrown because the logical size cannot be changed.
    /// <para>总是抛出，因为逻辑大小无法更改。</para></exception>
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
    private void FillChunk(SparseChunk chunk, long chunkLocal, Span<byte> destination)
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
                            if (_requireExactRawData)
                            {
                                throw new EndOfStreamException("Sparse RAW backing data ended before the declared range.");
                            }

                            break;
                        }
                        filled += read;
                    }


                    destination[filled..].Clear();
                    break;
                }

            case ChunkType.Fill:
                {
                    SparseFill.Fill(destination, chunk.FillValue, (int)(chunkLocal % 4));
                    break;
                }

            default:
                destination.Clear();
                break;
        }
    }
}