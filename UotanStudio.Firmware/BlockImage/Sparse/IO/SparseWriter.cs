using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.BlockImage.Sparse.IO;

/// <summary>
/// Serializes a <see cref="SparseFile"/> into Android sparse-image bytes, normalizing chunk geometry on the way out.
/// <para>将 <see cref="SparseFile"/> 序列化为 Android 稀疏镜像字节，并在输出时规范化数据块几何。</para>
/// </summary>
internal static class SparseWriter
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Width of the repeated-pattern table used to checksum FILL chunks.
    /// <para>用于校验 FILL 数据块的重复模式表宽度。</para></summary>
    /// <remarks>
    /// Kept as a multiple of the 4-byte pattern so every full-width append ends on a pattern boundary and the final
    /// short append still starts at pattern offset 0; a length that is not a multiple of the table width therefore
    /// needs no separate tail arithmetic. 64 KiB matches the tables used by <see cref="SparseReader"/> and
    /// <see cref="SparseFile.WriteRawToStream(Stream)"/>, and is pooled rather than <c>stackalloc</c>-ed because a per-chunk
    /// 64 KiB stack frame exceeds the small-buffer convention used elsewhere in this library.
    /// <para>宽度保持为 4 字节模式的整数倍，使每次满宽追加都结束在模式边界上，末段不足宽度的追加仍从模式偏移 0 开始；
    /// 因此不是表宽度整数倍的长度无需单独的尾部算术。64 KiB 与 <see cref="SparseReader"/> 和
    /// <see cref="SparseFile.WriteRawToStream(Stream)"/> 使用的表一致，并采用池化而非 <c>stackalloc</c>：
    /// 每块 64 KiB 的栈帧超出本库其他位置的小缓冲惯例。</para>
    /// </remarks>
    private const int FillPatternWidth = 64 * 1024;

    /// <summary>Writes the sparse image to the stream (sparse format).
    /// <para>将稀疏镜像写入流(稀疏格式)。</para></summary>
    /// <param name="sparseFile">Source model; not modified. Don't-care chunks are inserted for gaps and for the trailing region below the declared total blocks, oversized RAW chunks are split so each fits a 32-bit total size, and every chunk header's total size is rewritten to the value implied by its type, block count and the header's geometry.
    /// <para>源模型；不会被修改。在空隙及低于声明总块数的尾部区域会插入 don't-care 块，超大 RAW 块会被拆分以适配 32 位总大小，且每个块头部的总大小都会按其类型、块数与头部几何重写。</para></param>
    /// <param name="stream">Caller-owned writable destination; it is not flushed or closed. The declared chunk count and, when covered chunks extend past it, the total block count are raised in the emitted header; header sizes beyond the base sizes are padded with zeros.
    /// <para>调用方持有的可写目标；不刷新也不关闭。发出的头部会抬高声明的块数，块覆盖超出声明总块数时同样抬高总块数；超出基础大小的头部字节以零填充。</para></param>
    /// <param name="includeCrc">Appends a trailing CRC32 chunk covering the fully expanded image — RAW bytes, repeated fill patterns and all zero-filled regions included — and stores its little-endian value; the header's image checksum field is left untouched.
    /// <para>追加一个覆盖完整展开镜像的 CRC32 尾块——包含 RAW 字节、重复的填充模式与所有补零区域——并写入其小端值；头部的镜像校验和字段保持不变。</para></param>
    /// <param name="options">Optional write geometry; <see langword="null"/> keeps the historical output. <see cref="SparseWriteOptions.MaxRawChunkBlocks"/> caps RAW chunk sizes beyond the automatic 32-bit fitting, <see cref="SparseWriteOptions.EmitCrc"/> is OR-combined with <paramref name="includeCrc"/>, and <see cref="SparseWriteOptions.FileAlignment"/> rounds the emitted total block count up with a trailing don't-care chunk.
    /// <para>可选写出几何；<see langword="null"/> 保持历史输出。<see cref="SparseWriteOptions.MaxRawChunkBlocks"/> 在自动 32 位适配之外进一步限制 RAW 块大小，<see cref="SparseWriteOptions.EmitCrc"/> 与 <paramref name="includeCrc"/> 取或，<see cref="SparseWriteOptions.FileAlignment"/> 用尾块 don't-care 将输出的总块数向上取整。</para></param>
    /// <param name="cancellationToken">Checked before the header and between chunk writes; cancellation leaves the destination stream partially written because this stream-level API has no atomic publication.
    /// <para>在头部之前以及各数据块写入之间检查；取消会因该流级 API 无原子发布而在目标流留下部分写入。</para></param>
    /// <exception cref="InvalidDataException">The header's block size is zero or too large for a RAW chunk's total size to fit 32 bits.
    /// <para>头部块大小为 0，或过大导致 RAW 块总大小无法容纳于 32 位。</para></exception>
    /// <exception cref="OverflowException">A chunk end position or rewritten total size exceeds the 32-bit range.
    /// <para>某块的结束位置或重写后的总大小超过 32 位范围。</para></exception>
    public static void WriteToStream(SparseFile sparseFile, Stream stream, bool includeCrc = false,
        SparseWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options?.Validate();
        var effectiveCrc = includeCrc || (options?.EmitCrc ?? false);
        var temporaryChunks = new List<SparseChunk>();
        try
        {
            WriteToStreamCore(sparseFile, stream, effectiveCrc, temporaryChunks, options, cancellationToken);
        }
        finally
        {
            foreach (var chunk in temporaryChunks)
            {
                chunk.Dispose();
            }
        }
    }

    private static void WriteToStreamCore(SparseFile sparseFile, Stream stream, bool includeCrc,
        List<SparseChunk> temporaryChunks, SparseWriteOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chunks = sparseFile.Chunks;
        var finalChunks = new List<SparseChunk>(chunks.Count + 10);
        uint currentBlock = 0;

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.StartBlock > currentBlock)
            {
                var gapBlocks = chunk.StartBlock - currentBlock;
                finalChunks.Add(new SparseChunk(new ChunkHeader
                {
                    ChunkType = (ushort)ChunkType.DontCare,
                    ChunkSize = gapBlocks,
                    TotalSize = SparseFormat.ChunkHeaderSize
                })
                { StartBlock = currentBlock });
            }
            if ((ChunkType)chunk.Header.ChunkType == ChunkType.Raw)
            {
                AddSplitRawChunks(finalChunks, temporaryChunks, chunk, sparseFile.Header.BlockSize, sparseFile.Header.ChunkHeaderSize, options?.MaxRawChunkBlocks);
            }
            else
            {
                finalChunks.Add(chunk);
            }
            currentBlock = checked(chunk.StartBlock + chunk.Header.ChunkSize);
        }

        var outHeader = sparseFile.Header;
        var sumBlocks = currentBlock;
        var declaredBlocks = Math.Max(outHeader.TotalBlocks, sumBlocks);
        if (options is { FileAlignment: > 1 })
        {
            var alignment = options.FileAlignment;
            declaredBlocks = checked((uint)(((declaredBlocks + (ulong)alignment - 1) / alignment) * alignment));
        }
        var needsTrailingSkip = declaredBlocks > sumBlocks;

        var totalChunks = (uint)finalChunks.Count;
        if (needsTrailingSkip)
        {
            totalChunks++;
        }
        if (includeCrc)
        {
            totalChunks++;
        }

        outHeader = outHeader with { TotalChunks = totalChunks, TotalBlocks = declaredBlocks };

        var headerData = new byte[SparseFormat.SparseHeaderSize];
        outHeader.WriteTo(headerData);
        stream.Write(headerData);
        if (outHeader.FileHeaderSize > SparseFormat.SparseHeaderSize)
        {
            var pad = new byte[outHeader.FileHeaderSize - SparseFormat.SparseHeaderSize];
            stream.Write(pad);
        }

        Crc32IeeeAccumulator? crc = includeCrc ? new Crc32IeeeAccumulator() : null;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var headerBuf = new byte[outHeader.ChunkHeaderSize];
        var fillValData = new byte[4];

        try
        {
            foreach (var chunk in finalChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedTotal = ChunkHelper.GetExpectedTotalSize(chunk.Header.ChunkType, chunk.Header.ChunkSize, outHeader.ChunkHeaderSize, outHeader.BlockSize);
                var headerToWrite = chunk.Header.TotalSize == expectedTotal
                    ? chunk.Header
                    : new ChunkHeader
                    {
                        ChunkType = chunk.Header.ChunkType,
                        Reserved = chunk.Header.Reserved,
                        ChunkSize = chunk.Header.ChunkSize,
                        TotalSize = expectedTotal
                    };

                headerToWrite.WriteTo(headerBuf);
                stream.Write(headerBuf, 0, outHeader.ChunkHeaderSize);

                switch ((ChunkType)chunk.Header.ChunkType)
                {
                    case ChunkType.Raw when chunk.DataProvider != null:
                        {


                            var expectedDataSize = (long)chunk.Header.ChunkSize * outHeader.BlockSize;
                            long written = 0;
                            while (written < expectedDataSize)
                            {
                                var toRead = (int)Math.Min(buffer.Length, expectedDataSize - written);
                                var n = chunk.DataProvider.Read(written, buffer.AsSpan(0, toRead));
                                if (n <= 0)
                                {


                                    buffer.AsSpan(n, toRead - n).Clear();
                                    n = toRead;
                                }
                                stream.Write(buffer, 0, n);
                                crc?.Append(buffer.AsSpan(0, n));
                                written += n;
                            }
                            break;
                        }
                    case ChunkType.Fill:
                        {


                            BinaryPrimitives.WriteUInt32LittleEndian(fillValData, chunk.FillValue);
                            stream.Write(fillValData, 0, 4);
                            AppendRepeated(crc, chunk.FillValue, (long)chunk.Header.ChunkSize * outHeader.BlockSize);
                            break;
                        }

                    case ChunkType.Raw:
                        break;
                    case ChunkType.DontCare:
                        AppendZeros(crc, (long)chunk.Header.ChunkSize * outHeader.BlockSize);
                        break;
                    case ChunkType.Crc32:
                        break;
                    default:
                        {
                            AppendZeros(crc, (long)chunk.Header.ChunkSize * outHeader.BlockSize);
                            break;
                        }
                }
            }

            if (needsTrailingSkip)
            {
                var trailingBlocks = outHeader.TotalBlocks - sumBlocks;
                var trailingHeader = new ChunkHeader
                {
                    ChunkType = (ushort)ChunkType.DontCare,
                    ChunkSize = trailingBlocks,
                    TotalSize = SparseFormat.ChunkHeaderSize
                };
                trailingHeader.WriteTo(headerBuf);
                stream.Write(headerBuf, 0, outHeader.ChunkHeaderSize);
                AppendZeros(crc, (long)trailingBlocks * outHeader.BlockSize);
            }

            if (includeCrc && crc is not null)
            {
                var crcHeader = new ChunkHeader
                {
                    ChunkType = (ushort)ChunkType.Crc32,
                    ChunkSize = 0,
                    TotalSize = SparseFormat.ChunkHeaderSize + 4
                };
                crcHeader.WriteTo(headerBuf);
                stream.Write(headerBuf, 0, outHeader.ChunkHeaderSize);
                var crcBuf = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc.GetCurrentHash());
                stream.Write(crcBuf);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AddSplitRawChunks(List<SparseChunk> output, List<SparseChunk> temporaryChunks, SparseChunk chunk, uint blockSize, ushort headerSize, uint? maxBlocksOverride)
    {
        if (blockSize == 0) throw new InvalidDataException("Sparse block size must be non-zero.");
        var fitCeiling = (uint)((uint.MaxValue - (ulong)headerSize) / blockSize);
        if (fitCeiling == 0) throw new InvalidDataException("Sparse block size is too large for a RAW chunk.");
        var maxBlocks = maxBlocksOverride is > 0 ? Math.Min(maxBlocksOverride.Value, fitCeiling) : fitCeiling;

        var remaining = (long)chunk.Header.ChunkSize;
        long blockOffset = 0;
        while (remaining > 0)
        {
            var blocks = (uint)Math.Min(remaining, maxBlocks);
            var byteOffset = checked(blockOffset * blockSize);
            var byteLength = checked((long)blocks * blockSize);
            var startBlock = checked(chunk.StartBlock + (uint)blockOffset);
            var provider = chunk.DataProvider?.GetSubProvider(byteOffset, Math.Min(byteLength, Math.Max(0, chunk.DataProvider.Length - byteOffset)));
            var split = new SparseChunk(new ChunkHeader
            {
                ChunkType = (ushort)ChunkType.Raw,
                Reserved = chunk.Header.Reserved,
                ChunkSize = blocks,
                TotalSize = checked((uint)(headerSize + (ulong)byteLength)),
            })
            {
                StartBlock = startBlock,
                DataProvider = provider,
            };
            temporaryChunks.Add(split);
            output.Add(split);
            remaining -= blocks;
            blockOffset = checked(blockOffset + blocks);
        }
    }

    /// <summary>
    /// Appends a FILL chunk's expanded bytes to the streaming checksum through a pooled repeated-pattern table.
    /// <para>经池化的重复模式表，把 FILL 数据块展开后的字节追加到流式校验和。</para>
    /// </summary>
    /// <param name="crc">Checksum accumulator, or <see langword="null"/> when no checksum was requested, in which case nothing is rented and nothing is appended.
    /// <para>校验和累加器；未请求校验和时为 <see langword="null"/>，此时不租用缓冲也不追加任何字节。</para></param>
    /// <param name="fillValue">The little-endian 4-byte pattern repeated across the chunk's expanded region.
    /// <para>在数据块展开区域中重复的小端 4 字节模式。</para></param>
    /// <param name="totalBytes">Expanded length, <c>ChunkSize * BlockSize</c>. Whole table widths plus one final remainder are appended; because the table width is a multiple of the pattern, a length that is not a multiple of 4 — possible when the header's block size is not a multiple of 4 — still receives exactly the leading <c>totalBytes % 4</c> pattern bytes, matching a byte-for-byte expansion.
    /// <para>展开后的长度，即 <c>ChunkSize * BlockSize</c>。按整表宽度加上最后一次余量追加；由于表宽度是模式的整数倍，
    /// 即使长度不是 4 的倍数（在头部块大小不是 4 的倍数时可能出现）也只追加模式前 <c>totalBytes % 4</c> 字节，与逐字节展开结果一致。</para></param>
    private static void AppendRepeated(Crc32IeeeAccumulator? crc, uint fillValue, long totalBytes)
    {
        if (crc is null || totalBytes <= 0)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(FillPatternWidth);
        try
        {
            var buf = rented.AsSpan(0, FillPatternWidth);
            SparseFill.Fill(buf, fillValue);
            var remaining = totalBytes;
            while (remaining > 0)
            {
                var size = (int)Math.Min(buf.Length, remaining);
                crc.Append(buf[..size]);
                remaining -= size;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendZeros(Crc32IeeeAccumulator? crc, long totalBytes)
    {
        if (crc is null)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(FillPatternWidth);
        try
        {
            var buf = rented.AsSpan(0, FillPatternWidth);
            buf.Clear();
            var remaining = totalBytes;
            while (remaining > 0)
            {
                var n = (int)Math.Min(buf.Length, remaining);
                crc.Append(buf[..n]);
                remaining -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}