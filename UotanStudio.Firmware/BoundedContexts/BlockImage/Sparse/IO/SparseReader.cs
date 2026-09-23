using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;

public static class SparseReader
{
    /// <summary>
    /// Peeks at the sparse header of a file without reading the entire content.
    /// <para>预览文件的稀疏头部,不读取全部内容。</para>
    /// </summary>
    public static SparseHeader PeekHeader(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(headerData);
        return SparseHeader.FromBytes(headerData);
    }

    /// <summary>
    /// Loads a SparseFile lazily from a stream.
    /// <para>从流惰性加载 SparseFile；调用方保留所有权，并须保持流打开直到 SparseFile 被释放。</para>
    /// </summary>
    public static SparseFile FromStream(Stream stream, bool validateCrc = false, bool verbose = false)
        => FromStream(stream, validateCrc, verbose, 1048576, CancellationToken.None);

    /// <summary>Loads sparse metadata with a bounded chunk count and cancellable checksum processing.</summary>
    /// <param name="stream">The caller-owned image stream.</param>
    /// <param name="validateCrc">Whether to verify stored image and chunk checksums.</param>
    /// <param name="verbose">Reserved diagnostic option.</param>
    /// <param name="maximumChunks">Maximum accepted chunk count, including checksum chunks.</param>
    /// <param name="cancellationToken">Cancellation token checked during parsing and checksum processing.</param>
    /// <returns>The lazy image model; the source must remain open while reading it.</returns>
    public static SparseFile FromStream(Stream stream, bool validateCrc, bool verbose, int maximumChunks, CancellationToken cancellationToken)
        => FromStream(stream, validateCrc, verbose, maximumChunks, cancellationToken, long.MaxValue);

    /// <summary>Rejects an oversized logical image before reading chunks or computing expanded checksums.</summary>
    /// <param name="stream">The caller-owned image stream.</param>
    /// <param name="validateCrc">Whether to verify stored checksums.</param>
    /// <param name="verbose">Reserved diagnostic option.</param>
    /// <param name="maximumChunks">Maximum accepted chunk count.</param>
    /// <param name="cancellationToken">Cancellation for parsing and checksum processing.</param>
    /// <param name="maximumExpandedLength">Nonnegative upper bound on declared logical bytes.</param>
    /// <returns>The lazy image model retaining references to the caller-owned stream.</returns>
    public static SparseFile FromStream(Stream stream, bool validateCrc, bool verbose, int maximumChunks,
        CancellationToken cancellationToken, long maximumExpandedLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChunks);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumExpandedLength);
        cancellationToken.ThrowIfCancellationRequested();
        var sparseFile = new SparseFile();

        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(headerData);
        sparseFile.Header = SparseHeader.FromBytes(headerData);

        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("Invalid sparse header");
        }

        if (sparseFile.Header.TotalChunks > maximumChunks
            || (ulong)sparseFile.Header.TotalBlocks * sparseFile.Header.BlockSize > (ulong)maximumExpandedLength)
        {
            throw new InvalidDataException("Sparse image exceeds the supported chunk count or logical length.");
        }

        if (sparseFile.Header.FileHeaderSize > SparseFormat.SparseHeaderSize)
        {
            SkipExtendedHeader(stream, sparseFile.Header.FileHeaderSize - SparseFormat.SparseHeaderSize, cancellationToken);
        }

        if (stream.CanSeek && sparseFile.Header.TotalChunks > (stream.Length - stream.Position) / sparseFile.Header.ChunkHeaderSize)
        {
            throw new InvalidDataException("Sparse chunk headers exceed the remaining image length.");
        }

        Crc32? crc = validateCrc ? new Crc32() : null;
        try
        {
            uint currentBlock = 0;
            Span<byte> chunkHeaderData = stackalloc byte[SparseFormat.ChunkHeaderSize];
            Span<byte> buffer4 = stackalloc byte[4];

            for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(chunkHeaderData);
                var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

                if (sparseFile.Header.ChunkHeaderSize > SparseFormat.ChunkHeaderSize)
                {
                    SkipExtendedHeader(stream, sparseFile.Header.ChunkHeaderSize - SparseFormat.ChunkHeaderSize, cancellationToken);
                }

                var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlock };

                if (!chunkHeader.IsValid(sparseFile.Header.ChunkHeaderSize, sparseFile.Header.BlockSize))
                {
                    throw new InvalidDataException($"Invalid chunk header for chunk {i}: Type 0x{chunkHeader.ChunkType:X4}");
                }

                var dataSize = (long)chunkHeader.TotalSize - sparseFile.Header.ChunkHeaderSize;
                var expectedRawSize = (long)chunkHeader.ChunkSize * sparseFile.Header.BlockSize;
                if (chunkHeader.ChunkType == (ushort)ChunkType.Crc32
                    ? chunkHeader.ChunkSize != 0
                    : chunkHeader.ChunkSize > sparseFile.Header.TotalBlocks - currentBlock)
                {
                    throw new InvalidDataException($"Chunk {i} exceeds the declared sparse block count.");
                }

                switch ((ChunkType)chunkHeader.ChunkType)
                {
                    case ChunkType.Raw:
                        if (dataSize != expectedRawSize)
                        {
                            throw new InvalidDataException($"Total size ({chunkHeader.TotalSize}) for RAW chunk {i} does not match expected data size ({expectedRawSize})");
                        }


                        if (stream.CanSeek)
                        {
                            var rawOffset = stream.Position;
                            if (crc is not null)
                            {
                                AppendStream(crc, stream, dataSize, cancellationToken);
                            }
                            else
                            {
                                SeekPastRangeExactly(stream, dataSize);
                            }
                            chunk.DataProvider = new StreamDataProvider(stream, rawOffset, dataSize, leaveOpen: true);
                        }
                        else
                        {


                            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan-sparse-{Guid.NewGuid():N}.tmp");
                            try
                            {
                                using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                {
                                    CopyRawExactly(stream, temp, crc, dataSize, cancellationToken);
                                }
                                chunk.DataProvider = new TemporaryFileDataProvider(tempPath, dataSize);
                            }
                            catch
                            {
                                File.Delete(tempPath);
                                throw;
                            }
                        }
                        break;

                    case ChunkType.Fill:
                        if (dataSize != 4)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for FILL chunk {i} must be 4");
                        }
                        stream.ReadExactly(buffer4);
                        chunk.FillValue = BinaryPrimitives.ReadUInt32LittleEndian(buffer4);
                        AppendRepeated(crc, chunk.FillValue, expectedRawSize, cancellationToken);
                        break;

                    case ChunkType.DontCare:
                        if (dataSize != 0)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for DONT_CARE chunk {i} must be 0");
                        }
                        AppendZeros(crc, expectedRawSize, cancellationToken);
                        break;

                    case ChunkType.Crc32:
                        if (dataSize != 4)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for CRC32 chunk {i} must be 4");
                        }
                        stream.ReadExactly(buffer4);
                        var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer4);
                        if (crc is not null)
                        {
                            var computed = crc.GetCurrentHashAsUInt32();
                            if (fileCrc != computed)
                            {
                                throw new InvalidDataException($"CRC32 checksum mismatch: file has 0x{fileCrc:X8}, computed 0x{computed:X8}");
                            }
                        }
                        break;

                    default:
                        throw new InvalidDataException($"Unknown chunk type for chunk {i}: 0x{chunkHeader.ChunkType:X4}");
                }

                if (chunkHeader.ChunkType != (ushort)ChunkType.Crc32)
                {
                    AddChunkRaw(sparseFile, chunk);
                    currentBlock = checked(currentBlock + chunkHeader.ChunkSize);
                }
            }
            if (currentBlock != sparseFile.Header.TotalBlocks)
            {
                throw new InvalidDataException("Sparse chunks do not cover the declared total block count.");
            }
            if (crc is not null && sparseFile.Header.ImageChecksum != 0 &&
                crc.GetCurrentHashAsUInt32() != sparseFile.Header.ImageChecksum)
            {
                throw new InvalidDataException("Sparse header image checksum does not match the expanded data.");
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException and not NotSupportedException and not OperationCanceledException)
        {
            sparseFile.Dispose();
            throw new InvalidDataException("Failed to parse sparse image", ex);
        }
        catch
        {
            sparseFile.Dispose();
            throw;
        }

        return sparseFile;
    }

    /// <summary>
    /// Loads a SparseFile from a byte array.
    /// <para>从字节数组加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromBuffer(byte[] buffer, bool validateCrc = false, bool verbose = false)
    {


        var ms = new MemoryStream(buffer, writable: false);
        try
        {
            var sparse = FromStream(ms, validateCrc, verbose);
            sparse.Own(ms);
            return sparse;
        }
        catch
        {
            ms.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads a SparseFile from a file on disk.
    /// <para>从磁盘文件加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromImageFile(string filePath, bool validateCrc = false, bool verbose = false)
    {


        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            var sparse = FromStream(stream, validateCrc, verbose);
            sparse.Own(stream);
            return sparse;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Automatically imports an image file, detecting whether it is sparse or raw.
    /// <para>自动导入镜像文件,检测其为稀疏格式还是原始格式。</para>
    /// </summary>
    public static SparseFile ImportAuto(string filePath, bool validateCrc = false, bool verbose = false)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var sparse = ImportAuto(stream, validateCrc, verbose, leaveOpen: true);
            sparse.Own(stream);
            return sparse;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Automatically imports image data from a stream, detecting whether it is sparse or raw.
    /// <para>自动从流导入镜像数据，检测其为稀疏格式还是原始格式。</para>
    /// </summary>
    public static SparseFile ImportAuto(Stream stream, bool validateCrc = false, bool verbose = false) =>
        ImportAuto(stream, validateCrc, verbose, leaveOpen: true);

    private static SparseFile ImportAuto(Stream stream, bool validateCrc, bool verbose, bool leaveOpen)
    {
        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        var pos = stream.Position;
        var read = stream.Read(headerData);
        stream.Position = pos;

        if (read >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(headerData) == SparseFormat.SparseHeaderMagic)
        {
            return FromStream(stream, validateCrc, verbose);
        }


        var length = stream.Length - stream.Position;
        var blockSize = 4096u;
        var totalBlocks = (uint)((length + blockSize - 1) / blockSize);
        var sparseFile = new SparseFile(blockSize, length);
        sparseFile.AddRawChunk(new StreamDataProvider(stream, stream.Position, length, leaveOpen), totalBlocks);
        return sparseFile;
    }

    /// <summary>
    /// Converts a raw binary file to a sparse representation.
    /// <para>将原始二进制文件转换为稀疏表示。</para>
    /// </summary>
    public static SparseFile FromRawFile(string filePath, uint blockSize = 4096)
    {
        var length = new FileInfo(filePath).Length;
        var sparseFile = new SparseFile(blockSize, length);
        sparseFile.AddRawChunk(new FileDataProvider(filePath, 0, length), (uint)((length + blockSize - 1) / blockSize));
        return sparseFile;
    }

    private static void AddChunkRaw(SparseFile sparseFile, SparseChunk chunk)
    {
        var chunks = (List<SparseChunk>)sparseFile.Chunks;
        var index = chunks.Count;
        while (index > 0 && chunks[index - 1].StartBlock > chunk.StartBlock)
        {
            index--;
        }
        chunks.Insert(index, chunk);
    }

    private static void AppendRepeated(Crc32? crc, uint fillValue, long totalBytes, CancellationToken cancellationToken)
    {
        if (crc is null)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var buf = buffer.AsSpan(0, 64 * 1024);
            SparseFill.Fill(buf, fillValue);
            while (totalBytes > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = (int)Math.Min(buf.Length, totalBytes);
                crc.Append(buf[..size]);
                totalBytes -= size;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendZeros(Crc32? crc, long totalBytes, CancellationToken cancellationToken)
    {
        if (crc is null)
        {
            return;
        }

        Span<byte> buf = stackalloc byte[4096];
        buf.Clear();
        var remaining = totalBytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = (int)Math.Min(buf.Length, remaining);
            crc.Append(buf[..n]);
            remaining -= n;
        }
    }

    /// <summary>
    /// Incrementally appends count bytes read from stream to the CRC using a pooled buffer.
    /// <para>使用池化缓冲从流中读取指定字节数并增量追加到 CRC。</para>
    /// </summary>
    private static void CopyRawExactly(Stream source, Stream destination, Crc32? crc, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream in a RAW chunk.");
                }
                destination.Write(buffer, 0, read);
                crc?.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendStream(Crc32 crc, Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while computing CRC over a RAW chunk.");
                }
                crc.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SeekPastRangeExactly(Stream stream, long count)
    {
        if (count < 0 || stream.Position > stream.Length - count)
        {
            throw new EndOfStreamException("Unexpected end of stream while skipping a RAW chunk.");
        }

        stream.Seek(count, SeekOrigin.Current);
    }

    /// <summary>
    /// Discards count bytes from a non-seekable stream using a pooled buffer.
    /// <para>使用池化缓冲从不可寻址流中丢弃指定字节数。</para>
    /// </summary>
    private static void SkipExtendedHeader(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            SeekPastRangeExactly(stream, count);
            return;
        }
        SkipStream(stream, count, cancellationToken);
    }

    private static void SkipStream(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while skipping a RAW chunk.");
                }
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads up to count logical (unsparsified) bytes of a sparse image starting at offset, without expanding the whole image.
    /// <para>不展开整个镜像，从稀疏镜像的 offset 处读取最多 count 个逻辑（非稀疏）字节。</para>
    /// </summary>
    public static byte[] PeekLogicalRange(string filePath, long offset, int count)
    {


        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var sparseFile = FromImageFile(filePath);
        using var logical = new SparseReadStream(sparseFile);
        logical.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = logical.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
    }
}