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

/// <summary>
/// Guards chunk-based addressing (<c>EROFS_INODE_CHUNK_BASED</c>) and the multi-device layout behind it:
/// the <c>u</c> field's chunk format word, the 8-byte <c>erofs_inode_chunk_index</c> entries that carry a
/// device id, the <c>chunked_file</c> and <c>device_table</c> incompat bits derived from the planned
/// layout, and the rule that a payload on an extra device keeps its device-relative start blocks instead of
/// being copied into the primary image.
/// <para>守护 chunk-based 寻址（<c>EROFS_INODE_CHUNK_BASED</c>）及其背后的多设备布局：<c>u</c> 字段的
/// chunk 格式字、携带设备号的 8 字节 <c>erofs_inode_chunk_index</c> 表项、由已规划布局推导的
/// <c>chunked_file</c> 与 <c>device_table</c> incompat 位，以及「位于额外设备的负载保持其设备内相对首块而
/// 不复制进主镜像」这一规则。</para>
/// </summary>
public sealed class ErofsChunkDeviceTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Build_ChunkSizeOptionWithoutSourceInode_ProducesChunkBasedLayout()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var rebuilt = parser.ReadRebuildOptions();
        Assert.True(rebuilt.Features.Contains(ErofsFeature.ChunkedFile));
        var record = rebuilt.SourceInodesByPath!["data.bin"];
        Assert.Equal(ErofsDataLayout.ChunkBased, record.DataLayout);
        Assert.Equal(0u, record.DeviceId);
        Assert.Equal(3, record.ChunkIndex!.Length);
        Assert.False(record.UsesChunkIndexes);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_ChunkSizeBelowBlockSize_IsRejected()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize / 2;

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("power of two", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_BlobDeviceWithoutSourceChunkIndex_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 4 * BlockSize)];
        options.BlobDeviceIds = [1];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("device 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("data.bin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_BlobDeviceOutsideDeclaredDevices_IsRejected()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 4 * BlockSize)];
        options.BlobDeviceIds = [2];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("blob device index 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TwoBlobDevices_IsRejected()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ExtraDeviceCount = 2;
        options.ExtraDeviceImages =
        [
            new ErofsDeviceSpec("blob1.img", 4 * BlockSize),
            new ErofsDeviceSpec("blob2.img", 4 * BlockSize),
        ];
        options.BlobDeviceIds = [1, 2];

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("one device per image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedRebuild_ExternalDeviceChunkRecord_KeepsDeviceRelativeStartBlocks()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[2 * BlockSize]);
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 4 * BlockSize)];
        options.Features = ErofsFeatureSet.Auto
            .With(ErofsFeature.DeviceTable)
            .With(ErofsFeature.ChunkedFile);
        var record = ExternalChunkRecord("data.bin", 1, [3UL, 5UL]);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var rebuilt = parser.ReadRebuildOptions();
        Assert.True(rebuilt.Features.Contains(ErofsFeature.ChunkedFile));
        Assert.True(rebuilt.Features.Contains(ErofsFeature.DeviceTable));
        var reparsed = rebuilt.SourceInodesByPath!["data.bin"];
        Assert.Equal(ErofsDataLayout.ChunkBased, reparsed.DataLayout);
        Assert.Equal(1u, reparsed.DeviceId);
        Assert.Equal([3UL, 5UL], reparsed.ChunkIndex!);
    }

    [Fact]
    public void PreservedRebuild_ExternalDeviceUndeclared_FailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[2 * BlockSize]);
        var options = PinnedOptions();
        var record = ExternalChunkRecord("data.bin", 1, [0UL, 1UL]);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("device 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DeviceZeroEntryInsideExtraDeviceUnifiedWindow_ResolvesToThatDevice()
    {
        var payload = Enumerable.Repeat((byte)0x11, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 2 * BlockSize)];

        var image = new ErofsBuilder().Build(source.Path, options);

        ulong unifiedBase;
        long tableOffset;
        using (var parser = new ErofsParser(image))
        {
            var nid = parser.EnumerateFiles().Single(file => file.Path == "data.bin").Nid;
            tableOffset = checked((long)parser.Superblock.MetaBlkAddr * BlockSize
                + (long)nid * ErofsInode.CompactSize + ErofsInode.CompactSize);
            // s_blocks spans the whole logical filesystem, so the extra device's unified window opens at
            // the first block after the primary image: total blocks minus the blocks that device declares.
            var deviceSlotOffset = checked((long)parser.Superblock.DevtSlotoff * ErofsDeviceSlot.Size);
            var slot = ErofsDeviceSlot.FromBytes(
                image.AsSpan(checked((int)deviceSlotOffset), ErofsDeviceSlot.Size),
                parser.Superblock.Has48Bit);
            unifiedBase = parser.Superblock.BlockCount64 - slot.Blocks;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(checked((int)tableOffset), 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(checked((int)tableOffset + 2), 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(checked((int)tableOffset + 4), 4),
            checked((uint)unifiedBase + 1));

        var device = new byte[2 * BlockSize];
        Array.Fill(device, (byte)0xAB, BlockSize, BlockSize);
        var streams = new Stream[] { new MemoryStream(image, writable: false), new MemoryStream(device, writable: false) };
        using var multiDevice = new ErofsParser(streams);
        var content = multiDevice.ReadFileBytes("data.bin");

        Assert.Equal(3 * BlockSize, content.Length);
        Assert.All(content.AsSpan(0, BlockSize).ToArray(), value => Assert.Equal((byte)0xAB, value));
        Assert.Equal(payload.AsSpan(BlockSize).ToArray(), content.AsSpan(BlockSize).ToArray());
    }

    [Fact]
    public void Build_TwoChunkBasedFiles_KeepIndependentIndexTables()
    {
        var first = Enumerable.Repeat((byte)0x33, 2 * BlockSize).ToArray();
        var second = Enumerable.Repeat((byte)0x77, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", first);
        source.WriteBytes("b.bin", second);
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.Equal(first, parser.ReadFileBytes("a.bin"));
        Assert.Equal(second, parser.ReadFileBytes("b.bin"));
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

    private static ErofsInode ExternalChunkRecord(string path, uint deviceId, ulong[] index)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.ChunkBased,
            Idata = new ErofsChunkIdata
            {
                ChunkFormat = ErofsInode.ChunkFormatIndexes,
                DeviceId = deviceId,
            },
            Size = (ulong)(index.Length * BlockSize),
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            DeviceId = deviceId,
            SourcePath = path,
        };
        record.ChunkIndex = index;
        return record;
    }
}