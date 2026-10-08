using System.Buffers.Binary;
using System.IO.Hashing;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Checksum tests for the batched FILL path of the sparse writer: the stored trailing CRC32 chunk must equal a CRC32
/// computed here from a byte-for-byte expansion of the same image, including runs that cross the batching table width,
/// leave a remainder below it, or are not a multiple of the 4-byte fill pattern.
/// <para>稀疏写入器批量 FILL 校验路径的测试：存储的 CRC32 尾块必须等于此处按同一镜像逐字节展开后独立算出的 CRC32，
/// 涵盖跨越批处理表宽度、留下不足表宽的余量，以及不是 4 字节填充模式整数倍的长度。</para>
/// </summary>
public class SparseFillCrcBatchingTests
{
    private const uint BlockSize = 4096;

    private const int CrcTableWidth = 64 * 1024;

    [Fact]
    public void WriteToStream_FillChunksLargerThanPatternBuffer_MatchIndependentlyComputedCrc32()
    {
        const uint firstPattern = 0xA1B2C3D4;
        const uint secondPattern = 0x00FF00AA;
        var wideBlocks = 50u;
        var narrowBlocks = 17u;
        var rawPayload = BuildPatternedBytes((int)BlockSize, 13);
        var totalBlocks = 1 + wideBlocks + narrowBlocks;

        using var sparse = new SparseFile(BlockSize, totalBlocks * BlockSize);
        sparse.AddRawChunk(rawPayload);
        sparse.AddFillChunk(firstPattern, wideBlocks * BlockSize);
        sparse.AddFillChunk(secondPattern, narrowBlocks * BlockSize);

        var expected = new byte[(int)(totalBlocks * BlockSize)];
        rawPayload.CopyTo(expected, 0);
        RepeatPattern(expected.AsSpan((int)BlockSize, (int)(wideBlocks * BlockSize)), firstPattern);
        RepeatPattern(expected.AsSpan((int)((1 + wideBlocks) * BlockSize), (int)(narrowBlocks * BlockSize)), secondPattern);

        Assert.True(wideBlocks * BlockSize > CrcTableWidth,
            $"The {wideBlocks * BlockSize}-byte fill run never exceeds the {CrcTableWidth}-byte batching table.");

        var sparseBytes = WriteWithCrc(sparse);

        Assert.Equal(Crc32.HashToUInt32(expected), ReadTrailingCrc(sparseBytes));

        using var loaded = SparseReader.FromBuffer(sparseBytes, validateCrc: true);
        using var expanded = new MemoryStream();
        loaded.WriteRawToStream(expanded);
        Assert.Equal(expected, expanded.ToArray());
    }

    [Fact]
    public void WriteToStream_FillLengthNotMultipleOfWidth_KeepsStreamingCrc()
    {
        const uint belowWidthPattern = 0x5A5A5AF1;
        const uint pastWidthPattern = 0x12345678;
        var belowWidthBlocks = 3u;
        var pastWidthBlocks = 40u;
        var belowWidthLength = belowWidthBlocks * BlockSize;
        var pastWidthLength = pastWidthBlocks * BlockSize;
        var totalBlocks = belowWidthBlocks + pastWidthBlocks;

        using var sparse = new SparseFile(BlockSize, totalBlocks * BlockSize);
        sparse.AddFillChunk(belowWidthPattern, belowWidthLength);
        sparse.AddFillChunk(pastWidthPattern, pastWidthLength);

        Assert.True(belowWidthLength < CrcTableWidth,
            $"The {belowWidthLength}-byte run is no longer shorter than the {CrcTableWidth}-byte batching table.");
        Assert.True(pastWidthLength > CrcTableWidth && pastWidthLength % CrcTableWidth != 0,
            $"The {pastWidthLength}-byte run leaves no remainder against the {CrcTableWidth}-byte batching table.");

        var expected = new byte[(int)(totalBlocks * BlockSize)];
        RepeatPattern(expected.AsSpan(0, (int)belowWidthLength), belowWidthPattern);
        RepeatPattern(expected.AsSpan((int)belowWidthLength, (int)pastWidthLength), pastWidthPattern);

        var sparseBytes = WriteWithCrc(sparse);

        Assert.Equal(Crc32.HashToUInt32(expected), ReadTrailingCrc(sparseBytes));

        using var loaded = SparseReader.FromBuffer(sparseBytes, validateCrc: true);
        using var expanded = new MemoryStream();
        loaded.WriteRawToStream(expanded);
        Assert.Equal(expected, expanded.ToArray());
    }

    [Fact]
    public void WriteToStream_BlockSizeNotMultipleOfFour_KeepsPatternTailInCrc()
    {
        const uint oddBlockSize = 6;
        const uint fillPattern = 0xDEADBEF2;
        var fillBlocks = 11001u;
        var expandedLength = (long)oddBlockSize * fillBlocks;

        Assert.True(expandedLength % 4 != 0,
            $"The {expandedLength}-byte expansion is a multiple of 4, so the pattern tail is never exercised.");
        Assert.True(expandedLength > CrcTableWidth,
            $"The {expandedLength}-byte expansion never crosses the {CrcTableWidth}-byte batching table.");

        using var sparse = new SparseFile(oddBlockSize, expandedLength);
        sparse.AddFillChunk(fillPattern, expandedLength);

        var expected = new byte[checked((int)expandedLength)];
        RepeatPattern(expected, fillPattern);

        var sparseBytes = WriteWithCrc(sparse);

        Assert.Equal(Crc32.HashToUInt32(expected), ReadTrailingCrc(sparseBytes));

        using var expandedStream = new MemoryStream();
        sparse.WriteRawToStream(expandedStream);
        Assert.Equal(expected, expandedStream.ToArray());
    }

    [Fact]
    public void WriteToStream_ZeroChunks_StillMatchOracle()
    {
        const uint fillPattern = 0xDEADBEEF;
        var totalBlocks = 12u;
        var firstPayload = BuildPatternedBytes((int)BlockSize, 7);
        var secondPayload = BuildPatternedBytes((int)BlockSize, 23);

        using var sparse = new SparseFile(BlockSize, totalBlocks * BlockSize);
        sparse.AddRawChunk(firstPayload, blockIndex: 0);
        sparse.AddFillChunk(fillPattern, BlockSize, blockIndex: 2);
        sparse.AddDontCareChunk(2 * BlockSize, blockIndex: 3);
        sparse.AddRawChunk(secondPayload, blockIndex: 5);

        var expected = new byte[(int)(totalBlocks * BlockSize)];
        firstPayload.CopyTo(expected, 0);
        RepeatPattern(expected.AsSpan(2 * (int)BlockSize, (int)BlockSize), fillPattern);
        secondPayload.CopyTo(expected, 5 * (int)BlockSize);

        var sparseBytes = WriteWithCrc(sparse);

        Assert.Equal(Crc32.HashToUInt32(expected), ReadTrailingCrc(sparseBytes));

        using var loaded = SparseReader.FromBuffer(sparseBytes, validateCrc: true);
        Assert.Equal(3, loaded.Chunks.Count(chunk => (ChunkType)chunk.Header.ChunkType == ChunkType.DontCare));

        using var expanded = new MemoryStream();
        loaded.WriteRawToStream(expanded);
        Assert.Equal(expected, expanded.ToArray());
    }

    private static byte[] WriteWithCrc(SparseFile sparse)
    {
        using var stream = new MemoryStream();
        sparse.WriteToStream(stream, sparse: true, includeCrc: true,
            cancellationToken: TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static uint ReadTrailingCrc(byte[] sparseBytes)
    {
        var header = SparseHeader.FromBytes(sparseBytes);
        var found = new List<uint>();
        var offset = (int)header.FileHeaderSize;

        for (var index = 0u; index < header.TotalChunks; index++)
        {
            Assert.True(offset + header.ChunkHeaderSize <= sparseBytes.Length,
                $"Chunk {index} starts at {offset}, past the end of the {sparseBytes.Length}-byte container.");
            var chunk = ChunkHeader.FromBytes(sparseBytes.AsSpan(offset));
            Assert.True(chunk.TotalSize >= header.ChunkHeaderSize,
                $"Chunk {index} declares {chunk.TotalSize} bytes, below its {header.ChunkHeaderSize}-byte header.");

            if (chunk.ChunkType == (ushort)ChunkType.Crc32)
            {
                Assert.Equal(0u, chunk.ChunkSize);
                Assert.Equal((uint)(header.ChunkHeaderSize + 4), chunk.TotalSize);
                Assert.Equal(sparseBytes.Length, offset + (int)chunk.TotalSize);
                found.Add(BinaryPrimitives.ReadUInt32LittleEndian(sparseBytes.AsSpan(offset + header.ChunkHeaderSize)));
            }

            offset += (int)chunk.TotalSize;
        }

        Assert.Equal(sparseBytes.Length, offset);
        return Assert.Single(found);
    }

    private static void RepeatPattern(Span<byte> destination, uint pattern)
    {
        var index = 0;
        while (index + 4 <= destination.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(index), pattern);
            index += 4;
        }

        if (index == destination.Length)
        {
            return;
        }

        Span<byte> patternBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(patternBytes, pattern);
        patternBytes[..(destination.Length - index)].CopyTo(destination[index..]);
    }

    private static byte[] BuildPatternedBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor) & 0xFF);
        }

        return data;
    }
}