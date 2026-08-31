using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Reads and parses Android sparse image files.
/// <para>读取并解析 Android 稀疏镜像文件。</para>
/// </summary>
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
    /// Loads a <see cref="SparseFile"/> lazily from a stream. The caller retains ownership
    /// and must keep the stream open until the returned SparseFile is disposed.
    /// <para>从流惰性加载 SparseFile；调用方保留所有权，并须保持流打开直到 SparseFile 被释放。</para>
    /// </summary>
    public static SparseFile FromStream(Stream stream, bool validateCrc = false, bool verbose = false)
    {
        var sparseFile = new SparseFile();

        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(headerData);
        sparseFile.Header = SparseHeader.FromBytes(headerData);

        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("Invalid sparse header");
        }

        if (sparseFile.Header.FileHeaderSize > SparseFormat.SparseHeaderSize)
        {
            SkipExtendedHeader(stream, sparseFile.Header.FileHeaderSize - SparseFormat.SparseHeaderSize);
        }

        Crc32? crc = validateCrc ? new Crc32() : null;
        try
        {
            uint currentBlock = 0;
            Span<byte> chunkHeaderData = stackalloc byte[SparseFormat.ChunkHeaderSize];
            Span<byte> buffer4 = stackalloc byte[4];

            for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
            {
                stream.ReadExactly(chunkHeaderData);
                var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

                if (sparseFile.Header.ChunkHeaderSize > SparseFormat.ChunkHeaderSize)
                {
                    SkipExtendedHeader(stream, sparseFile.Header.ChunkHeaderSize - SparseFormat.ChunkHeaderSize);
                }

                var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlock };

                if (!chunkHeader.IsValid(sparseFile.Header.ChunkHeaderSize, sparseFile.Header.BlockSize))
                {
                    throw new InvalidDataException($"Invalid chunk header for chunk {i}: Type 0x{chunkHeader.ChunkType:X4}");
                }

                var dataSize = (long)chunkHeader.TotalSize - sparseFile.Header.ChunkHeaderSize;
                var expectedRawSize = (long)chunkHeader.ChunkSize * sparseFile.Header.BlockSize;

                switch ((ChunkType)chunkHeader.ChunkType)
                {
                    case ChunkType.Raw:
                        if (dataSize != expectedRawSize)
                        {
                            throw new InvalidDataException($"Total size ({chunkHeader.TotalSize}) for RAW chunk {i} does not match expected data size ({expectedRawSize})");
                        }
                        // RAW 块不再整块物化：仅记录源流中的 [offset, length) 范围，
                        // 由 StreamDataProvider 在需要时直接流式读取。CRC 校验用池化缓冲增量计算。
                        // RAW chunks are no longer materialized in memory: we record the source range
                        // and let StreamDataProvider stream it on demand. CRC is computed incrementally
                        // through a pooled buffer.
                        if (stream.CanSeek)
                        {
                            var rawOffset = stream.Position;
                            if (crc is not null)
                            {
                                AppendStream(crc, stream, dataSize);
                            }
                            else
                            {
                                SeekPastRangeExactly(stream, dataSize);
                            }
                            chunk.DataProvider = new StreamDataProvider(stream, rawOffset, dataSize, leaveOpen: true);
                        }
                        else
                        {
                            // A forward-only source cannot back a lazy range. Spool exactly this bounded
                            // RAW payload and let the provider own/delete the temporary file.
                            var tempPath = Path.Combine(Path.GetTempPath(), $"uotan-sparse-{Guid.NewGuid():N}.tmp");
                            try
                            {
                                using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                {
                                    CopyRawExactly(stream, temp, crc, dataSize);
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
                        AppendRepeated(crc, chunk.FillValue, expectedRawSize);
                        break;

                    case ChunkType.DontCare:
                        if (dataSize != 0)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for DONT_CARE chunk {i} must be 0");
                        }
                        AppendZeros(crc, expectedRawSize);
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
                    currentBlock += chunkHeader.ChunkSize;
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException and not NotSupportedException)
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
    /// Loads a <see cref="SparseFile"/> from a byte array.
    /// <para>从字节数组加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromBuffer(byte[] buffer, bool validateCrc = false, bool verbose = false)
    {
        // 源流所有权随 SparseFile 转移：Dispose SparseFile 时一并关闭。
        // The source stream's ownership transfers to the SparseFile: disposing the
        // SparseFile closes it too.
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
    /// Loads a <see cref="SparseFile"/> from a file on disk.
    /// <para>从磁盘文件加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromImageFile(string filePath, bool validateCrc = false, bool verbose = false)
    {
        // RAW 块改为流式引用源文件范围，因此流必须保持打开并由 SparseFile 生命周期管理。
        // RAW chunks now reference the source file range lazily, so the stream must stay open
        // and is managed by the SparseFile lifecycle (closed on Dispose).
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
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
    /// The caller retains ownership and must keep the stream open while the returned SparseFile is used.
    /// <para>自动从流导入镜像数据，检测其为稀疏格式还是原始格式。调用方保留所有权，
    /// 并须在使用返回的 SparseFile 期间保持流打开。</para>
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

        // Raw image: wrap the whole stream as a single lazy raw chunk. The caller-facing
        // stream overload retains caller ownership; path overloads separately register ownership.
        // 原始镜像包装为单个惰性 RAW 块；Stream 重载保留调用方所有权，路径重载另行注册所有权。
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

    private static void AppendRepeated(Crc32? crc, uint fillValue, long totalBytes)
    {
        if (crc is null)
        {
            return;
        }

        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, fillValue);
        var full = (int)(totalBytes / 4);
        for (var i = 0; i < full; i++)
        {
            crc.Append(buf);
        }

        var rem = (int)(totalBytes % 4);
        if (rem > 0)
        {
            crc.Append(buf[..rem]);
        }
    }

    private static void AppendZeros(Crc32? crc, long totalBytes)
    {
        if (crc is null)
        {
            return;
        }

        Span<byte> buf = stackalloc byte[4096];
        var remaining = totalBytes;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buf.Length, remaining);
            crc.Append(buf[..n]);
            remaining -= n;
        }
    }

    /// <summary>
    /// Incrementally appends <paramref name="count"/> bytes read from <paramref name="stream"/>
    /// to the CRC using a pooled buffer.
    /// <para>使用池化缓冲从流中读取指定字节数并增量追加到 CRC。</para>
    /// </summary>
    private static void CopyRawExactly(Stream source, Stream destination, Crc32? crc, long count)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
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

    private static void AppendStream(Crc32 crc, Stream stream, long count)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
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
    /// Discards <paramref name="count"/> bytes from a non-seekable stream using a pooled buffer.
    /// <para>使用池化缓冲从不可寻址流中丢弃指定字节数。</para>
    /// </summary>
    private static void SkipExtendedHeader(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            SeekPastRangeExactly(stream, count);
            return;
        }
        SkipStream(stream, count);
    }

    private static void SkipStream(Stream stream, long count)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
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
    /// Reads up to <paramref name="count"/> logical (unsparsified) bytes of a sparse image
    /// starting at <paramref name="offset"/>, without expanding the whole image. Primarily
    /// used for filesystem magic detection on sparse images.
    /// <para>不展开整个镜像，从稀疏镜像的 offset 处读取最多 count 个逻辑（非稀疏）字节。
    /// 主要用于稀疏镜像的文件系统魔数探测。</para>
    /// </summary>
    /// <param name="filePath">Path to the sparse image file. <para>稀疏镜像文件路径。</para></param>
    /// <param name="offset">Logical byte offset to start reading from. <para>起始读取的逻辑字节偏移。</para></param>
    /// <param name="count">Maximum number of logical bytes to read. <para>要读取的最大逻辑字节数。</para></param>
    /// <returns>The bytes actually read; fewer than <paramref name="count"/> when the end of
    /// the image is reached. <para>实际读取到的字节数组；到达镜像末尾时可能少于 count。</para></returns>
    public static byte[] PeekLogicalRange(string filePath, long offset, int count)
    {
        // 打开源文件流，并在作用域结束时随 SparseFile / SparseReadStream 一并释放。
        // Open the source file stream; it is released together with the SparseFile and
        // SparseReadStream when the scope exits.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var sparseFile = FromImageFile(filePath);
        using var logical = new SparseReadStream(sparseFile);
        logical.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = logical.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
    }
}