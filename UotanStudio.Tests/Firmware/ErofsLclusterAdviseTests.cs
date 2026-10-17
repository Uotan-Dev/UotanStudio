using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsLclusterAdviseTests
{
    private const int BlockSize = 4096;
    private const int IndexRowSize = 8;
    private const int FullIndexStart = ZErofsMapHeader.Size + 8;

    [Fact]
    public void ValidateAdvise_BitFourteenSet_ThrowsFileSystemCorruptedException()
    {
        var entry = new ZErofsLclusterIndex((ushort)(1 << 14), 0, 0);

        Assert.Throws<FileSystemCorruptedException>(entry.ValidateAdvise);
    }

    [Fact]
    public void ValidateAdvise_D0CblkCntAdviseBit_IsAccepted()
    {
        var entry = new ZErofsLclusterIndex(ZErofsLclusterIndex.LiD0CblkCnt, 0, 0);

        entry.ValidateAdvise();

        Assert.True(entry.HasD0CblkCntAdvise);
    }

    [Fact]
    public void ValidateAdvise_PartialRefBit_IsAccepted()
    {
        var entry = new ZErofsLclusterIndex(
            (ushort)(ZErofsLclusterIndex.LiPartialRef | ZErofsLclusterIndex.LclusterTypeHead1), 0, 0);

        entry.ValidateAdvise();

        Assert.True(entry.IsPartial);
        Assert.True(entry.IsHead1);
    }

    [Fact]
    public void CompressedBlockCount_Delta0WithMarker_ReturnsBlockCount()
    {
        Assert.Equal(3, ZErofsLclusterIndex.CompressedBlockCount((ushort)(ZErofsLclusterIndex.LiD0CblkCnt | 3)));
        Assert.Equal(0, ZErofsLclusterIndex.CompressedBlockCount(7));
    }

    [Fact]
    public void DumpIndexClusters_IndexRowWithBitFourteenAdvise_ThrowsFileSystemCorruptedException()
    {
        var image = BuildFullIndexImage(out var headerOffset);
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)headerOffset + FullIndexStart));
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan((int)headerOffset + FullIndexStart), (ushort)(advise | (1 << 14)));

        using var parser = new ErofsParser(image);

        Assert.Throws<FileSystemCorruptedException>(() => parser.DumpIndexClusters("data.bin"));
    }

    [Fact]
    public void DumpIndexClusters_IndexRowWithPartialRefAdvise_Parses()
    {
        var image = BuildFullIndexImage(out var headerOffset);
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)headerOffset + FullIndexStart));
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan((int)headerOffset + FullIndexStart), (ushort)(advise | ZErofsLclusterIndex.LiPartialRef));

        using var parser = new ErofsParser(image);

        Assert.NotEmpty(parser.DumpIndexClusters("data.bin"));
    }

    [Fact]
    public void DumpIndexClusters_NonHeadDelta0WithCblkCntMarker_ConvertsDeltaToHeadDistance()
    {
        var image = BuildBigPclusterImage(out var headerOffset);
        int nonHead;
        using (var probe = new ErofsParser(image))
        {
            var rows = probe.DumpIndexClusters("data.bin");
            nonHead = rows.FindIndex(row => row.Type == ZErofsLclusterIndex.LclusterTypeNonHead);
        }

        Assert.True(nonHead >= 0, "the fixture produced no NONHEAD full index row");
        var delta0Offset = (int)headerOffset + FullIndexStart + nonHead * IndexRowSize + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(delta0Offset), (ushort)(ZErofsLclusterIndex.LiD0CblkCnt | 3));

        using var parser = new ErofsParser(image);
        var patched = parser.DumpIndexClusters("data.bin");

        Assert.Equal(ZErofsLclusterIndex.LclusterTypeNonHead, patched[nonHead].Type);
        Assert.Equal(1u, patched[nonHead].Delta0);
    }

    [Fact]
    public void ReadRebuildOptions_FullIndexInodeWithThirtyTwoKiBLogicalCluster_IsAccepted()
    {
        using var source = new TempDirectory();
        source.Write("data.bin", string.Concat(Enumerable.Repeat("UotanStudio EROFS 32 KiB logical cluster. ", 2000)));
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = 32768,
            Compress = true,
            UseCompactedIndexes = false,
            LegacyCompatibility = true,
            SuperblockChecksum = false,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });

        using var parser = new ErofsParser(image);
        var options = parser.ReadRebuildOptions();

        var inodes = options.SourceInodes;
        Assert.NotNull(inodes);
        Assert.Contains(
            inodes!.Values,
            inode => inode.SourceCompressed && inode.DataLayout != ErofsDataLayout.CompactIndexes && inode.LogicalClusterSize == 32768);
    }

    [Fact]
    public void ReadRebuildOptions_CompactedIndexInodeAboveFourteenBits_ThrowsCompactLimit()
    {
        using var source = new TempDirectory();
        source.Write("data.bin", string.Concat(Enumerable.Repeat("UotanStudio EROFS compacted cluster. ", 2000)));
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = 16384,
            Compress = true,
            UseCompactedIndexes = true,
            SuperblockChecksum = false,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });

        long headerOffset;
        using (var probe = new ErofsParser(image))
        {
            headerOffset = probe.CompressedMapHeaderOffset("data.bin");
        }

        image[(int)headerOffset + 7] = (byte)((image[(int)headerOffset + 7] & 0x80) | 1);

        using var parser = new ErofsParser(image);
        var exception = Assert.Throws<NotSupportedException>(parser.ReadRebuildOptions);
        Assert.Contains("compacted", exception.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildFullIndexImage(out long headerOffset)
    {
        using var source = new TempDirectory();
        source.Write("data.bin", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400)));
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            UseCompactedIndexes = false,
            SuperblockChecksum = false,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });
        using var parser = new ErofsParser(image);
        headerOffset = parser.CompressedMapHeaderOffset("data.bin");
        return image;
    }

    private static byte[] BuildBigPclusterImage(out long headerOffset)
    {
        using var source = new TempDirectory();
        var tile = new byte[128];
        new Random(20261011).NextBytes(tile);
        var payload = new byte[16 * BlockSize];
        for (var offset = 0; offset < payload.Length; offset += tile.Length)
        {
            tile.AsSpan(0, Math.Min(tile.Length, payload.Length - offset)).CopyTo(payload.AsSpan(offset));
        }

        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            UseCompactedIndexes = false,
            SuperblockChecksum = false,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 2 * BlockSize,
        });
        using var parser = new ErofsParser(image);
        headerOffset = parser.CompressedMapHeaderOffset("data.bin");
        return image;
    }
}