using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the EROFS index shape a kernel without the <c>z_erofs_get_extent_compressedlen</c> refinement
/// can resolve: while an inode declares big pclusters,
/// <c>z_erofs_get_extent_compressedlen</c> reads the row after every compressed head row to learn the
/// pcluster's physical length, so a head row on the file's last logical cluster makes
/// <c>compacted_load_cluster_from_disk</c> answer <c>-EINVAL</c> (or read past the index) and the tail
/// page fails in <c>z_erofs_read_folio</c>. The builder therefore keeps the terminal single-cluster
/// pcluster uncompressed, which keeps a final chunk of at most one block uncompressed, and the stored
/// bytes plus the one-block physical length stay self-evident from the head row.
/// <para>守护没有经过 <c>z_erofs_get_extent_compressedlen</c> 精修的内核能够解析的 EROFS 索引布局：只要 inode
/// 声明 big pcluster，<c>z_erofs_get_extent_compressedlen</c> 就会读取压缩 head 行之后的那一行来取得 pcluster
/// 的物理长度，于是落在文件最后一个逻辑簇上的 head 行会让 <c>compacted_load_cluster_from_disk</c> 返回
/// <c>-EINVAL</c>（或读到索引之外的字节），尾页在 <c>z_erofs_read_folio</c> 中失败。构建器因此把末尾那个
/// 单簇 pcluster 保持为不压缩，使至多一个块的尾部片段不压缩，
/// 存放的字节与一个块的物理长度都能由 head 行自身说明。</para>
/// </summary>
public sealed class ErofsTerminalPclusterTests
{
    private const int BlockSize = 4096;
    private const int CheapClusters = 64;
    private const int TotalClusters = 65;
    private const byte PlainType = (byte)ZErofsLclusterIndex.LclusterTypePlain;
    private const byte HeadType = (byte)ZErofsLclusterIndex.LclusterTypeHead1;
    private const byte Head2Type = (byte)ZErofsLclusterIndex.LclusterTypeHead2;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_BigPclusterFileEndingWithOneLogicalCluster_StoresTheTerminalClusterUncompressed(bool dense)
    {
        var payload = TerminalClusterPayload();
        Assert.Equal(TotalClusters * BlockSize, payload.Length);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 2 * BlockSize,
            OptimizeCompression = dense,
        };

        var image = new ErofsBuilder().Build(source.Path, options);
        using var parser = new ErofsParser(image);
        var rows = parser.DumpIndexClusters("data.bin");

        Assert.Equal(TotalClusters, rows.Count);
        Assert.True(parser.MaxCompressedPclusterBytes() > BlockSize,
            $"the fixture produced no multi-block frame, so the big-pcluster advise that triggers the kernel probe stayed untested (dense={dense}).");
        var last = rows[^1];
        Assert.Equal(0, last.ClusterOfs);
        Assert.Equal(TotalClusters - 1, last.Lcn);
        Assert.NotEqual(HeadType, last.Type);
        Assert.Equal(PlainType, last.Type);
        Assert.Equal(payload.AsSpan((TotalClusters - 1) * BlockSize, BlockSize),
            image.AsSpan((int)(last.Pblk * BlockSize), BlockSize));
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_AdvisedAndUnadvisedTails_NoProbedHeadRowOccupiesTheLastIndexRow(bool compacted)
    {
        var aligned = TerminalClusterPayload();
        var partial = TerminalClusterPayload(1500);
        var single = Repeating(BlockSize);
        using var source = new TempDirectory();
        source.WriteBytes("aligned.bin", aligned);
        source.WriteBytes("partial.bin", partial);
        source.WriteBytes("single.bin", single);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 2 * BlockSize,
            UseCompactedIndexes = compacted,
        });
        using var parser = new ErofsParser(image);

        var payloads = new[] { ("aligned.bin", aligned), ("partial.bin", partial), ("single.bin", single) };
        var probedHeads = 0;
        foreach (var (path, payload) in payloads)
        {
            var header = ZErofsMapHeader.FromBytes(
                image.AsSpan((int)parser.CompressedMapHeaderOffset(path), ZErofsMapHeader.Size));
            var rows = parser.DumpIndexClusters(path);
            Assert.Equal((payload.Length + BlockSize - 1) / BlockSize, rows.Count);
            for (var index = 0; index < rows.Count; index++)
            {
                var licensed = rows[index].Type == HeadType && header.HasBigPcluster1 ||
                    rows[index].Type == Head2Type && header.HasBigPcluster2;
                if (!licensed)
                {
                    continue;
                }

                probedHeads++;
                Assert.True(index + 1 < rows.Count,
                    $"{path} ends with a head row the kernel probes past the index (compacted={compacted}, " +
                    $"lcn={rows[index].Lcn}, rows={rows.Count}, advise=0x{header.Advise:X4}, type={rows[index].Type}).");
            }

            Assert.Equal(payload, parser.ReadFileBytes(path));
        }

        var advisedHeader = ZErofsMapHeader.FromBytes(
            image.AsSpan((int)parser.CompressedMapHeaderOffset("aligned.bin"), ZErofsMapHeader.Size));
        Assert.True(advisedHeader.HasBigPcluster1,
            $"the fixture must license the kernel probe; advise=0x{advisedHeader.Advise:X4} (compacted={compacted}).");
        Assert.Equal(PlainType, parser.DumpIndexClusters("aligned.bin")[^1].Type);
        Assert.True(probedHeads > 0, $"no head row was licensed by the emitted advise (compacted={compacted}).");
    }

    private static byte[] Repeating(int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill((byte)'u');
        return bytes;
    }

    private static byte[] TerminalClusterPayload(int tailLength = 0)
    {
        var tile = new byte[128];
        new Random(20261009).NextBytes(tile);
        var bytes = new byte[TotalClusters * BlockSize + tailLength];
        for (var offset = 0; offset < bytes.Length; offset += tile.Length)
        {
            var chunk = Math.Min(tile.Length, bytes.Length - offset);
            tile.AsSpan(0, chunk).CopyTo(bytes.AsSpan(offset, chunk));
        }

        new Random(20261010).NextBytes(bytes.AsSpan((TotalClusters - 2) * BlockSize, BlockSize));
        return bytes;
    }
}