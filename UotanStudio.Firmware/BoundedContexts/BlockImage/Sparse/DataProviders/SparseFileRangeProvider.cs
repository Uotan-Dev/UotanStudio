using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;

/// <summary>
/// Read-only window over the expanded (logical) byte range of a parsed sparse image. Bytes are
/// produced by replaying the owner's chunk list, so holes between chunks and the tail of a
/// <c>DONT_CARE</c> region read as zeros without ever materialising the whole image.
/// <para>已解析稀疏镜像展开后（逻辑）字节范围的只读窗口。字节通过重放所有者的数据块列表生成，
/// 因此数据块之间的空隙与 <c>DONT_CARE</c> 区域的尾部都会按零读取，而不会把整张镜像实体化。</para>
/// </summary>
public sealed class SparseFileRangeProvider : ISparseDataProvider
{
    private readonly SparseFile _owner;
    private readonly long _start;

    /// <summary>
    /// Initializes a new range provider over the owner's logical image.
    /// <para>初始化所有者逻辑镜像之上的新范围提供程序。</para>
    /// </summary>
    /// <param name="owner">
    /// Parsed sparse file supplying the header and chunk list; its lifetime is managed by the caller
    /// and it must stay undisposed while this provider is used.
    /// <para>提供头部与数据块列表的已解析稀疏文件；其生命周期由调用方管理，本提供程序使用期间不得释放。</para>
    /// </param>
    /// <param name="start">
    /// Absolute logical offset where the window begins.
    /// <para>窗口开始的绝对逻辑偏移。</para>
    /// </param>
    /// <param name="length">
    /// Byte length of the window within the logical image.
    /// <para>窗口在逻辑镜像内的字节长度。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="owner"/> is <see langword="null"/>.
    /// <para><paramref name="owner"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> lies outside the logical image, or <paramref name="length"/> is
    /// negative or runs past its end.
    /// <para><paramref name="start"/> 落在逻辑镜像之外，或 <paramref name="length"/> 为负、越过逻辑镜像末尾。</para>
    /// </exception>
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

    /// <summary>
    /// Byte length of the logical window.
    /// <para>逻辑窗口的字节长度。</para>
    /// </summary>
    /// <value>
    /// The validated length given at construction; a length of <c>0</c> makes every read return
    /// without touching the chunk list.
    /// <para>构造时经过校验的长度；长度为 <c>0</c> 时所有读取都会直接返回而不触及数据块列表。</para>
    /// </value>
    public long Length { get; }

    /// <summary>
    /// Writes the content to the destination stream.
    /// <para>将内容写入目标流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream that receives exactly <see cref="Length"/> logical bytes, including
    /// the zero-filled gaps between chunks.
    /// <para>可写目标流，接收恰好 <see cref="Length"/> 个逻辑字节，包含数据块之间以零填充的空隙。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="stream"/> does not accept writes.
    /// <para><paramref name="stream"/> 不接受写入。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The logical range produced no bytes before the declared length was reached.
    /// <para>在达到声明长度之前逻辑范围未产生任何字节。</para>
    /// </exception>
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
    /// <param name="stream">
    /// Writable destination stream that receives exactly <see cref="Length"/> logical bytes.
    /// <para>可写目标流，接收恰好 <see cref="Length"/> 个逻辑字节。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before every block expansion and destination write; the argument check for an
    /// already-cancelled token happens synchronously in this call.
    /// <para>在每次展开数据块与写入目标前检查；对已取消令牌的参数校验在本调用内同步完成。</para>
    /// </param>
    /// <returns>
    /// Task that completes once the whole window has been written.
    /// <para>在整个窗口写入完成后结束的 Task。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled; output written before cancellation is kept.
    /// <para><paramref name="cancellationToken"/> 被取消；取消前已写出的内容会被保留。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The logical range produced no bytes before the declared length was reached.
    /// <para>在达到声明长度之前逻辑范围未产生任何字节。</para>
    /// </exception>
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
    /// <param name="offset">
    /// Offset inside this window; a negative value reads nothing.
    /// <para>本窗口内的偏移；负值不读取任何数据。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination array receiving the bytes.
    /// <para>接收字节的目标数组。</para>
    /// </param>
    /// <param name="bufferOffset">
    /// Index in <paramref name="buffer"/> where writing starts.
    /// <para>开始写入的 <paramref name="buffer"/> 索引。</para>
    /// </param>
    /// <param name="count">
    /// Maximum number of bytes requested.
    /// <para>请求的最大字节数。</para>
    /// </param>
    /// <returns>
    /// Bytes copied, truncated at the window end; uncovered logical space arrives as zeros.
    /// <para>实际复制的字节数，到达窗口末尾时会被截断；未覆盖的逻辑空间以零补齐。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bufferOffset"/> or <paramref name="count"/> falls outside <paramref name="buffer"/>.
    /// <para><paramref name="bufferOffset"/> 或 <paramref name="count"/> 超出 <paramref name="buffer"/> 范围。</para>
    /// </exception>
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <summary>
    /// Reads bytes from the stream into the buffer.
    /// <para>从流中读取字节到缓冲区。</para>
    /// </summary>
    /// <param name="offset">
    /// Offset inside this window; values below <c>0</c> or at or beyond <see cref="Length"/> read nothing.
    /// <para>本窗口内的偏移；小于 <c>0</c>、等于或超过 <see cref="Length"/> 时不读取任何数据。</para>
    /// </param>
    /// <param name="buffer">
    /// Destination span whose length is the maximum wanted.
    /// <para>长度即所需最大字节数的目标 Span。</para>
    /// </param>
    /// <returns>
    /// Always the number of bytes requested, because the span is filled completely and trailing
    /// logical space is zeroed; <c>0</c> when the request starts outside the window or the span is empty.
    /// <para>由于目标会被完整填充并把剩余逻辑空间置零，返回值总等于请求的字节数；
    /// 当请求起点落在窗口之外或目标为空时返回 <c>0</c>。</para>
    /// </returns>
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
    /// <param name="offset">
    /// Offset inside this window where the child window starts.
    /// <para>子窗口在本窗口内的起始偏移。</para>
    /// </param>
    /// <param name="length">
    /// Byte length of the child window.
    /// <para>子窗口的字节长度。</para>
    /// </param>
    /// <returns>
    /// Provider over the same owner whose window is anchored at this window's start plus
    /// <paramref name="offset"/>; it keeps no handle of its own.
    /// <para>基于同一所有者的提供程序，其窗口起点为本窗口起点加上 <paramref name="offset"/>；它不持有任何句柄。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The resulting range starts outside the owner's logical image or extends past its end.
    /// <para>所得范围的起点落在所有者逻辑镜像之外，或越过其末尾。</para>
    /// </exception>
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