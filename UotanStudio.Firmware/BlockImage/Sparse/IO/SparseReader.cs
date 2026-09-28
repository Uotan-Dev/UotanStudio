using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BlockImage.Sparse.IO;

/// <summary>
/// Parses Android sparse images into lazily-evaluated <see cref="SparseFile"/> models, with bounded chunk counts, cancellable checksum passes and zero-filled raw-import fallbacks.
/// <para>将 Android 稀疏镜像解析为惰性求值的 <see cref="SparseFile"/> 模型：块数量受限、校验和阶段可取消，并提供以原始数据回退的导入方式。</para>
/// </summary>
public static class SparseReader
{
    /// <summary>
    /// Peeks at the sparse header of a file without reading the entire content.
    /// <para>预览文件的稀疏头部,不读取全部内容。</para>
    /// </summary>
    /// <param name="filePath">Path to a file whose first bytes are the 28-byte little-endian sparse header; no magic or geometry validation is applied.
    /// <para>路径指向一个以 28 字节小端稀疏头部起始的文件；不校验魔数与几何。</para></param>
    /// <returns>The raw header fields; check <see cref="SparseHeader.IsValid"/> before trusting them.
    /// <para>按原样解析的头部字段；信任前请先调用 <see cref="SparseHeader.IsValid"/>。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    /// <exception cref="EndOfStreamException">The file is shorter than the base header.
    /// <para>文件短于基础头部。</para></exception>
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
    /// <param name="stream">Caller-owned image stream positioned at the file header; RAW chunks reference positions inside it, so it must stay open while the model is read.
    /// <para>调用方持有、定位在文件头部的镜像流；RAW 块引用其内部位置，因此模型读取期间必须保持打开。</para></param>
    /// <param name="validateCrc">When <see langword="true"/>, expands a CRC32 over the logical data and verifies stored chunk and header checksums.
    /// <para>为 <see langword="true"/> 时按逻辑数据展开计算 CRC32，并校验存储的块与头部校验和。</para></param>
    /// <param name="verbose">Reserved diagnostic option; does not change parsing.
    /// <para>保留的诊断选项；不改变解析行为。</para></param>
    /// <returns>A lazy <see cref="SparseFile"/>; non-seekable streams have their RAW payloads spilled to temporary files owned by the model.
    /// <para>惰性的 <see cref="SparseFile"/>；不可寻址流的 RAW 载荷会落盘到模型拥有的临时文件。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The header, a chunk, or the checksums are invalid, or the image exceeds the default cap of 1,048,576 chunks; any partial model is disposed.
    /// <para>头部、某块或校验和无效，或镜像超过默认 1,048,576 块上限；部分构建的模型会被释放。</para></exception>
    public static SparseFile FromStream(Stream stream, bool validateCrc = false, bool verbose = false)
        => FromStream(stream, validateCrc, verbose, 1048576, CancellationToken.None);

    /// <summary>Loads sparse metadata with a bounded chunk count and cancellable checksum processing.
    /// <para>以受限的块数量加载稀疏元数据，校验和处理可取消。</para></summary>
    /// <param name="stream">The caller-owned image stream.
    /// <para>调用方持有的镜像流。</para></param>
    /// <param name="validateCrc">Whether to verify stored image and chunk checksums.
    /// <para>是否校验存储的镜像与块校验和。</para></param>
    /// <param name="verbose">Reserved diagnostic option.
    /// <para>保留的诊断选项。</para></param>
    /// <param name="maximumChunks">Maximum accepted chunk count, including checksum chunks.
    /// <para>接受的最大块数量，包含校验和块。</para></param>
    /// <param name="cancellationToken">Cancellation token checked during parsing and checksum processing.
    /// <para>在解析与校验和处理期间检查的取消令牌。</para></param>
    /// <returns>The lazy image model; the source must remain open while reading it.
    /// <para>惰性镜像模型；读取期间源流必须保持打开。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumChunks"/> is zero or negative.
    /// <para><paramref name="maximumChunks"/> 为 0 或负数。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the partial model is disposed.
    /// <para>已请求取消；部分构建的模型会被释放。</para></exception>
    /// <exception cref="InvalidDataException">Structure or bounds checks failed; the partial model is disposed.
    /// <para>结构或边界检查失败；部分构建的模型会被释放。</para></exception>
    public static SparseFile FromStream(Stream stream, bool validateCrc, bool verbose, int maximumChunks, CancellationToken cancellationToken)
        => FromStream(stream, validateCrc, verbose, maximumChunks, cancellationToken, long.MaxValue);

    /// <summary>Rejects an oversized logical image before reading chunks or computing expanded checksums.
    /// <para>在读取数据块或计算展开校验和之前，拒绝逻辑大小超限的镜像。</para></summary>
    /// <param name="stream">The caller-owned image stream.
    /// <para>调用方持有的镜像流。</para></param>
    /// <param name="validateCrc">Whether to verify stored checksums.
    /// <para>是否校验存储的校验和。</para></param>
    /// <param name="verbose">Reserved diagnostic option.
    /// <para>保留的诊断选项。</para></param>
    /// <param name="maximumChunks">Maximum accepted chunk count.
    /// <para>接受的最大块数量。</para></param>
    /// <param name="cancellationToken">Cancellation for parsing and checksum processing.
    /// <para>用于解析与校验和处理的取消令牌。</para></param>
    /// <param name="maximumExpandedLength">Nonnegative upper bound on declared logical bytes.
    /// <para>声明的逻辑字节数的非负上限。</para></param>
    /// <returns>The lazy image model retaining references to the caller-owned stream.
    /// <para>保留对调用方所有流引用的惰性镜像模型。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumChunks"/> is zero or negative, or <paramref name="maximumExpandedLength"/> is negative.
    /// <para><paramref name="maximumChunks"/> 为 0 或负数，或 <paramref name="maximumExpandedLength"/> 为负。</para></exception>
    /// <exception cref="InvalidDataException">The header or a chunk fails validation, declared totals exceed the accepted bounds, checksums mismatch, or chunks do not cover the declared block count; the partial model is disposed and any other failure (except unsupported-stream operations) is wrapped as an <see cref="InvalidDataException"/>.
    /// <para>头部或某块校验失败、声明总量超过接受边界、校验和不匹配或块未覆盖声明块数时抛出；部分构建的模型会被释放，其他失败（不支持的流操作除外）会被包装为 <see cref="InvalidDataException"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is observed before or between chunk records; the partial model is disposed.
    /// <para>在块记录之间或之前观察到取消；部分构建的模型会被释放。</para></exception>
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
        sparseFile.AssignParsedHeader(SparseHeader.FromBytes(headerData));

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
                    sparseFile.AddParsedChunk(chunk);
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
    /// <param name="buffer">Sparse image bytes; the array is wrapped without copying, so it must not be modified before the returned model is disposed.
    /// <para>稀疏镜像字节；数组被直接包装而不复制，因此在返回模型释放前不得修改。</para></param>
    /// <param name="validateCrc">When <see langword="true"/>, verifies stored chunk and header checksums.
    /// <para>为 <see langword="true"/> 时校验存储的块与头部校验和。</para></param>
    /// <param name="verbose">Reserved diagnostic option; does not change parsing.
    /// <para>保留的诊断选项；不改变解析行为。</para></param>
    /// <returns>A model whose RAW chunks read through the internal buffer-backed stream, which the model disposes for the caller.
    /// <para>其 RAW 块经内部缓冲流读取的模型；该流由模型代为释放。</para></returns>
    /// <exception cref="InvalidDataException">The bytes are not a valid sparse image; see the <see cref="FromStream(Stream, bool, bool, int, CancellationToken)"/> overload for the full failure behaviour.
    /// <para>字节不是有效的稀疏镜像；完整失败行为参见 <see cref="FromStream(Stream, bool, bool, int, CancellationToken)"/> 重载。</para></exception>
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
    /// <param name="filePath">Path to a sparse image file, opened for sequential reading.
    /// <para>稀疏镜像文件路径；以顺序读取方式打开。</para></param>
    /// <param name="validateCrc">When <see langword="true"/>, verifies stored chunk and header checksums.
    /// <para>为 <see langword="true"/> 时校验存储的块与头部校验和。</para></param>
    /// <param name="verbose">Reserved diagnostic option; does not change parsing.
    /// <para>保留的诊断选项；不改变解析行为。</para></param>
    /// <returns>A model that owns the underlying file stream; the file stays open until the model is disposed.
    /// <para>持有底层文件流的模型；直到模型释放前文件保持打开。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file is not a valid sparse image.
    /// <para>文件不是有效的稀疏镜像。</para></exception>
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
    /// <param name="filePath">Path to the image file; the first four little-endian bytes decide the format.
    /// <para>镜像文件路径；前四个小端字节决定格式。</para></param>
    /// <param name="validateCrc">Passed to the sparse parser; ignored for raw files.
    /// <para>传递给稀疏解析器；原始文件时忽略。</para></param>
    /// <param name="verbose">Reserved diagnostic option; does not change importing.
    /// <para>保留的诊断选项；不改变导入行为。</para></param>
    /// <returns>A model owning the opened file stream. A raw file becomes one 4096-byte-block RAW chunk covering the whole file; a sparse file is parsed as by <see cref="FromImageFile(string, bool, bool)"/>.
    /// <para>返回持有已打开文件流的模型。原始文件会成为一个覆盖整个文件、块大小 4096 的 RAW 块；稀疏文件按 <see cref="FromImageFile(string, bool, bool)"/> 解析。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The magic matches but the sparse structure fails validation.
    /// <para>魔数匹配但稀疏结构校验失败。</para></exception>
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
    /// <param name="stream">Caller-owned, seekable stream; detection peeks the header four bytes and rewinds, and raw import wraps the remaining bytes from the current position. The stream must stay open while the returned model is read.
    /// <para>调用方持有的可寻址流；检测时会窥视头部四字节后回退，原始导入则从当前位置包装剩余字节。读取返回模型期间流必须保持打开。</para></param>
    /// <param name="validateCrc">Passed to the sparse parser; ignored for raw data.
    /// <para>传递给稀疏解析器；原始数据时忽略。</para></param>
    /// <param name="verbose">Reserved diagnostic option; does not change importing.
    /// <para>保留的诊断选项；不改变导入行为。</para></param>
    /// <returns>A model referencing the stream, which stays open when the model is disposed.
    /// <para>引用该流的模型；模型释放时流保持打开。</para></returns>
    /// <exception cref="NotSupportedException">The stream does not support seeking, reading or the position.
    /// <para>流不支持寻址、读取或位置访问。</para></exception>
    /// <exception cref="InvalidDataException">The magic matches but the sparse structure fails validation.
    /// <para>魔数匹配但稀疏结构校验失败。</para></exception>
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
    /// <param name="filePath">Path to the raw file; its bytes are never read here, only its length is taken.
    /// <para>原始文件路径；此处不读取其字节，仅获取长度。</para></param>
    /// <param name="blockSize">Logical block size for the produced image; must be non-zero and is not validated further.
    /// <para>生成镜像的逻辑块大小；必须非 0，不做更多校验。</para></param>
    /// <returns>A model with one RAW chunk whose provider lazily reads the file from disk; the source file is not opened until the chunk is read.
    /// <para>返回仅含一个 RAW 块的模型，其提供器按需从磁盘读取；在读取该块前不会打开源文件。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is zero.
    /// <para><paramref name="blockSize"/> 为 0。</para></exception>
    /// <exception cref="OverflowException">The file's block count exceeds <see cref="uint.MaxValue"/>.
    /// <para>文件的块数超过 <see cref="uint.MaxValue"/>。</para></exception>
    public static SparseFile FromRawFile(string filePath, uint blockSize = 4096)
    {
        var length = new FileInfo(filePath).Length;
        var sparseFile = new SparseFile(blockSize, length);
        sparseFile.AddRawChunk(new FileDataProvider(filePath, 0, length), (uint)((length + blockSize - 1) / blockSize));
        return sparseFile;
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
    /// <param name="filePath">Path to a sparse image file; the file is closed again before the result is returned.
    /// <para>稀疏镜像文件路径；返回结果前文件已再次关闭。</para></param>
    /// <param name="offset">Absolute offset in the expanded logical image; positions past the end yield an empty result.
    /// <para>展开后逻辑镜像中的绝对偏移；超出末尾时返回空结果。</para></param>
    /// <param name="count">Maximum number of bytes to return; regions covered by don't-care or holes come back as zeros.
    /// <para>返回字节数的上限；由 don't-care 或空隙覆盖的区域以零返回。</para></param>
    /// <returns>The requested bytes, shortened only when the logical end is reached.
    /// <para>请求的字节；仅在到达逻辑末尾时结果才会变短。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file is not a valid sparse image.
    /// <para>文件不是有效的稀疏镜像。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.
    /// <para><paramref name="count"/> 为负。</para></exception>
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