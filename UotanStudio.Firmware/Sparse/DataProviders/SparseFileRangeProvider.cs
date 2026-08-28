using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Provides access to a byte range of a sparse file's logical (unsparsified) image
/// as an <see cref="ISparseDataProvider"/>. Reads lazily from the owning
/// <see cref="SparseFile"/>'s chunks, expanding FILL / DONT_CARE chunks on demand
/// without ever materializing the whole image.
/// <para>以 ISparseDataProvider 提供稀疏文件逻辑（非稀疏）镜像的字节范围访问。
/// 惰性读取所属 SparseFile 的数据块，按需展开 FILL / DONT_CARE 块，无需物化整张镜像。</para>
/// </summary>
/// <remarks>
/// Creates a new <see cref="SparseFileRangeProvider"/> over the logical byte range
/// [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>).
/// Disposing the provider does not dispose the owner; the owner manages its own source streams.
/// <para>在逻辑字节范围 [start, start+length) 上创建新的 SparseFileRangeProvider。
/// 释放该提供程序不会释放所有者；所有者自行管理其源流。</para>
/// </remarks>
public sealed class SparseFileRangeProvider : ISparseDataProvider
{
    private readonly SparseFile _owner;
    private readonly long _start;

    /// <summary>
    /// Initializes a new range provider over the owner's logical image.
    /// <para>初始化所有者逻辑镜像之上的新范围提供程序。</para>
    /// </summary>
    /// <param name="owner">The sparse file whose logical image is exposed. <para>要暴露逻辑镜像的稀疏文件。</para></param>
    /// <param name="start">Byte offset in the logical image where the range starts. <para>逻辑镜像中范围起始的字节偏移。</para></param>
    /// <param name="length">Number of logical bytes exposed by this provider. <para>此提供程序暴露的逻辑字节数。</para></param>
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

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public int Read(long offset, byte[] buffer, int bufferOffset, int count) => Read(offset, buffer.AsSpan(bufferOffset, count));

    /// <inheritdoc />
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, Length - offset);
        return ReadLogical(_start + offset, buffer[..toRead]);
    }

    /// <inheritdoc />
    public ISparseDataProvider GetSubProvider(long offset, long length) => new SparseFileRangeProvider(_owner, _start + offset, length);

    /// <summary>
    /// Dispose is a no-op: the owner manages the lifetime of its source streams.
    /// <para>释放为空操作：所有者管理其源流的生命周期。</para>
    /// </summary>
    public void Dispose() { }

    /// <summary>
    /// Fills <paramref name="buffer"/> with logical bytes starting at an absolute logical offset,
    /// expanding chunks as needed. Any uncovered region (gaps / trailing padding) is zero-filled.
    /// <para>从绝对逻辑偏移开始填充 buffer，按需展开数据块；未覆盖区域（空隙/尾部补齐）零填充。</para>
    /// </summary>
    private int ReadLogical(long absoluteOffset, Span<byte> buffer)
    {
        var blockSize = (long)_owner.Header.BlockSize;
        var requestedStart = absoluteOffset;
        var requestedEnd = checked(absoluteOffset + buffer.Length);
        var written = 0;

        foreach (var chunk in _owner.Chunks)
        {
            if (written >= buffer.Length)
            {
                break;
            }

            var chunkStart = (long)chunk.StartBlock * blockSize;
            var chunkEnd = chunkStart + (long)chunk.Header.ChunkSize * blockSize;
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

        // Regions not covered by any chunk (e.g. logical tail beyond the last chunk) are zero.
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