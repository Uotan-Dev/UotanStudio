using System.Text;
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
/// Guards the build-side replay of captured EROFS dimensions that used to be recomputed from the new layout:
/// the raw device-slot tag bytes, the recorded unified device address while it stays addressable, the
/// <c>chunkformat</c> high bits, the superblock's <c>s_sb_extslots</c> and <c>s_dirblkbits</c>, and the
/// per-inode advise bit. Every dimension keeps the repository's pre-existing behavior when the profile carries
/// no recorded value, so an older document rebuilds exactly as before.
/// <para>守护此前会按新布局重算的已捕获 EROFS 维度的构建侧重放：设备槽 tag 原始字节、仍可寻址时记录的设备统一
/// 地址、<c>chunkformat</c> 高位、超级块的 <c>s_sb_extslots</c> 与 <c>s_dirblkbits</c>，以及逐 inode advise
/// 位。任一方位在档案未记录取值时都保持仓库既有行为，因此更早的文档仍与修复前逐字节一致地重建。</para>
/// </summary>
public sealed class ErofsBuilderReplayTests
{
    private const int BlockSize = 4096;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    private const int DeviceBlocks = 4;

    private const ushort NlinkOneOrDotOmitted = 1 << 4;

    [Fact]
    public void Build_RecordedDeviceTagBytes_AreWrittenVerbatimIntoTheSlot()
    {
        var digest = DigestTag();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x41, BlockSize).ToArray());
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [Device("dev1") with { TagBytes = digest }];

        var image = new ErofsBuilder().Build(source.Path, options);

        var slot = ReadDeviceSlot(image);
        Assert.Equal(digest, slot.Tag.AsSpan(0, digest.Length).ToArray());
        Assert.All(slot.Tag.AsSpan(digest.Length).ToArray(), value => Assert.Equal((byte)0, value));
        using var parser = new ErofsParser(image);
        Assert.Equal(BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_DeviceDescriptorWithoutRecordedBytes_KeepsTheTextTagPath()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x42, BlockSize).ToArray());
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [Device("other.img")];

        var image = new ErofsBuilder().Build(source.Path, options);

        var slot = ReadDeviceSlot(image);
        Assert.Equal("other.img", Encoding.UTF8.GetString(slot.Tag).TrimEnd('\0'));
    }

    [Fact]
    public void Build_RecordedDeviceUniAddrThatStaysAddressable_IsWrittenVerbatim()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x43, BlockSize).ToArray());
        var baseline = new ErofsBuilder().Build(source.Path, DeviceOptions());
        var baselineSlot = ReadDeviceSlot(baseline);

        var replay = DeviceOptions();
        replay.SourceDeviceTable =
        [
            new ErofsSourceDevice(new byte[ErofsDeviceSlot.TagSize], baselineSlot.Blocks, baselineSlot.UniAddr + 8),
        ];

        var rebuilt = new ErofsBuilder().Build(source.Path, replay);

        Assert.Equal(baselineSlot.UniAddr + 8, ReadDeviceSlot(rebuilt).UniAddr);
        Assert.Empty(replay.BuildWarnings);
    }

    [Fact]
    public void Build_RecordedDeviceUniAddrThatOverlapsTheImage_IsRebasedAndReported()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x44, BlockSize).ToArray());
        var baseline = new ErofsBuilder().Build(source.Path, DeviceOptions());
        var baselineSlot = ReadDeviceSlot(baseline);

        var replay = DeviceOptions();
        replay.SourceDeviceTable =
        [
            new ErofsSourceDevice(new byte[ErofsDeviceSlot.TagSize], baselineSlot.Blocks, 1),
        ];

        var rebuilt = new ErofsBuilder().Build(source.Path, replay);

        Assert.Equal(baselineSlot.UniAddr, ReadDeviceSlot(rebuilt).UniAddr);
        var warning = Assert.Single(replay.BuildWarnings);
        Assert.Contains("cannot be replayed", warning, StringComparison.Ordinal);
        Assert.Contains("re-based", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RecordedSbExtSlots_IsPublishedAndKeepsTheConfigurationAreaReadable()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x45, 2 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.SourceSbExtSlots = 3;

        var image = new ErofsBuilder().Build(source.Path, options);

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal((byte)3, superblock.SbExtSlots);
        using var parser = new ErofsParser(image);
        Assert.Equal(BlockSize, parser.ReadRebuildOptions().MaxPclusterSize);
        Assert.Equal(2 * BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_RecordedSbExtSlotsBelowTheMetaboxNeed_IsRaisedToTheMetaboxSlot()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x46, BlockSize).ToArray());
        var options = PinnedOptions();
        options.Metabox = true;
        options.MetaboxRequired = true;
        options.SourceSbExtSlots = 0;

        var image = new ErofsBuilder().Build(source.Path, options);

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal((byte)1, superblock.SbExtSlots);
        Assert.NotEqual(0UL, superblock.MetaBoxNid);
    }

    [Fact]
    public void Build_RecordedDirBlkBitsForTheBlockSize_IsPublished()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x47, BlockSize).ToArray());
        var options = PinnedOptions();
        options.SourceDirBlkBits = 12;

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.Equal((byte)12, ErofsSuperblock.FromImage(image).DirBlkBits);
        using var parser = new ErofsParser(image);
        Assert.Equal(BlockSize, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Build_RecordedDirBlkBitsForAnotherGeometry_IsRefused()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x48, BlockSize).ToArray());
        var options = PinnedOptions();
        options.SourceDirBlkBits = 16;

        var exception = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("s_dirblkbits=16", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnrecordedSbExtSlotsAndDirBlkBits_KeepTheRepositoryDefaults()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x49, BlockSize).ToArray());

        var image = new ErofsBuilder().Build(source.Path, PinnedOptions());

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal((byte)0, superblock.SbExtSlots);
        Assert.Equal((byte)0, superblock.DirBlkBits);
    }

    [Fact]
    public void Build_RecordedChunkFormat48Bit_IsReplayedIntoTheInodeWord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[2 * BlockSize]);
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [Device("blob.img")];
        options.Features = ErofsFeatureSet.Auto
            .With(ErofsFeature.DeviceTable)
            .With(ErofsFeature.ChunkedFile)
            .With(ErofsFeature.FortyEightBit);
        var record = ChunkRecord(
            "data.bin",
            (ushort)(ErofsInode.ChunkFormatIndexes | ErofsInode.ChunkFormat48Bit),
            deviceId: 1,
            [3UL, 5UL]);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 3] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["data.bin"] = record,
        };

        var image = new ErofsBuilder().Build(source.Path, options);

        Assert.True(ErofsSuperblock.FromImage(image).Has48Bit);
        using var parser = new ErofsParser(image);
        var reparsed = parser.ReadRebuildOptions().SourceInodesByPath!["data.bin"];
        Assert.True(reparsed.UsesChunkIndexes);
        Assert.True(reparsed.Chunk48Bit);
        Assert.Equal(1u, reparsed.DeviceId);
        Assert.Equal([3UL, 5UL], reparsed.ChunkIndex!);
    }

    [Fact]
    public void Build_RecordedPerInodeAdviseBit_IsMirroredInsteadOfResolvedOnceForTheImage()
    {
        using var source = new TempDirectory();
        source.WriteBytes("marked.bin", Enumerable.Repeat((byte)0x4A, 2 * BlockSize).ToArray());
        source.WriteBytes("plain.bin", Enumerable.Repeat((byte)0x4B, 2 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.NlinkOneAdviseSupported = true;
        options.DotOmitted = ErofsTriStateFeature.Enabled;
        var marked = CompressedRecord("marked.bin", NlinkOneOrDotOmitted);
        var plain = CompressedRecord("plain.bin", 0);
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 4] = marked, [ulong.MaxValue - 5] = plain };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["marked.bin"] = marked,
            ["plain.bin"] = plain,
        };

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var rebuilt = parser.ReadRebuildOptions().SourceInodesByPath!;
        Assert.NotEqual(0, rebuilt["marked.bin"].IFormat & NlinkOneOrDotOmitted);
        Assert.Equal(0, rebuilt["plain.bin"].IFormat & NlinkOneOrDotOmitted);
    }

    [Fact]
    public void Build_RecordedDotOmittedDirectory_IsReportedInsteadOfDroppedSilently()
    {
        using var source = new TempDirectory();
        source.WriteBytes("sub/keep.bin", Enumerable.Repeat((byte)0x4C, BlockSize).ToArray());
        var options = PinnedOptions();
        options.NlinkOneAdviseSupported = true;
        options.DotOmitted = ErofsTriStateFeature.Enabled;
        var directory = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            IFormat = NlinkOneOrDotOmitted,
            SourceIFormatRecorded = true,
            Mode = ErofsInode.ModeDirectory | 0x1ED,
            DataLayout = ErofsDataLayout.Flat,
            Idata = new ErofsFlatIdata(),
            SourceCompressed = false,
            SourceAlgorithm = ErofsCompressionAlgorithm.Lz4,
            SourceFragmentShape = ErofsFragmentShape.None,
            SourcePath = "sub",
            CTime = PinnedBuildTime,
        };
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 6] = directory };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["sub"] = directory,
        };

        var image = new ErofsBuilder().Build(source.Path, options);

        var warning = Assert.Single(options.BuildWarnings);
        Assert.Contains("dot-omitted", warning, StringComparison.Ordinal);
        Assert.Contains("'sub'", warning, StringComparison.Ordinal);
        using var parser = new ErofsParser(image);
        Assert.Equal(BlockSize, parser.ReadFileBytes("sub/keep.bin").Length);
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

    private static ErofsOptions DeviceOptions()
    {
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [Device("blob.img")];
        return options;
    }

    private static ErofsDeviceSpec Device(string tag)
        => new(tag, DeviceBlocks * BlockSize);

    private static byte[] DigestTag()
    {
        var digest = new byte[32];
        for (var i = 0; i < digest.Length; i++)
        {
            digest[i] = (byte)(0x80 + i);
        }

        digest[0] = 0x00;
        digest[1] = 0xFF;
        return digest;
    }

    private static ErofsDeviceSlot ReadDeviceSlot(byte[] image)
    {
        var superblock = ErofsSuperblock.FromImage(image);
        var offset = superblock.DevtSlotoff * ErofsDeviceSlot.Size;
        return ErofsDeviceSlot.FromBytes(image.AsSpan(offset, ErofsDeviceSlot.Size), superblock.Has48Bit);
    }

    private static ErofsInode ChunkRecord(string path, ushort chunkFormat, uint deviceId, ulong[] index)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            IFormat = 0,
            SourceIFormatRecorded = true,
            DataLayout = ErofsDataLayout.ChunkBased,
            Idata = new ErofsChunkIdata { ChunkFormat = chunkFormat, DeviceId = deviceId },
            Size = (ulong)(index.Length * BlockSize),
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            DeviceId = deviceId,
            SourcePath = path,
            CTime = PinnedBuildTime,
        };
        record.ChunkIndex = index;
        return record;
    }

    private static ErofsInode CompressedRecord(string path, ushort format)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            IFormat = format,
            SourceIFormatRecorded = true,
            DataLayout = ErofsDataLayout.CompactIndexes,
            Idata = new ErofsCompressedIdata { CompactedIndexes = true, LogicalClusterSize = BlockSize },
            Size = 2UL * BlockSize,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourceCompressed = true,
            SourceAlgorithm = ErofsCompressionAlgorithm.Lz4,
            SourceFragmentShape = ErofsFragmentShape.None,
            SourcePath = path,
            CTime = PinnedBuildTime,
        };
        return record;
    }
}