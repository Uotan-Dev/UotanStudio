using System.Buffers.Binary;

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
    /// Loads a <see cref="SparseFile"/> from a stream.
    /// <para>从流加载 SparseFile。</para>
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
            if (!stream.CanSeek)
            {
                throw new InvalidDataException("Stream must be seekable when sparse file header is extended.");
            }
            stream.Seek(sparseFile.Header.FileHeaderSize - SparseFormat.SparseHeaderSize, SeekOrigin.Current);
        }

        uint? checksum = validateCrc ? Crc32.Begin() : null;
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
                    if (!stream.CanSeek)
                    {
                        throw new InvalidDataException("Stream must be seekable when chunk headers are extended.");
                    }
                    stream.Seek(sparseFile.Header.ChunkHeaderSize - SparseFormat.ChunkHeaderSize, SeekOrigin.Current);
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
                        if (dataSize > int.MaxValue)
                        {
                            throw new NotSupportedException($"Raw data for chunk {i} is too large ({dataSize} bytes)");
                        }
                        var rawData = new byte[dataSize];
                        stream.ReadExactly(rawData);
                        if (checksum.HasValue)
                        {
                            checksum = Crc32.Update(checksum.Value, rawData);
                        }
                        chunk.DataProvider = new MemoryDataProvider(rawData);
                        break;

                    case ChunkType.Fill:
                        if (dataSize != 4)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for FILL chunk {i} must be 4");
                        }
                        stream.ReadExactly(buffer4);
                        chunk.FillValue = BinaryPrimitives.ReadUInt32LittleEndian(buffer4);
                        if (checksum.HasValue)
                        {
                            checksum = Crc32.UpdateRepeated(checksum.Value, chunk.FillValue, expectedRawSize);
                        }
                        break;

                    case ChunkType.DontCare:
                        if (dataSize != 0)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for DONT_CARE chunk {i} must be 0");
                        }
                        if (checksum.HasValue)
                        {
                            checksum = Crc32.UpdateZero(checksum.Value, expectedRawSize);
                        }
                        break;

                    case ChunkType.Crc32:
                        if (dataSize != 4)
                        {
                            throw new InvalidDataException($"Data size ({dataSize}) for CRC32 chunk {i} must be 4");
                        }
                        stream.ReadExactly(buffer4);
                        var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer4);
                        if (checksum.HasValue && fileCrc != Crc32.Finish(checksum.Value))
                        {
                            throw new InvalidDataException($"CRC32 checksum mismatch: file has 0x{fileCrc:X8}, computed 0x{Crc32.Finish(checksum.Value):X8}");
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
            throw new InvalidDataException("Failed to parse sparse image", ex);
        }

        return sparseFile;
    }

    /// <summary>
    /// Loads a <see cref="SparseFile"/> from a byte array.
    /// <para>从字节数组加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromBuffer(byte[] buffer, bool validateCrc = false, bool verbose = false)
    {
        using var ms = new MemoryStream(buffer, writable: false);
        return FromStream(ms, validateCrc, verbose);
    }

    /// <summary>
    /// Loads a <see cref="SparseFile"/> from a file on disk.
    /// <para>从磁盘文件加载 SparseFile。</para>
    /// </summary>
    public static SparseFile FromImageFile(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return FromStream(stream, validateCrc, verbose);
    }

    /// <summary>
    /// Automatically imports an image file, detecting whether it is sparse or raw.
    /// <para>自动导入镜像文件,检测其为稀疏格式还是原始格式。</para>
    /// </summary>
    public static SparseFile ImportAuto(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ImportAuto(stream, validateCrc, verbose);
    }

    /// <summary>
    /// Automatically imports image data from a stream, detecting whether it is sparse or raw.
    /// <para>自动从流导入镜像数据,检测其为稀疏格式还是原始格式。</para>
    /// </summary>
    public static SparseFile ImportAuto(Stream stream, bool validateCrc = false, bool verbose = false)
    {
        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        var pos = stream.Position;
        var read = stream.Read(headerData);
        stream.Position = pos;

        if (read >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(headerData) == SparseFormat.SparseHeaderMagic)
        {
            return FromStream(stream, validateCrc, verbose);
        }

        // Raw image: wrap the whole stream as a single raw chunk.
        var length = stream.Length - stream.Position;
        var blockSize = 4096u;
        var totalBlocks = (uint)((length + blockSize - 1) / blockSize);
        var sparseFile = new SparseFile(blockSize, length);
        sparseFile.AddRawChunk(new StreamDataProvider(stream, stream.Position, length), totalBlocks);
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
}