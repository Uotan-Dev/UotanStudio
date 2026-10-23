using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards what the rebuild capture records about a verified source image, beyond the dimensions the rebuilt
/// bytes are derived from: the device slot's raw tag field, the whole chunk format word and the chunk index
/// table behind it, the per-inode <c>i_format</c> word together with the compacted 2-byte and interlaced index
/// forms, and the extended-superblock geometry (<c>s_sb_extslots</c>, <c>dirblkbits</c>) that the compression
/// records and the directory geometry are anchored to. Every dimension is compared against the bytes the
/// builder actually wrote, so a capture that only kept the low five chunk bits or rejected a binary tag is
/// reported here instead of surfacing later as an image that cannot be rebuilt.
/// <para>守护重建采集对已校验源镜像所记录的、超出"重建字节由此推导"的那些维度：设备槽的原始 tag 字段、完整
/// chunk 格式字及其背后的 chunk 索引表、逐 inode 的 <c>i_format</c> 字连同紧凑 2 字节与交错索引形态，以及压缩
/// 记录与目录几何所依赖的扩展超级块几何（<c>s_sb_extslots</c>、<c>dirblkbits</c>）。每个维度都对照打包器实际写出
/// 的字节比较，因此只保留 chunk 低 5 位或拒绝二进制 tag 的采集会在这里被报告，而不是以后以"无法重建的镜像"
/// 的形式出现。</para>
/// </summary>
public sealed class ErofsCaptureFidelityTests
{
    private const int BlockSize = 4096;

    private const int AddressFieldOffset = 16;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    [Fact]
    public void Capture_ExtendedSuperblockGeometry_RecordsExtSlotsAndDirectoryBlockBits()
    {
        using var source = new TempDirectory();
        source.WriteBytes("payload.bin", new byte[BlockSize]);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            Metabox = true,
        });

        using var parser = new ErofsParser(image);
        Assert.True(parser.Superblock.SbExtSlots >= 1, "the metabox build must widen the superblock area.");

        var captured = parser.ReadRebuildOptions();

        Assert.Equal<byte?>(parser.Superblock.SbExtSlots, captured.SourceSbExtSlots);
        Assert.Equal<byte?>(parser.Superblock.DirBlkBits, captured.SourceDirBlkBits);
    }

    [Fact]
    public void Capture_DigestDeviceTag_KeepsTheRawSlotBytesAndTheDeviceTable()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x21, 2 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 2 * BlockSize)];

        var image = new ErofsBuilder().Build(source.Path, options);
        int slotOffset;
        using (var probe = new ErofsParser(image))
        {
            slotOffset = checked((int)probe.Superblock.DevtSlotoff * ErofsDeviceSlot.Size);
        }

        var digest = DigestTag();
        var slotTag = new byte[ErofsDeviceSlot.TagSize];
        digest.CopyTo(slotTag, 0);
        slotTag.CopyTo(image, slotOffset);

        using var parser = new ErofsParser(image);
        var captured = parser.ReadRebuildOptions();

        Assert.True(captured.Features.Contains(ErofsFeature.DeviceTable),
            "a digest tag must not make the capture drop the declared device table.");
        Assert.NotNull(captured.ExtraDeviceImages);
        var descriptor = Assert.Single(captured.ExtraDeviceImages!);
        Assert.Equal(slotTag, descriptor.TagBytes);
        Assert.Equal(2L * BlockSize, descriptor.SizeBytes);
        Assert.NotNull(captured.SourceDeviceTable);
        Assert.Equal(slotTag, Assert.Single(captured.SourceDeviceTable!).Tag);
    }

    [Fact]
    public void Capture_ChunkBasedInode_RecordsTheWholeChunkFormatWordAndTheIndexTable()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 3 * BlockSize).ToArray());
        var options = PinnedOptions();
        options.ChunkSize = BlockSize;
        options.ChunkIndexKind = ErofsChunkIndexKind.Indexes;

        var image = new ErofsBuilder().Build(source.Path, options);
        int formatOffset;
        using (var probe = new ErofsParser(image))
        {
            var nid = probe.EnumerateFiles().Single(file => file.Path == "data.bin").Nid;
            formatOffset = checked((int)probe.Superblock.GetInodeOffset(nid) + AddressFieldOffset);
            var written = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(formatOffset, 2));
            Assert.NotEqual(0, written & ErofsInode.ChunkFormatIndexes);
        }

        var patched = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(formatOffset, 2))
            | ErofsInode.ChunkFormat48Bit);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(formatOffset, 2), patched);

        using var parser = new ErofsParser(image);
        var captured = parser.ReadRebuildOptions();
        var record = captured.SourceInodesByPath!["data.bin"];

        Assert.Equal(ErofsDataLayout.ChunkBased, record.DataLayout);
        Assert.True(record.UsesChunkIndexes);
        Assert.Equal(patched, record.ChunkFormat);
        Assert.Equal(0, record.ChunkFormat & ~ErofsInode.ChunkFormatAll);
        Assert.NotNull(record.ChunkIndex);
        Assert.Equal(3, record.ChunkIndex!.Length);
    }

    [Fact]
    public void Capture_SingleLinkedFiles_RecordTheirOwnFormatWord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("plain.txt", "plain payload"u8.ToArray());
        source.WriteBytes("second.txt", "second payload"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            NlinkOneAdviseSupported = true,
            Compress = false,
            BlockSize = BlockSize,
        });

        using var parser = new ErofsParser(image);
        var captured = parser.ReadRebuildOptions();
        var records = captured.SourceInodes!;
        var marked = 0;
        foreach (var entry in parser.EnumerateFiles())
        {
            var onDisk = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan(checked((int)parser.Superblock.GetInodeOffset(entry.Nid)), 2));
            var record = records[entry.Nid];
            Assert.Equal(onDisk, record.IFormat);
            if ((record.IFormat & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0)
            {
                marked++;
            }
        }

        Assert.Equal(2, marked);
    }

    [Fact]
    public void Capture_Compacted2BIndexes_RecordsTheCompactionFormAndTheAdviseWord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", CompressiblePayload(1500 * 1024, 1024));
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = 1024,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseCompactedIndexes = true,
            UseCompacted2BIndexes = true,
        });

        using var parser = new ErofsParser(image);
        var captured = parser.ReadRebuildOptions();
        var record = captured.SourceInodesByPath!["data.bin"];
        var header = ZErofsMapHeader.FromBytes(
            image.AsSpan((int)parser.CompressedMapHeaderOffset("data.bin"), ZErofsMapHeader.Size));

        Assert.True(header.HasCompacted2B);
        Assert.Equal(ErofsFragmentShape.None, record.SourceFragmentShape);
        Assert.Equal(ErofsDataLayout.CompactIndexes, record.DataLayout);
        Assert.True(record.Compacted2B);
        Assert.Equal(header.Advise, record.ZAdvise);
        Assert.Equal(
            (ushort)(header.Advise & ZErofsMapHeader.AdviseInterlacedPcluster),
            (ushort)(record.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster));
    }

    [Fact]
    public void ChunkFormatFields_MatchTheOnDiskDefinition()
    {
        Assert.Equal((ushort)0x001F, ErofsInode.ChunkFormatBlkBitsMask);
        Assert.Equal((ushort)0x0020, ErofsInode.ChunkFormatIndexes);
        Assert.Equal((ushort)0x0040, ErofsInode.ChunkFormat48Bit);
        Assert.Equal(
            ErofsInode.ChunkFormat48Bit,
            (ushort)(ErofsInode.ChunkFormatAll & ErofsInode.ChunkFormat48Bit));
    }

    [Fact]
    public void DeviceSpec_RawTagBytes_SurviveValidationBesideTheTextLabel()
    {
        var digest = DigestTag();
        var options = PinnedOptions();
        options.ExtraDeviceCount = 1;
        options.ExtraDeviceImages = [new ErofsDeviceSpec("dev1", 2 * BlockSize) { TagBytes = digest }];

        options.Validate();

        Assert.Equal(digest, options.ExtraDeviceImages[0].TagBytes);
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

    private static byte[] DigestTag()
    {
        var tag = new byte[32];
        for (var i = 0; i < tag.Length; i++)
        {
            tag[i] = (byte)(0x80 + i);
        }

        tag[7] = 0;
        tag[19] = 0xFF;
        return tag;
    }

    private static byte[] CompressiblePayload(int length, int blockSize)
    {
        var payload = new byte[length];
        var block = new byte[blockSize];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (byte)((i * 31 + i / 7) & 0x3F);
        }

        for (var written = 0; written < payload.Length; written += block.Length)
        {
            var count = Math.Min(block.Length, payload.Length - written);
            block.AsSpan(0, count).CopyTo(payload.AsSpan(written));
            block[0] = (byte)(written / block.Length);
        }

        return payload;
    }
}