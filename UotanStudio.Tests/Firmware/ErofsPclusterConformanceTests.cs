using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the EROFS pcluster plan against the rules a conformant reader applies to the emitted index instead of
/// the rules the writer assumed. Each extent is re-derived from the on-disk logical-cluster rows: the head's
/// <c>di_clusterofs</c> gives the logical start, the
/// non-head <c>li_delta[1]</c> chain gives the logical length the index credits to the frame, and the physical
/// length is the block count a reader can name. The named region is then stripped of its leading zero padding and
/// decoded with the credited length as the destination size, which is the exact check that produced
/// <c>failed to full decompress</c> in <c>fsck.erofs --extract</c> for rebuilt images that our own parser had no
/// complaint about.
/// <para>按合规读取方对已发出索引所使用的规则守护 EROFS pcluster 规划，而不是按写入方自认为的规则。
/// 每个 extent 都从盘上逻辑簇索引行重新推导：头簇的
/// <c>di_clusterofs</c> 给出逻辑起点，非头簇的 <c>li_delta[1]</c> 链给出索引记给该帧的逻辑长度，物理长度则为
/// 读取方能够表明的块数。随后剥掉该块区的前导零填充，并以该逻辑长度作为目标缓冲大小做解压——这一步正是让
/// 托管解析器毫无意见、却被 <c>fsck.erofs --extract</c> 报为 <c>failed to full decompress</c> 的检查。</para>
/// </summary>
public sealed class ErofsPclusterConformanceTests
{
    private const int BlockSize = 4096;
    private const byte PlainType = 0;
    private const byte NonHeadType = 2;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_LegacyDensePclusters_FrameFitsOneBlockAndDecodesCreditedSpan(bool compacted)
    {
        var payload = CompressiblePayload(1024 * 1024 + 1237);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var extents = Plan(source.Path, payload, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            OptimizeCompression = true,
            UseCompactedIndexes = compacted,
        }, out var image);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, ErofsSuperblock.FromImage(image).FeatureIncompat);
        Assert.True(extents.Count(extent => extent.CoveredClusters > 1) > 0,
            "the legacy plan lost byte-granular dense packing, so no pcluster covers more than one logical cluster.");
        Assert.All(extents, extent => Assert.Equal(1, ReaderNamedBlocks(extent, image, payload, 1)));
        Assert.All(Pairs(extents), pair => Assert.Equal(1, pair.Next.BlockAddress - pair.Previous.BlockAddress));
        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    [Fact]
    public void Build_BigPclusterDense_ClampedToDeclaredPclusterSize()
    {
        var payload = TextLikePayload(1024 * 1024 + 401);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var extents = Plan(source.Path, payload, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            OptimizeCompression = true,
            MaxPclusterSize = 64 * 1024,
            UseCompactedIndexes = false,
            Features = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, ErofsSuperblock.IncompatLz4_0Padding)
                .With(ErofsFeature.CompressionConfigs),
        }, out var image);

        Assert.Equal(0x00000003u, ErofsSuperblock.FromImage(image).FeatureIncompat);
        Assert.All(extents, extent => Assert.True(extent.LogicalLength <= 64 * 1024,
            $"the index credits {extent.LogicalLength} logical bytes at offset {extent.Start}, over the declared pcluster size."));
        Assert.True(extents.Any(extent => ReaderNamedBlocks(extent, image, payload, 16) > 1),
            $"the declared big-pcluster shape never emitted a frame above one block: {Describe(extents)}.");
        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    [Fact]
    public void Build_FirstClusterIncompressibleAboveOneBlock_ExtendsThePclusterInsteadOfLosingTheBlockCountRow()
    {
        var payload = Concat(RandomBytes(BlockSize), CompressiblePayload(60 * 1024));
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var extents = Plan(source.Path, payload, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 2 * BlockSize,
            UseCompactedIndexes = false,
        }, out var image);

        Assert.True(extents[0].Plain || extents[0].CoveredClusters > 1,
            $"the incompressible first cluster became a {ReaderNamedBlocks(extents[0], image, payload, 2)}-block frame "
            + "while one logical cluster credits it, so no index row can carry its block count.");
        Assert.True(extents.Any(extent => !extent.Plain),
            $"the whole payload went plain once the first cluster was refused: {Describe(extents)}.");
        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    [Fact]
    public void Build_UndeclaredBigPclusterWithUncompressibleClusters_PinsEveryFrameToOneBlock()
    {
        var payload = Concat(RandomBytes(4 * BlockSize), CompressiblePayload(64 * 1024));
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var extents = Plan(source.Path, payload, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Compress = true,
            UseCompactedIndexes = false,
            Features = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, ErofsSuperblock.IncompatLz4_0Padding),
        }, out var image);

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);
        Assert.True(extents.Any(extent => !extent.Plain && extent.CoveredClusters > 1),
            "the undeclared-big-pcluster plan dropped every multi-cluster pcluster, so a one-block frame was never credited a run.");
        Assert.All(extents, extent => Assert.Equal(1, ReaderNamedBlocks(extent, image, payload, 4)));
        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    /// <summary>Guards the row a conformant reader needs when a file's data ends inside its final logical
    /// cluster. <c>z_erofs_map_blocks_iter</c> keeps <c>end = (lcn + 1) &lt;&lt; lclusterbits</c> for the
    /// cluster a lookup starts in and <c>z_erofs_extent_lookback</c> only rewinds the start, so a non-head row
    /// left at that cluster would demand the whole cluster from the frame and
    /// <c>z_erofs_lz4_decompress</c> answers <c>-EIO</c> whenever the stream produces less. A conformant writer closes
    /// the index with a trailing plain row whose cluster offset is the residual end offset, so the extent is
    /// credited exactly the bytes it encodes.
    /// <para>守护文件数据结束于末尾逻辑簇内时合规读取方所需的那一行。5.10 的 <c>z_erofs_map_blocks_iter</c> 对查找起点的
    /// 簇保持 <c>end = (lcn + 1) &lt;&lt; lclusterbits</c>，而 <c>z_erofs_extent_lookback</c> 只回退起点，因此若该簇上留着
    /// 非头行，它就会要求帧产出整个簇，而 <c>z_erofs_lz4_decompress</c> 在流产出不足时返回 <c>-EIO</c>。合规写入方用尾部 plain 行收尾索引，其簇偏移等于剩余结束偏移，使该 extent 被记为正它编码的字节数。</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_PartialTailCluster_IsClosedByARowAtTheResidualOffset(bool compacted)
    {
        var payload = TextLikePayload(BlockSize + 46);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Compress = true,
            MaxPclusterSize = 4 * BlockSize,
            UseCompactedIndexes = compacted,
        });

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        var rows = parser.DumpIndexClusters("data.bin");
        var closing = rows[^1];
        Assert.Equal((long)payload.Length / BlockSize, closing.Lcn);
        Assert.NotEqual(NonHeadType, closing.Type);
        Assert.Equal((int)(payload.Length % BlockSize), closing.ClusterOfs);
    }

    [Fact]
    public void Build_UndeclaredBigPcluster_CreditedSpanDecodesFromTheOneNamedBlock()
    {
        var payload = LineCounterPayload(256 * 1024);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Compress = true,
            OptimizeCompression = false,
            UseCompactedIndexes = false,
            Features = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, ErofsSuperblock.IncompatLz4_0Padding),
        });
        using var parser = new ErofsParser(image);
        var extents = Extents(parser, "data.bin", payload.LongLength, BlockSize);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, ErofsSuperblock.FromImage(image).FeatureIncompat);
        Assert.True(extents.Any(extent => !extent.Plain && extent.CoveredClusters > 1),
            $"the cluster planner credited no multi-cluster pcluster, so a one-block frame never had to decode a run: {Describe(extents)}.");
        Assert.True(extents.Any(extent => !extent.Plain && extent.CoveredClusters is > 1 and < 64),
            "every accepted pcluster reached the planner's 64-cluster cap, so the probe search never rejected a longer span after accepting a shorter one.");
        AssertCoverage(extents, payload.LongLength);
        AssertSingleNamedBlockFrames(image, payload, extents);
    }

    /// <summary>Applies the reader's own derivation to every pcluster of a shape without the big-pcluster bit:
    /// <c>z_erofs_get_extent_compressedlen</c> names exactly one filesystem block, the leading zero bytes of that
    /// block are the frame's padding, and what remains has to decode to precisely the span the index rows credit.
    /// <para>把读取方自身的推导应用到未声明 big pcluster 位的镜像中的每个 pcluster：
    /// <c>z_erofs_get_extent_compressedlen</c> 只会表明一个文件系统块，该块的前导零字节是帧的填充，
    /// 剩下的字节必须正好解码成索引行记给该帧的跨度。</para></summary>
    private static void AssertSingleNamedBlockFrames(byte[] image, byte[] payload, List<Extent> extents)
    {
        foreach (var extent in extents)
        {
            var start = checked((int)(extent.BlockAddress * BlockSize));
            Assert.True(start + BlockSize <= image.Length,
                $"the index points a pcluster at block {extent.BlockAddress}, whose single named block runs past the image end.");
            var expected = payload.AsSpan((int)extent.Start, extent.LogicalLength);
            if (extent.Plain)
            {
                Assert.True(expected.SequenceEqual(image.AsSpan(start, extent.LogicalLength)),
                    $"the plain pcluster at block {extent.BlockAddress} does not store the {extent.LogicalLength} bytes credited at {extent.Start}.");
                continue;
            }

            var margin = 0;
            while (margin < BlockSize && image[start + margin] == 0)
            {
                margin++;
            }

            var decoded = new byte[extent.LogicalLength + BlockSize];
            var written = ErofsCompression.Decompress(image, start + margin, BlockSize - margin, decoded, 0, decoded.Length);
            Assert.True(written == extent.LogicalLength,
                $"stripping {margin} leading zero bytes from the one block a reader names at block {extent.BlockAddress} leaves a frame that decodes to {written} bytes, not the {extent.LogicalLength} bytes the index credits at {extent.Start}, so the stored frame encodes a different span than the accepted one.");
            Assert.True(expected.SequenceEqual(decoded.AsSpan(0, Math.Max(written, 0))),
                $"the frame at block {extent.BlockAddress} decodes bytes that are not the payload credited at {extent.Start}.");
        }
    }

    /// <summary>
    /// Guards the two rules that keep a planned pcluster from ever occupying more physical blocks than the
    /// logical bytes it credits, both taken from <c>lib/compress.c</c>. Line 602-614 hands the encoder a whole
    /// pcluster of output and takes whatever input it consumed, so the accepted span is byte-granular and is
    /// not bounded by the logical cluster size; line 616-628 keeps a frame only when its block-rounded length
    /// is strictly smaller than that span, so a span whose encoding rounds up to its own length is stored raw
    /// one block at a time instead of being padded into a larger pcluster.
    /// <para>守护两条规则：它们使已规划的 pcluster 绝不占用比它所记逻辑字节更多的物理块，两条都取自
    /// <c>lib/compress.c</c>。第 602-614 行把一整个 pcluster 的输出交给编码器并采纳它消耗的输入，因此被接受的
    /// 跨度是字节粒度的、不受逻辑簇大小约束；第 616-628 行只在取整后长度严格小于该跨度时保留压缩帧，因此编码长度
    /// 向上取整后等于自身长度的跨度会按一块一段原样存放，而不会被超配进更大的 pcluster。</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_BigPclusterFillDestinationTake_NeverStoresMoreBlocksThanTheSpanItCredits(bool compacted)
    {
        var payload = TextLikePayload(1024 * 1024 + 1237);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var extents = Plan(source.Path, payload, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 16 * BlockSize,
            UseCompactedIndexes = compacted,
        }, out var image);

        Assert.True(extents.Any(extent => !extent.Plain && extent.LogicalLength % BlockSize != 0),
            $"every accepted span landed on a logical cluster boundary, so the take is still cluster-granular: {Describe(extents)}.");
        foreach (var extent in extents)
        {
            if (extent.Plain)
            {
                continue;
            }

            var named = ReaderNamedBlocks(extent, image, payload, 16);
            Assert.True(named > 0,
                $"no block count a reader can name decodes the {extent.LogicalLength} bytes credited at {extent.Start}.");
            Assert.True((long)named * BlockSize < extent.LogicalLength,
                $"the frame at block {extent.BlockAddress} occupies {named} block(s) for the {extent.LogicalLength} bytes "
                + "credited at its start, so the pcluster stores more physical bytes than the span it covers.");
        }

        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    /// <summary>
    /// Guards the closing row under the byte-granular planner. A file whose data ends inside its final logical
    /// cluster is closed by the trailing plain row of <c>z_erofs_fini_full_indexes</c>
    /// (<c>lib/compress.c</c>:144-158): its cluster offset is the residual end offset and its block address is
    /// zero, which is what makes the extent before it be credited exactly the bytes it encodes instead of a
    /// whole final cluster.
    /// <para>守护字节粒度规划器下的收尾行。数据结束于末尾逻辑簇内的文件由 <c>z_erofs_fini_full_indexes</c>
    /// 的尾部 plain 行收尾（<c>lib/compress.c</c>:144-158）：其簇偏移是剩余结束偏移、块地址为零，这正是让前一个
    /// extent 被记为正它编码的字节数而不是整个末尾簇的原因。</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_ByteGranularTail_ClosingRowCreditsOnlyTheBytesTheFrameEncodes(bool compacted)
    {
        var payload = TextLikePayload(8 * BlockSize + 1301);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 16 * BlockSize,
            UseCompactedIndexes = compacted,
        });

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        var rows = parser.DumpIndexClusters("data.bin");
        var closing = rows[^1];
        Assert.Equal((long)payload.Length / BlockSize, closing.Lcn);
        Assert.Equal(PlainType, closing.Type);
        Assert.Equal((int)(payload.Length % BlockSize), closing.ClusterOfs);
        if (!compacted)
        {
            Assert.Equal(0, closing.Pblk);
        }

        var extents = Extents(parser, "data.bin", payload.LongLength, BlockSize);
        Assert.True(extents.Count == 1 && !extents[0].Plain && extents[0].LogicalLength == payload.Length,
            $"the payload was not planned as one compressed extent closed by that row: {Describe(extents)}");
        AssertCoverage(extents, payload.LongLength);
        AssertFrames(image, payload, extents);
    }

    private static List<Extent> Plan(string tree, byte[] payload, ErofsOptions options, out byte[] image)
    {
        image = new ErofsBuilder().Build(tree, options);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        return Extents(parser, "data.bin", payload.LongLength, BlockSize);
    }

    /// <summary>Derives the block count a conformant reader can name for one extent: one filesystem block unless a
    /// second logical cluster carries a block count, which is what
    /// <c>z_erofs_get_extent_compressedlen</c> does for big pclusters only.
    /// <para>推导合规读取方能够为一个 extent 表明的块数：除非有第二个逻辑簇携带块数，否则恒为一个文件系统块，
    /// 这正是 <c>z_erofs_get_extent_compressedlen</c> 只对 big pcluster 采用的做法。</para></summary>
    private static int ReaderNamedBlocks(Extent extent, byte[] image, byte[] payload, int maxBlocks)
    {
        for (var blocks = 1; blocks <= maxBlocks; blocks++)
        {
            if (Decodes(extent, image, payload, blocks)) return blocks;
        }

        return 0;
    }

    private static bool Decodes(Extent extent, byte[] image, byte[] payload, int blocks)
    {
        var start = checked((int)(extent.BlockAddress * BlockSize));
        var region = blocks * BlockSize;
        if (start + region > image.Length) return false;
        var expected = payload.AsSpan((int)extent.Start, extent.LogicalLength);
        if (extent.Plain)
        {
            return blocks == 1 && expected.SequenceEqual(image.AsSpan(start, extent.LogicalLength));
        }

        var margin = 0;
        while (margin < region && image[start + margin] == 0) margin++;
        if (margin >= region) return false;
        var decoded = new byte[extent.LogicalLength];
        var written = ErofsCompression.Decompress(image, start + margin, region - margin, decoded, 0, decoded.Length);
        return written == extent.LogicalLength && expected.SequenceEqual(decoded);
    }

    private static void AssertFrames(byte[] image, byte[] payload, List<Extent> extents)
    {
        for (var i = 0; i < extents.Count; i++)
        {
            var extent = extents[i];
            var named = ReaderNamedBlocks(extent, image, payload, ErofsCompression.MaxPclusterSize / BlockSize);
            Assert.True(named > 0,
                $"no block count a reader could name makes the region at block {extent.BlockAddress} decode the {extent.LogicalLength} bytes credited at {extent.Start}, so the frame is padded on the wrong side or encodes a different span.");
            Assert.True(named == 1 || extent.CoveredClusters > 1,
                $"a {named}-block frame at block {extent.BlockAddress} covers one logical cluster, so no row can carry its block count.");
            if (i + 1 < extents.Count)
            {
                Assert.Equal((long)named, extents[i + 1].BlockAddress - extent.BlockAddress);
            }
        }
    }

    private static void AssertCoverage(List<Extent> extents, long fileSize)
    {
        var previousEnd = 0L;
        foreach (var extent in extents)
        {
            Assert.Equal(previousEnd, extent.Start);
            Assert.True(extent.ClusterOfs < BlockSize,
                $"head row at cluster {extent.Start / BlockSize} stores di_clusterofs {extent.ClusterOfs} over the cluster size.");
            previousEnd = extent.Start + extent.LogicalLength;
        }

        Assert.Equal(fileSize, previousEnd);
    }

    private static string Describe(List<Extent> extents)
        => string.Join("; ", extents.Take(4).Select(extent =>
            $"start={extent.Start} len={extent.LogicalLength} covered={extent.CoveredClusters} plain={extent.Plain} pblk={extent.BlockAddress}"));

    private static List<(Extent Previous, Extent Next)> Pairs(List<Extent> extents)
        => [.. extents.Skip(1).Select((extent, index) => (extents[index], extent))];

    private static List<Extent> Extents(ErofsParser parser, string path, long fileSize, int clusterSize)
    {
        var rows = parser.DumpIndexClusters(path);
        var extents = new List<Extent>();
        for (var lcn = 0; lcn < rows.Count; lcn++)
        {
            var row = rows[lcn];
            if (row.Type == NonHeadType || row.Pblk == 0) continue;
            var start = lcn * (long)clusterSize + row.ClusterOfs;
            if (start >= fileSize) continue;
            var end = CreditedEnd(rows, lcn, fileSize, clusterSize);
            extents.Add(new Extent(start, checked((int)(end - start)), row.Pblk, row.ClusterOfs,
                row.Type == PlainType, (end + clusterSize - 1) / clusterSize - start / clusterSize));
        }

        return extents;
    }

    private static long CreditedEnd(List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> rows,
        int headLcn, long fileSize, int clusterSize)
    {
        var lcn = headLcn;
        while (lcn * (long)clusterSize < fileSize)
        {
            if (lcn >= rows.Count) return fileSize;
            var row = rows[lcn];
            if (row.Type != NonHeadType)
            {
                if (lcn != headLcn) return lcn * (long)clusterSize + row.ClusterOfs;
                lcn++;
                continue;
            }

            lcn += row.Delta1 == 0 ? 1 : checked((int)row.Delta1);
        }

        return fileSize;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var bytes = new byte[first.Length + second.Length];
        first.CopyTo(bytes, 0);
        second.CopyTo(bytes, first.Length);
        return bytes;
    }

    private static byte[] CompressiblePayload(int length)
    {
        var tile = new byte[256];
        for (var i = 0; i < tile.Length; i++) tile[i] = (byte)(i * 7 + 3);
        var bytes = new byte[length];
        for (var offset = 0; offset < length; offset += tile.Length)
            tile.AsSpan(0, Math.Min(tile.Length, length - offset)).CopyTo(bytes.AsSpan(offset));
        return bytes;
    }

    private static byte[] LineCounterPayload(int length)
    {
        var builder = new StringBuilder(length + 64);
        for (var line = 0; builder.Length < length; line++)
        {
            builder.Append("erofs pcluster ").Append(line).Append(" lcluster ").Append(line).Append(" kernel\n");
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static byte[] TextLikePayload(int length)
    {
        string[] words = ["erofs", "pcluster", "clusterofs", "lcluster", "di_size", "kernel", "decoder", "superblock"];
        var random = new Random(20261010);
        var builder = new StringBuilder(length + 32);
        while (builder.Length < length)
            builder.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(20261010).NextBytes(bytes);
        return bytes;
    }

    private sealed record Extent(long Start, int LogicalLength, long BlockAddress, int ClusterOfs, bool Plain,
        long CoveredClusters);
}