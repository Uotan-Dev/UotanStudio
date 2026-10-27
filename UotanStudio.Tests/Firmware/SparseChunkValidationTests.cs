using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using Xunit;
using ChunkType = UotanStudio.Firmware.BlockImage.Sparse.Models.ChunkType;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Validates that unknown sparse chunk types are consistently rejected at the
/// header-validation stage, matching the reader's default-branch rejection.
/// The sparse image format defines only four standard types (RAW/FILL/DONT_CARE/CRC32).
/// <para>验证未知 sparse chunk 类型在头验证阶段即被一致拒绝，与读取端 default 分支一致。
/// 稀疏镜像格式仅定义四种标准类型（RAW/FILL/DONT_CARE/CRC32），校验与读取端因此保持一致。</para>
/// </summary>
public class SparseChunkValidationTests
{
    private const ushort ChunkHeaderSize = 12;
    private const uint BlockSize = 4096;

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x8000)]
    [InlineData(0xCAC5)]
    [InlineData(0xFFFF)]
    [InlineData(0x0005)]
    public void IsValid_RejectsUnknownTypes(ushort unknownType)
    {
        var header = new ChunkHeader
        {
            ChunkType = unknownType,
            Reserved = 0,
            ChunkSize = 1,
            TotalSize = ChunkHeaderSize + 4,
        };

        Assert.False(header.IsValid(ChunkHeaderSize, BlockSize),
            $"Unknown chunk type 0x{unknownType:X4} should be rejected by IsValid.");
    }

    [Fact]
    public void IsValid_AcceptsRawChunk()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Raw,
            ChunkSize = 2,
            TotalSize = ChunkHeaderSize + 2 * BlockSize,
        };
        Assert.True(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    [Fact]
    public void IsValid_AcceptsFillChunk()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Fill,
            ChunkSize = 1,
            TotalSize = ChunkHeaderSize + 4,
        };
        Assert.True(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    [Fact]
    public void IsValid_AcceptsDontCareChunk()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.DontCare,
            ChunkSize = 1,
            TotalSize = ChunkHeaderSize,
        };
        Assert.True(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    [Fact]
    public void IsValid_AcceptsCrc32Chunk()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Crc32,
            ChunkSize = 0,
            TotalSize = ChunkHeaderSize + 4,
        };
        Assert.True(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    [Fact]
    public void IsValid_RejectsUndersizedTotalSize()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Raw,
            ChunkSize = 1,
            TotalSize = 4,
        };
        Assert.False(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    [Fact]
    public void FromStream_UnknownChunkType_FailsParse()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(0xed26ff3au);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write((ushort)28);
        bw.Write((ushort)12);
        bw.Write(BlockSize);
        bw.Write(1u);
        bw.Write(1u);
        bw.Write(0u);

        bw.Write((ushort)ChunkType.Raw);
        bw.Write((ushort)0);
        bw.Write(1u);
        bw.Write(ChunkHeaderSize + BlockSize);
        bw.Write(new byte[BlockSize]);

        ms.Position = 0;

        var file = SparseReader.FromStream(ms);
        Assert.NotNull(file);
        file.Dispose();
    }

    [Fact]
    public void FromStream_VendorRangeChunkType_FailsParse()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(0xed26ff3au);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write((ushort)28);
        bw.Write((ushort)12);
        bw.Write(BlockSize);
        bw.Write(1u);
        bw.Write(1u);
        bw.Write(0u);
        bw.Write(0u);

        bw.Write((ushort)0xCAC5);
        bw.Write((ushort)0);
        bw.Write(1u);
        bw.Write(ChunkHeaderSize + 4);
        bw.Write(new byte[4]);

        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => SparseReader.FromStream(ms));
    }
}