using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the metadata zone that <c>mkfs.erofs --MZ</c> produces, where a nonzero <c>s_meta_blkaddr</c> moves
/// every inode record, its inline body and its index tables into a separate area after the data blocks while
/// the data blocks stay at the head of the image. A preserved rebuild has to place that zone again and publish
/// its block back, otherwise the recorded inode numbers resolve to different byte offsets and the layout is
/// silently changed. The same file also pins the metabox container rule: a source whose container payload was
/// compressed cannot be reproduced by a writer that stores the container verbatim, so it fails loudly instead
/// of publishing an uncompressed container under the same recorded feature.
/// <para>守护 <c>mkfs.erofs --MZ</c> 产出的元数据区：非零的 <c>s_meta_blkaddr</c> 把每条 inode 记录、其内联体与
/// 索引表移到数据块之后的独立区域，而数据块留在镜像头部。保留式重建必须重新放置该区并发布其块号，否则记录的
/// inode 编号会解析到不同的字节偏移，布局被静默改变。本文件同时钉住 metabox 容器规则：容器负载被压缩过的源无法
/// 由逐字存放容器的写入方复现，因此必须显式失败，而不是在同一个已记录特性下发布未压缩容器。</para>
/// </summary>
public sealed class ErofsMetazoneTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_DefaultOptions_KeepsMetadataAtTheImageHead()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.Equal(0u, parser.Superblock.MetaBlkAddr);
        Assert.Equal(0u, parser.ReadRebuildOptions().SourceMetaBlkAddr);
        Assert.Equal(4 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_RecordedMetadataZone_PublishesANonzeroStartBlock()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;
        options.SourceMetaBlkAddr = 4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.NotEqual(0u, parser.Superblock.MetaBlkAddr);
        Assert.NotEqual(0u, parser.ReadRebuildOptions().SourceMetaBlkAddr);
        Assert.Equal(4 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_RecordedMetadataZone_KeepsEveryDataBlockBeforeTheZone()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        source.WriteBytes("other.bin", Enumerable.Repeat((byte)0xA5, 3 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;
        options.SourceMetaBlkAddr = 4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var zoneBlock = parser.Superblock.MetaBlkAddr;
        var records = parser.ReadRebuildOptions().SourceInodesByPath!;
        Assert.True(zoneBlock > 0, "the metadata zone has to be published");
        foreach (var record in records.Values)
        {
            if (!record.IsDirectory && record.Idata is ErofsFlatIdata { BlockAddress: not uint.MaxValue } flat)
            {
                Assert.True(flat.BlockAddress < zoneBlock,
                    $"data block {flat.BlockAddress} of '{record.SourcePath}' has to precede the zone at {zoneBlock}");
            }
        }
    }

    [Fact]
    public void Build_RecordedMetadataZone_ResolvesEveryNidIntoTheZone()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        source.WriteBytes("other.bin", Enumerable.Repeat((byte)0xA5, 3 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;
        options.SourceMetaBlkAddr = 4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var zoneStart = checked((long)parser.Superblock.MetaBlkAddr * BlockSize);
        Assert.True(zoneStart > 0);
        var entries = parser.EnumerateEntries().ToList();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var offset = parser.Superblock.GetInodeOffset(entry.Nid);
            Assert.True(offset >= zoneStart,
                $"inode '{entry.Path}' resolves to {offset}, before the metadata zone at {zoneStart}");
            Assert.True(offset < image.LongLength,
                $"inode '{entry.Path}' resolves to {offset}, past the image end {image.LongLength}");
        }
    }

    [Fact]
    public void Build_RecordedMetadataZone_ReparsesWithTheSameRecordedBlock()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;
        options.SourceMetaBlkAddr = 4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var rebuilt = parser.ReadRebuildOptions();
        Assert.Equal(parser.Superblock.MetaBlkAddr, rebuilt.SourceMetaBlkAddr);

        var second = new ErofsBuilder().Build(source.Path, rebuilt);
        using var reread = new ErofsParser(second);
        Assert.Equal(rebuilt.SourceMetaBlkAddr, reread.Superblock.MetaBlkAddr);
        Assert.Equal(4 * BlockSize, reread.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_MetaboxWithCompressedSourceContainer_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.Metabox = true;
        options.SourceMetaboxCompressed = true;
        options.Features = ErofsFeatureSet.FromWords(0, ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatMetabox);
        AttachFlatSourceRecord(options, "data.bin", BlockSize);

        var error = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("metabox container is compressed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MetaboxWithVerbatimSourceContainer_DoesNotFail()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.Metabox = true;
        options.SourceMetaboxCompressed = false;
        options.Features = ErofsFeatureSet.FromWords(0, ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatMetabox);
        AttachFlatSourceRecord(options, "data.bin", BlockSize);

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.True(parser.Superblock.HasMetabox);
        Assert.NotEqual(0UL, parser.Superblock.MetaBoxNid);
        Assert.Equal(BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_MetaboxWithMetadataZone_KeepsBothStructures()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.Metabox = true;
        options.SourceMetaBlkAddr = 4;
        options.Features = ErofsFeatureSet.FromWords(0, ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatMetabox);
        AttachFlatSourceRecord(options, "data.bin", BlockSize);

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.True(parser.Superblock.HasMetabox);
        Assert.NotEqual(0UL, parser.Superblock.MetaBoxNid);
        Assert.NotEqual(0u, parser.Superblock.MetaBlkAddr);
        var zoneStart = checked((long)parser.Superblock.MetaBlkAddr * BlockSize);
        Assert.True(parser.Superblock.GetInodeOffset(parser.Superblock.MetaBoxNid) >= zoneStart);
        Assert.Equal(BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void RebuildMetadata_CaptureAndRestore_CarriesTheMetadataZone()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 4 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.Compress = false;
        options.SourceMetaBlkAddr = 4;
        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var captured = ErofsRebuildMetadata.Capture(parser.ReadRebuildOptions());
        Assert.Equal(parser.Superblock.MetaBlkAddr, captured.MetaBlkAddr);

        var restored = ErofsRebuildMetadata.Restore(captured);
        Assert.Equal(parser.Superblock.MetaBlkAddr, restored.SourceMetaBlkAddr);
    }

    private static void AttachFlatSourceRecord(ErofsOptions options, string path, int size)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.Flat,
            Idata = new ErofsFlatIdata { BlockAddress = 0 },
            Size = (ulong)size,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourcePath = path,
            SourceCompressed = false,
            IFormat = 0,
        };
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            [path] = record,
        };
    }

    private static ErofsOptions PinnedOptions() => new()
    {
        BlockSize = BlockSize,
        Compress = true,
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        MaxPclusterSize = BlockSize,
        SuperblockChecksum = false,
        Uuid = new byte[16],
        BuildTime = PinnedBuildTime,
    };
}