using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the EROFS extent reader's call-scoped scratch buffers: one walk over many extents must reuse
/// the pooled encoded/decoded pair instead of renting per extent, and an extent claiming more logical
/// bytes than the decompression budget must still be rejected. The repository builder emits lcluster
/// indexes only, so these tests re-point a verified legacy LZ4 image's index area at its own pclusters
/// using the z_erofs_extent record layout the parser reads.
/// <para>守护 EROFS extent 读取器的调用作用域临时缓冲：一次遍历大量 extent 必须复用池化的压缩/解码
/// 缓冲对，而不是每个 extent 重新租借；声称的逻辑字节数超过解压预算的 extent 仍必须被拒绝。仓库的
/// 打包器只产出 lcluster 索引，因此本测试按解析器读取的 z_erofs_extent 记录布局，把已校验的 legacy
/// LZ4 镜像索引区重新指向其自身的 pcluster。</para>
/// </summary>
public sealed class ErofsExtentBufferReuseTests : IDisposable
{
    private const int BlockSize = 4096;
    private const int LclusterTypeHead1 = 1;
    private const int LclusterTypeNonHead = 2;
    private const int ExtentRecordSize = 16;
    private const uint Lz4RecordFormat = 1u;
    private const int FullIndexEntryBytes = 8;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-erofs-extent-" + Guid.NewGuid().ToString("N"));

    public ErofsExtentBufferReuseTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void WriteCompressedExtentData_MultiExtentImage_RentsScratchBuffersOncePerWalk()
    {
        const int clusters = 128;
        var payload = ClusteredPayload(clusters);
        var image = BuildImage("multi", "data.bin", payload);
        var layout = ReadLayout(image, "data.bin", payload.LongLength);

        foreach (var head in layout.Heads)
        {
            Assert.Equal(LclusterTypeHead1, head.Type);
        }
        Assert.Equal(0, layout.Heads[0].ClusterOfs);
        Assert.True(layout.Heads.Count >= 3,
            $"expected at least three pclusters so several extents are walked, got {layout.Heads.Count}.");
        Assert.True(layout.Heads.Count < layout.Rows.Count,
            $"every lcluster is a pcluster head, so extents would cover one cluster each "
            + $"({layout.Heads.Count} heads for {layout.Rows.Count} lclusters).");
        Assert.True(layout.Heads.Count * ExtentRecordSize <= layout.Rows.Count * FullIndexEntryBytes,
            $"synthetic extent index needs {layout.Heads.Count * ExtentRecordSize} bytes but the source "
            + $"lcluster area holds only {layout.Rows.Count * FullIndexEntryBytes}.");

        var patched = WriteExtentIndex(image, "multi-extents.img", layout, layout.Heads.Count);

        using var reader = new ErofsParser(patched);
        Assert.Equal(payload, reader.ReadFileBytes("data.bin"));

        using var scratch = new ErofsParser.ExtentWriteScratch();
        using var output = new MemoryStream();
        reader.WriteCompressedExtentData("data.bin", output, scratch, TestContext.Current.CancellationToken);

        Assert.Equal(payload, output.ToArray());
        Assert.True(scratch.RentCount < layout.Heads.Count,
            $"extent walk rented {scratch.RentCount} buffers for {layout.Heads.Count} extents; rentals must stay call-scoped.");
        Assert.True(scratch.RentCount <= 4,
            $"expected one rental per buffer plus rare in-place growth, got {scratch.RentCount}.");
    }

    [Fact]
    public void ReadFile_ExtentAboveDecompressionBudget_IsRejected()
    {
        const int clusters = 3328;
        var payload = ClusteredPayload(clusters);
        var image = BuildImage("oversized", "big.bin", payload);
        var layout = ReadLayout(image, "big.bin", payload.LongLength);
        Assert.True(payload.LongLength > ErofsCompression.MaxPclusterDecompressedSize,
            $"single extent must claim more logical bytes than the {ErofsCompression.MaxPclusterDecompressedSize}-byte budget.");

        var patched = WriteExtentIndex(image, "oversized-extents.img", layout, 1);

        using var reader = new ErofsParser(patched);
        var error = Assert.Throws<FileSystemCorruptedException>(() => reader.ReadFile("big.bin", Stream.Null));
        Assert.Contains("decompression budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_LargerThenSmallerRequests_GrowsOnceAndKeepsRentCount()
    {
        using var scratch = new ErofsParser.ExtentWriteScratch();
        Assert.Equal(0, scratch.RentCount);

        scratch.Ensure(4096, 8192);
        var compressedCapacity = scratch.Compressed.Length;
        var decodedCapacity = scratch.Decoded.Length;
        Assert.Equal(2, scratch.RentCount);
        Assert.True(compressedCapacity >= 4096);
        Assert.True(decodedCapacity >= 8192);

        scratch.Ensure(4096, 8192);
        Assert.Equal(2, scratch.RentCount);
        Assert.Equal(compressedCapacity, scratch.Compressed.Length);
        Assert.Equal(decodedCapacity, scratch.Decoded.Length);

        scratch.Ensure(compressedCapacity, decodedCapacity);
        Assert.Equal(2, scratch.RentCount);

        scratch.Ensure(decodedCapacity + 1, 0);
        Assert.Equal(3, scratch.RentCount);
        Assert.True(scratch.Compressed.Length > compressedCapacity);

        scratch.Release();
        Assert.Empty(scratch.Compressed);
        Assert.Empty(scratch.Decoded);
    }

    private string BuildImage(string directoryName, string fileName, byte[] payload)
    {
        var source = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, fileName), payload);
        var image = Path.Combine(_root, directoryName + ".img");
        new ErofsBuilder().Build(source, image, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            CompressedTailPacking = false,
            SuperblockChecksum = false,
            CancellationToken = TestContext.Current.CancellationToken,
        });
        return image;
    }

    private static Layout ReadLayout(string imagePath, string fileName, long fileLength)
    {
        using var parser = new ErofsParser(imagePath);
        var headerOffset = parser.CompressedMapHeaderOffset(fileName);
        var header = File.ReadAllBytes(imagePath).AsSpan((int)headerOffset, ZErofsMapHeader.Size);
        var clusterSize = BlockSize << (header[7] & ZErofsMapHeader.LclusterBitsMask);
        var rows = parser.DumpIndexClusters(fileName)
            .Select(row => new Row(row.Lcn, row.Type, row.ClusterOfs, row.Pblk))
            .ToList();
        var heads = rows.Where(row => row.Type != LclusterTypeNonHead).ToList();
        return new Layout(headerOffset, clusterSize, fileLength, rows, heads);
    }

    private string WriteExtentIndex(string imagePath, string destinationName, Layout layout, int extentCount)
    {
        var image = File.ReadAllBytes(imagePath);
        var baseOffset = (layout.HeaderOffset + ZErofsMapHeader.Size + ExtentRecordSize - 1) / ExtentRecordSize * ExtentRecordSize;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)layout.HeaderOffset, 4), (uint)extentCount);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)layout.HeaderOffset + 4, 2),
            (ushort)(ZErofsMapHeader.AdviseExtents | (2 << ZErofsMapHeader.ExtRecSizeBit)));
        image[(int)layout.HeaderOffset + 6] = 0;
        image[(int)layout.HeaderOffset + 7] = 0;

        var heads = layout.Heads.Take(extentCount).ToList();
        Assert.Equal(0, heads[0].ClusterOfs);
        for (var i = 0; i < heads.Count; i++)
        {
            var logicalStart = heads[i].Lcn * layout.ClusterSize + heads[i].ClusterOfs;
            var logicalEnd = i + 1 < heads.Count
                ? heads[i + 1].Lcn * layout.ClusterSize + heads[i + 1].ClusterOfs
                : layout.FileLength;
            Assert.True(logicalEnd > logicalStart,
                $"extent {i} claims logical bytes up to {logicalEnd} from {logicalStart}.");
            var record = image.AsSpan((int)(baseOffset + (long)i * ExtentRecordSize), ExtentRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)BlockSize | Lz4RecordFormat << ZErofsMapHeader.ExtentPlenFmtBit);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..8], (uint)(heads[i].Pblk * BlockSize));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..12], 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..16], (uint)logicalStart);
        }

        var destination = Path.Combine(_root, destinationName);
        File.WriteAllBytes(destination, image);
        return destination;
    }

    private static byte[] ClusteredPayload(int clusters)
    {
        var bytes = new byte[checked(clusters * BlockSize)];
        var rng = new Random(20261007);
        for (var cluster = 0; cluster < clusters; cluster++)
        {
            var tile = new byte[BlockSize / 4];
            rng.NextBytes(tile);
            for (var repeat = 0; repeat < 4; repeat++)
            {
                tile.CopyTo(bytes.AsSpan(cluster * BlockSize + repeat * tile.Length, tile.Length));
            }
        }

        return bytes;
    }

    public void Dispose() => Directory.Delete(_root, true);

    private readonly record struct Row(long Lcn, int Type, int ClusterOfs, long Pblk);

    private readonly record struct Layout(
        long HeaderOffset,
        int ClusterSize,
        long FileLength,
        IReadOnlyList<Row> Rows,
        IReadOnlyList<Row> Heads);
}