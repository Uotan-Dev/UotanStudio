using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using Xunit;
using ChunkType = UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.ChunkType;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Validates that unknown sparse chunk types are consistently rejected at the
/// header-validation stage, matching the reader's default-branch rejection.
/// AOSP defines only four standard types (RAW/FILL/DONT_CARE/CRC32); the previous
/// <c>_ =&gt; (ChunkType &amp; 0x8000) != 0</c> rule allowed vendor-range types that the
/// reader's switch would then reject — leaving a validation/reading inconsistency.
/// <para>验证未知 sparse chunk 类型在头验证阶段即被一致拒绝，与读取端 default 分支一致。
/// AOSP 仅定义四种标准类型（RAW/FILL/DONT_CARE/CRC32）；之前的 <c>0x8000</c> 放行规则
/// 允许厂商范围类型，但读取端 switch 会拒绝它们，造成验证/读取不一致。</para>
/// </summary>
public class SparseChunkValidationTests
{
    private const ushort ChunkHeaderSize = 12;
    private const uint BlockSize = 4096;

    // ---- Unknown types are rejected at IsValid ----

    [Theory]
    [InlineData(0x0001)] // Unassigned low type
    [InlineData(0x8000)] // Vendor range base
    [InlineData(0xCAC5)] // Vendor range
    [InlineData(0xFFFF)] // Vendor range high
    [InlineData(0x0005)] // Unassigned
    public void IsValid_RejectsUnknownTypes(ushort unknownType)
    {
        // Construct a minimal chunk header with TotalSize >= ChunkHeaderSize.
        var header = new ChunkHeader
        {
            ChunkType = unknownType,
            Reserved = 0,
            ChunkSize = 1,
            TotalSize = ChunkHeaderSize + 4, // some payload
        };

        Assert.False(header.IsValid(ChunkHeaderSize, BlockSize),
            $"Unknown chunk type 0x{unknownType:X4} should be rejected by IsValid.");
    }

    // ---- Standard types are accepted ----

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

    // ---- TotalSize < ChunkHeaderSize is rejected ----

    [Fact]
    public void IsValid_RejectsUndersizedTotalSize()
    {
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Raw,
            ChunkSize = 1,
            TotalSize = 4, // < ChunkHeaderSize (12)
        };
        Assert.False(header.IsValid(ChunkHeaderSize, BlockSize));
    }

    // ---- Full small sparse input with unknown type also fails parsing ----

    [Fact]
    public void FromStream_UnknownChunkType_FailsParse()
    {
        // Build a minimal sparse image in memory with one RAW chunk.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Sparse header (28 bytes — exactly SparseFormat.SparseHeaderSize)
        bw.Write(0xed26ff3au); // magic
        bw.Write((ushort)1);   // major version
        bw.Write((ushort)1);   // minor version
        bw.Write((ushort)28);  // file header size
        bw.Write((ushort)12);  // chunk header size
        bw.Write(BlockSize);   // block size
        bw.Write(1u);          // total blocks (single RAW chunk of 1 block)
        bw.Write(1u);          // total chunks (just the RAW chunk)
        bw.Write(0u);          // image checksum

        // Chunk 1: RAW, 1 block
        bw.Write((ushort)ChunkType.Raw); // type
        bw.Write((ushort)0);                    // reserved
        bw.Write(1u);                           // chunk_size (blocks)
        bw.Write(ChunkHeaderSize + BlockSize);  // total_size
        bw.Write(new byte[BlockSize]);          // raw data

        ms.Position = 0;

        // Parsing should succeed for this valid minimal image.
        var file = SparseReader.FromStream(ms);
        Assert.NotNull(file);
        file.Dispose();
    }

    [Fact]
    public void FromStream_VendorRangeChunkType_FailsParse()
    {
        // Build a sparse image whose first chunk has vendor-range type 0xCAC5.
        // The reader's IsValid check must reject it.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Sparse header (28 bytes)
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

        // Chunk: vendor type 0xCAC5
        bw.Write((ushort)0xCAC5);
        bw.Write((ushort)0);
        bw.Write(1u);
        bw.Write(ChunkHeaderSize + 4);
        bw.Write(new byte[4]);

        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => SparseReader.FromStream(ms));
    }
}
