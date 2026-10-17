using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the i_format acceptance word, the nlink-one advice dimension, the source-xattr inheritance and the
/// chunk-format mask as kernel-facing contracts read straight from the image bytes. <c>EROFS_I_ALL</c> is
/// derived upstream from <c>EROFS_I_NLINK_1_BIT = 4</c>, so the widest word both the writer and the kernel
/// accept is <c>0x1F</c>; a regression that narrowed the mask (to the three-bit data-layout width) would make
/// the managed parser reject or mis-decode an inode the kernel itself accepts, and a regression that widened
/// it would emit a word the kernel refuses with <c>unsupported i_format</c>.
/// <para>把 i_format 接受字、nlink-one advice 维度、源 xattr 继承与 chunk 格式掩码作为面向内核、直接从镜像
/// 字节读取的契约钉住。上游由 <c>EROFS_I_NLINK_1_BIT = 4</c> 推导 <c>EROFS_I_ALL</c>，因此写入方与内核都
/// 接受的最宽字是 <c>0x1F</c>；把掩码收窄（收窄到三位数据布局宽度）的回归会让托管解析器拒绝或错误解码内核
/// 自己接受的 inode，而放宽掩码的回归会写出内核以 <c>unsupported i_format</c> 拒绝的字。</para>
/// </summary>
public sealed class ErofsInodeAdviseAcceptanceRegressionTests
{
    private const int BlockSize = 4096;

    private const ushort KernelInodeFormatAll = 0x1F;
    private const ushort NlinkOneOrDotOmitted = 1 << 4;

    private const int AddressFieldOffset = 16;

    [Fact]
    public void Build_NlinkOneAdviseDeclared_KeepsEveryInodeFormatInsideTheDerivedKernelMask()
    {
        Assert.Equal(KernelInodeFormatAll, ErofsInode.AdviseAll);

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
        var formats = InodeFormatsOfEnumeratedFiles(parser, image);
        Assert.Equal(2, formats.Count);
        Assert.All(formats, format => Assert.Equal(0, format & ~KernelInodeFormatAll));

        var files = parser.EnumerateFiles();
        Assert.Contains(files, entry => entry.Path == "plain.txt");
        Assert.Contains(files, entry => entry.Path == "second.txt");
    }

    [Fact]
    public void Build_NlinkOneAdviseDeclared_RaisesBitFourOnSingleLinkedFiles()
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
        var superblock = parser.Superblock;
        var files = parser.EnumerateFiles();
        Assert.NotEmpty(files);
        var marked = 0;
        foreach (var entry in files)
        {
            var format = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan((int)superblock.GetInodeOffset(entry.Nid), 2));
            Assert.Equal(0, format & ~KernelInodeFormatAll);
            Assert.NotEqual(0, format & NlinkOneOrDotOmitted);
            Assert.Equal(1UL, entry.LinkCount);
            marked++;
        }

        Assert.Equal(2, marked);
    }

    [Fact]
    public void Build_NlinkOneAdviseNotDeclared_LeavesBitFourClearOnEveryInode()
    {
        using var source = new TempDirectory();
        source.WriteBytes("plain.txt", "plain payload"u8.ToArray());
        source.WriteBytes("second.txt", "second payload"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            NlinkOneAdviseSupported = false,
            Compress = false,
            BlockSize = BlockSize,
        });

        using var parser = new ErofsParser(image);
        var formats = InodeFormatsOfEnumeratedFiles(parser, image);
        Assert.Equal(2, formats.Count);
        Assert.All(formats, format => Assert.Equal(0, format & NlinkOneOrDotOmitted));
    }

    [Fact]
    public void Capture_Restore_NlinkOneAdviseSource_ReplaysTheDimensionWithoutReSupplyingIt()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x6B, 8 * 1024).ToArray());
        source.WriteBytes("small.txt", "advice"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            NlinkOneAdviseSupported = true,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            BlockSize = BlockSize,
        });

        using (var sourceProbe = new ErofsParser(image))
        {
            var sourceFormats = InodeFormatsOfEnumeratedFiles(sourceProbe, image);
            Assert.Contains(sourceFormats, format => (format & NlinkOneOrDotOmitted) != 0);
        }

        var extracted = Path.Combine(source.Path, "extracted");
        ErofsRebuildParameters profile;
        using (var sourceParser = new ErofsParser(image))
        {
            sourceParser.ExtractToDirectory(extracted);
            profile = ErofsRebuildMetadata.Capture(sourceParser.ReadRebuildOptions());
        }

        Assert.True(profile.NlinkOneAdviseSupported);

        var restored = ErofsRebuildMetadata.Restore(SimulateDocumentRoundTrip(profile));
        Assert.True(restored.NlinkOneAdviseSupported);

        var rebuilt = new ErofsBuilder().Build(extracted, restored);
        using var parser = new ErofsParser(rebuilt);
        var rebuiltFormats = InodeFormatsOfEnumeratedFiles(parser, rebuilt);
        Assert.NotEmpty(rebuiltFormats);
        Assert.All(rebuiltFormats, format => Assert.Equal(0, format & ~KernelInodeFormatAll));
        Assert.Contains(rebuiltFormats, format => (format & NlinkOneOrDotOmitted) != 0);

        Assert.Equal(Enumerable.Repeat((byte)0x6B, 8 * 1024).ToArray(), parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_PreservedPlanWithSourceInodeButNoPlanXattrs_InheritsTheSourceAttributeSet()
    {
        using var source = new TempDirectory();
        source.WriteBytes("target.txt", "payload"u8.ToArray());
        source.WriteBytes("plain.txt", "no xattr here"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["target.txt"] =
                [
                    new ErofsXattrEntry { Name = "user.comment", Value = "hello"u8.ToArray(), NameIndex = 1 },
                    new ErofsXattrEntry { Name = "user.owner", Value = "system"u8.ToArray(), NameIndex = 1 },
                ],
            },
        });

        var extracted = Path.Combine(source.Path, "extracted");
        ErofsOptions captured;
        PreservedInodeEntry[] parsedEntries;
        using (var sourceParser = new ErofsParser(image))
        {
            sourceParser.ExtractToDirectory(extracted);
            captured = sourceParser.ReadRebuildOptions();
            parsedEntries =
            [
                .. sourceParser.EnumerateEntries().Select(entry => new PreservedInodeEntry
                {
                    Path = entry.Path,
                    Inode = entry.Nid,
                    Mode = entry.Mode,
                }),
            ];
            var sourceTarget = Assert.Single(sourceParser.EnumerateFiles(), entry => entry.Path == "target.txt");
            Assert.NotNull(sourceTarget.Xattrs);
            Assert.Single(sourceTarget.Xattrs!, xattr => xattr.Name == "user.comment");
            var sourcePlain = Assert.Single(sourceParser.EnumerateFiles(), entry => entry.Path == "plain.txt");
            Assert.Null(sourcePlain.Xattrs);
        }

        var projection = parsedEntries.ToDictionary(
            entry => entry.Path,
            entry => (entry.Mode & 0xF000) == 0x4000 ? "directory" : "file",
            StringComparer.Ordinal);
        var manifest = new FileSystemPreservationData
        {
            ParsedEntries = [.. parsedEntries],
            ImagePaths = parsedEntries.ToDictionary(entry => entry.Path, entry => entry.Path, StringComparer.Ordinal),
        };

        byte[] rebuilt;
        using (new FileSystemEditPlan(extracted, manifest, projection).Enter())
        {
            rebuilt = new ErofsBuilder().Build(extracted, captured);
        }

        using var parser = new ErofsParser(rebuilt);
        var target = Assert.Single(parser.EnumerateFiles(), entry => entry.Path == "target.txt");
        Assert.NotNull(target.Xattrs);
        var comment = Assert.Single(target.Xattrs!, xattr => xattr.Name == "user.comment");
        Assert.Equal("hello", Encoding.UTF8.GetString(comment.Value));
        var owner = Assert.Single(target.Xattrs!, xattr => xattr.Name == "user.owner");
        Assert.Equal("system", Encoding.UTF8.GetString(owner.Value));
        var plain = Assert.Single(parser.EnumerateFiles(), entry => entry.Path == "plain.txt");
        Assert.Null(plain.Xattrs);
        Assert.Equal("payload"u8.ToArray(), parser.ReadFileBytes("target.txt"));
    }

    [Fact]
    public void Build_ChunkBasedImage_KeepsChunkFormatInsideTheDefinedMask()
    {
        Assert.Equal(0x7F, ErofsInode.ChunkFormatAll);

        var payload = Enumerable.Repeat((byte)0x4C, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            ChunkSize = BlockSize,
            SuperblockChecksum = false,
        });

        using var parser = new ErofsParser(image);
        var entry = Assert.Single(parser.EnumerateFiles(), file => file.Path == "data.bin");
        var inodeOffset = parser.Superblock.GetInodeOffset(entry.Nid);
        var format = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)inodeOffset, 2));
        Assert.Equal(ErofsInode.DataLayoutChunkBased, (ushort)(format & ErofsInode.DataLayoutMask));

        var chunkFormat = BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan((int)inodeOffset + AddressFieldOffset, 2));
        Assert.Equal(0, chunkFormat & ~ErofsInode.ChunkFormatAll);

        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Read_ChunkBasedInodeRaisingAnUndefinedBit_ThrowsCorruption()
    {
        var payload = Enumerable.Repeat((byte)0x4C, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            ChunkSize = BlockSize,
            SuperblockChecksum = false,
        });

        var corrupted = RewriteChunkFormat(image, ErofsInode.ChunkFormatAll + 1);

        using var corruptedParser = new ErofsParser(corrupted);
        var error = Assert.Throws<FileSystemCorruptedException>(
            corruptedParser.ReadRebuildOptions);
        Assert.Contains("chunk format", error.Message, StringComparison.Ordinal);
        Assert.Contains("data.bin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ImageWithoutFortyEightBit_IgnoresTheReservedHighHalfOfADeviceSlot()
    {
        var payload = Enumerable.Repeat((byte)0x11, 3 * BlockSize).ToArray();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            ChunkSize = BlockSize,
            ExtraDeviceCount = 1,
            ExtraDeviceImages = [new ErofsDeviceSpec("blob.img", 2 * BlockSize)],
            SuperblockChecksum = false,
        });

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.False(superblock.Has48Bit);
        Assert.Equal((ushort)1, superblock.ExtraDevices);

        var slotOffset = checked((int)(superblock.DevtSlotoff * ErofsDeviceSlot.Size));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotOffset + 64)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(slotOffset + 72)));

        var poisoned = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(poisoned.AsSpan(slotOffset + 72), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(poisoned.AsSpan(slotOffset + 74), ushort.MaxValue);

        var device = new byte[2 * BlockSize];
        Array.Fill(device, (byte)0xAB);
        Stream[] streams = [new MemoryStream(poisoned, writable: false), new MemoryStream(device, writable: false)];
        using var multiDevice = new ErofsParser(streams);
        var content = multiDevice.ReadFileBytes("data.bin");

        Assert.Equal(3 * BlockSize, content.Length);
        Assert.Equal(payload, content);
    }

    private static byte[] RewriteChunkFormat(byte[] image, ushort undefinedBit)
    {
        var copy = (byte[])image.Clone();
        using var parser = new ErofsParser(copy);
        var entry = Assert.Single(parser.EnumerateFiles(), file => file.Path == "data.bin");
        var inodeOffset = checked((int)parser.Superblock.GetInodeOffset(entry.Nid));
        var current = BinaryPrimitives.ReadUInt16LittleEndian(
            copy.AsSpan(inodeOffset + AddressFieldOffset, 2));
        Assert.Equal(0, current & undefinedBit);
        BinaryPrimitives.WriteUInt16LittleEndian(
            copy.AsSpan(inodeOffset + AddressFieldOffset, 2),
            (ushort)(current | undefinedBit));
        return copy;
    }

    private static List<ushort> InodeFormatsOfEnumeratedFiles(ErofsParser parser, byte[] image)
    {
        var formats = new List<ushort>();
        foreach (var entry in parser.EnumerateFiles().Where(entry => !entry.IsDirectory))
        {
            var offset = parser.Superblock.GetInodeOffset(entry.Nid);
            if (offset < 0 || offset + 2 > image.Length)
            {
                continue;
            }

            formats.Add(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)offset, 2)));
        }

        return formats;
    }

    private static ErofsRebuildParameters SimulateDocumentRoundTrip(ErofsRebuildParameters parameters)
        => JsonSerializer.Deserialize(
            JsonSerializer.Serialize(parameters, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters),
            FileSystemPreservationJsonContext.Default.ErofsRebuildParameters)!;
}