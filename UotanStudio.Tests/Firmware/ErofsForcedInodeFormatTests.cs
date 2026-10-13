using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the forced inode shapes behind <c>mkfs.erofs -E force-inode-compact|force-inode-extended</c> and
/// <c>force-inode-blockmap|force-chunk-indexes</c>: the requested record width or chunk index form is emitted
/// as asked, and a request the metadata or the assigned addresses cannot express fails loudly instead of
/// silently producing the other form, because the option names a layout the image then reports.
/// <para>守护 <c>mkfs.erofs -E force-inode-compact|force-inode-extended</c> 与
/// <c>force-inode-blockmap|force-chunk-indexes</c> 背后的强制 inode 形态：请求的记录宽度或 chunk 索引形态按
/// 要求写出，而元数据或已分配地址无法表达的请求会显式失败而非静默产出另一种形态，因为该选项命名的是镜像随后
/// 报告的布局。</para>
/// </summary>
public sealed class ErofsForcedInodeFormatTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_ForceInodeCompact_ProducesCompactRecords()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 3 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.InodeFormat = ErofsInodeFormat.Compact;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(record.IsExtended);
        Assert.Equal(ErofsInode.CompactSize, record.OnDiskSize);
        Assert.Equal(3 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_ForceInodeExtended_ProducesExtendedRecords()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 3 * BlockSize).ToArray());
        var compactOptions = PinnedOptions();
        compactOptions.InodeFormat = ErofsInodeFormat.Compact;
        var extendedOptions = PinnedOptions();
        extendedOptions.InodeFormat = ErofsInodeFormat.Extended;

        var compact = new ErofsBuilder().Build(source.Path, compactOptions);
        var extended = new ErofsBuilder().Build(source.Path, extendedOptions);

        using var parser = new ErofsParser(extended);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(record.IsExtended);
        Assert.Equal(ErofsInode.ExtendedSize, record.OnDiskSize);
        using var compactParser = new ErofsParser(compact);
        var compactRecord = compactParser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(compactRecord.IsExtended);
        Assert.Equal(ErofsInode.CompactSize, compactRecord.OnDiskSize);
    }

    [Fact]
    public void Build_ForceInodeCompactWithWideOwnership_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.InodeFormat = ErofsInodeFormat.Compact;
        options.FsConfig = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
        {
            ["data.bin"] = new FsConfigEntry(70_000, 70_001, 0x81A4),
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("compact inodes were forced", exception.Message, StringComparison.Ordinal);
        Assert.Contains("uid 70000", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ForceInodeCompactWithExtendedSourceRecord_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.InodeFormat = ErofsInodeFormat.Compact;
        var record = SourceRecord("data.bin", BlockSize);
        options.Features = ErofsFeatureSet.Auto.With(ErofsFeature.ChunkedFile);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("recorded an extended inode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ForceChunkIndexes_ProducesWideChunkEntries()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[3 * BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.Indexes;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(record.UsesChunkIndexes);
        Assert.Equal(3, record.ChunkIndex!.Length);
    }

    [Fact]
    public void Build_ForceBlockMap_ProducesBlockMapEntries()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[3 * BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.BlockMap;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var record = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.False(record.UsesChunkIndexes);
        Assert.Equal(3, record.ChunkIndex!.Length);
        Assert.Equal(3 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_ForceBlockMapWithBlobDevice_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.BlockMap;
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 4 * BlockSize)];
        options.BlobDeviceIds = [1];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("4-byte chunk block maps were forced", exception.Message, StringComparison.Ordinal);
        Assert.Contains("device 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ForceBlockMapWithSourceWideChunkForm_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[2 * BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.BlockMap;
        var record = SourceRecord("data.bin", 2 * BlockSize, ErofsInode.ChunkFormatIndexes);
        record.ChunkIndex = [4UL, 5UL];
        ((ErofsChunkIdata)record.Idata).ChunkFormat = ErofsInode.ChunkFormatIndexes;
        options.Features = ErofsFeatureSet.Auto.With(ErofsFeature.ChunkedFile);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("4-byte chunk block maps were forced", exception.Message, StringComparison.Ordinal);
        Assert.Contains("device-shaped layout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LegacyCompatibility_ClearsForcedFormats()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[3 * BlockSize]);
        var options = PinnedOptions();
        options.LegacyCompatibility = true;
        options.InodeFormat = ErofsInodeFormat.Extended;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.Indexes;

        options.Validate();

        Assert.Equal(ErofsInodeFormat.Auto, options.InodeFormat);
        Assert.Equal(ErofsChunkIndexKind.Auto, options.ChunkIndexKind);
    }

    [Fact]
    public void PreservedRebuild_CompactSourceForcedExtended_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.InodeFormat = ErofsInodeFormat.Extended;
        var record = FlatSourceRecord("data.bin", BlockSize);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("preserved inode 'data.bin'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("recorded an extended=False inode record", exception.Message, StringComparison.Ordinal);
        Assert.Contains("forced layout emits an extended=True", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedRebuild_BlockMapChunkSourceForcedIndexes_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[2 * BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.Indexes;
        var record = ChunkSourceRecord("data.bin", 2 * BlockSize);
        record.ChunkIndex = [4UL, 5UL];
        options.Features = ErofsFeatureSet.Auto.With(ErofsFeature.ChunkedFile);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("preserved inode 'data.bin'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("recorded 8-byte chunk index entries=False", exception.Message, StringComparison.Ordinal);
        Assert.Contains("forced layout emits 8-byte chunk index entries=True", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedRebuild_ChunkBasedSourceWithoutPlannedChunkIndex_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", []);
        var options = PinnedOptions();
        options.Compress = false;
        options.ChunkSize = BlockSize;
        var record = ChunkSourceRecord("data.bin", 0);
        options.Features = ErofsFeatureSet.Auto.With(ErofsFeature.ChunkedFile);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("preserved inode 'data.bin'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("recorded a chunk-based layout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("without a chunk index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedRebuild_AutoInodeFormatWithExtendedTimestamp_KeepsWidenedRecord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.Compress = false;
        var record = FlatSourceRecord("data.bin", BlockSize);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var written = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(written.IsExtended);
        Assert.Equal(BlockSize, parser.ReadFileBytes("data.bin").Length);
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

    private static ErofsInode FlatSourceRecord(string path, int size)
        => new()
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

    private static ErofsInode ChunkSourceRecord(string path, int size)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.ChunkBased,
            Size = (ulong)size,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourcePath = path,
            SourceCompressed = false,
            IFormat = 0,
        };
        record.Idata = new ErofsChunkIdata { ChunkFormat = 0 };
        record.ChunkIndex = [];
        return record;
    }

    private static ErofsInode SourceRecord(string path, int size, int chunkFormat = 0)
    {
        var chunkBased = chunkFormat > 0;
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = chunkBased ? ErofsDataLayout.ChunkBased : ErofsDataLayout.CompactIndexes,
            Size = (ulong)size,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourcePath = path,
            SourceCompressed = !chunkBased,
            RequiresExtendedTime = true,
            IFormat = ErofsInode.FlagExtended,
        };
        if (chunkBased)
        {
            record.Idata = new ErofsChunkIdata();
            ((ErofsChunkIdata)record.Idata).ChunkFormat = (ushort)chunkFormat;
        }
        else
        {
            record.Idata = new ErofsCompressedIdata { CompactedIndexes = true };
        }

        record.LogicalClusterSize = BlockSize;
        return record;
    }
}