using System.Buffers.Binary;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Serializes a <see cref="SparseFile"/> back to the sparse on-disk format.
/// <para>将 SparseFile 序列化回稀疏磁盘格式。</para>
/// </summary>
public static class SparseWriter
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Writes the sparse image to the stream (sparse format).
    /// <para>将稀疏镜像写入流(稀疏格式)。</para>
    /// </summary>
    public static void WriteToStream(SparseFile sparseFile, Stream stream, bool includeCrc = false)
    {
        var chunks = sparseFile.Chunks;
        var finalChunks = new List<SparseChunk>(chunks.Count + 10);
        uint currentBlock = 0;

        foreach (var chunk in chunks)
        {
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
            finalChunks.Add(chunk);
            currentBlock = chunk.StartBlock + chunk.Header.ChunkSize;
        }

        var outHeader = sparseFile.Header;
        var sumBlocks = currentBlock;
        var needsTrailingSkip = outHeader.TotalBlocks > sumBlocks;

        var totalChunks = (uint)finalChunks.Count;
        if (needsTrailingSkip)
        {
            totalChunks++;
        }
        if (includeCrc)
        {
            totalChunks++;
        }

        outHeader = outHeader with { TotalChunks = totalChunks };
        if (sumBlocks > outHeader.TotalBlocks)
        {
            outHeader = outHeader with { TotalBlocks = sumBlocks };
        }

        var headerData = new byte[SparseFormat.SparseHeaderSize];
        outHeader.WriteTo(headerData);
        stream.Write(headerData);
        if (outHeader.FileHeaderSize > SparseFormat.SparseHeaderSize)
        {
            var pad = new byte[outHeader.FileHeaderSize - SparseFormat.SparseHeaderSize];
            stream.Write(pad);
        }

        var checksum = includeCrc ? Crc32.Begin() : (uint?)null;
        var buffer = new byte[BufferSize];
        var headerBuf = new byte[outHeader.ChunkHeaderSize];
        var fillValData = new byte[4];

        foreach (var chunk in finalChunks)
        {
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
                        // Raw 块数据必须按 chunkSize * blockSize 写入(不足部分以零补齐),与头部声明一致。
                        // Raw chunk data must be written as chunkSize * blockSize (zero-padded), matching the header claim.
                        var expectedDataSize = (long)chunk.Header.ChunkSize * outHeader.BlockSize;
                        long written = 0;
                        while (written < expectedDataSize)
                        {
                            var toRead = (int)Math.Min(buffer.Length, expectedDataSize - written);
                            var n = chunk.DataProvider.Read(written, buffer.AsSpan(0, toRead));
                            if (n <= 0)
                            {
                                // 数据源耗尽:以零补齐剩余块。
                                // Data source exhausted: zero-pad the remaining block.
                                buffer.AsSpan(n, toRead - n).Clear();
                                n = toRead;
                            }
                            stream.Write(buffer, 0, n);
                            if (checksum.HasValue)
                            {
                                checksum = Crc32.Update(checksum.Value, buffer.AsSpan(0, n));
                            }
                            written += n;
                        }
                        break;
                    }
                case ChunkType.Fill:
                    {
                        // Per the Android sparse spec a FILL chunk stores only the 4-byte pattern;
                        // readers expand it to chunkSize * blockSize logically.
                        // <para>按 Android 稀疏格式规范，FILL 数据块只存储 4 字节模式值；
                        // 读取方将其逻辑展开为 chunkSize * blockSize。</para>
                        BinaryPrimitives.WriteUInt32LittleEndian(fillValData, chunk.FillValue);
                        stream.Write(fillValData, 0, 4);
                        if (checksum.HasValue)
                        {
                            checksum = Crc32.UpdateRepeated(checksum.Value, chunk.FillValue, (long)chunk.Header.ChunkSize * outHeader.BlockSize);
                        }
                        break;
                    }

                case ChunkType.Raw:
                    break;
                case ChunkType.DontCare:
                    break;
                case ChunkType.Crc32:
                    break;
                default:
                    {
                        // DontCare: no payload.
                        if (checksum.HasValue)
                        {
                            checksum = Crc32.UpdateZero(checksum.Value, (long)chunk.Header.ChunkSize * outHeader.BlockSize);
                        }
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
            if (checksum.HasValue)
            {
                checksum = Crc32.UpdateZero(checksum.Value, (long)trailingBlocks * outHeader.BlockSize);
            }
        }

        if (includeCrc && checksum.HasValue)
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
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, Crc32.Finish(checksum.Value));
            stream.Write(crcBuf);
        }
    }
}