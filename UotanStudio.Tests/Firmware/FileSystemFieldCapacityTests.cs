using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Fixed-size filesystem fields and their capacity rules: EXT4 <c>s_volume_name</c> holds 16 UTF-8 bytes,
/// EROFS stores a symlink target inline inside the inode record so the record must stay within one block,
/// F2FS <c>volume_name</c> is a 512-entry <c>__le16</c> array that must carry whole UTF-16 code units rather
/// than byte-truncated ones, and an F2FS dentry block must reserve slots from the same name length it writes.
/// These tests pin the produced image bytes and the failure messages, because a repo-internal round-trip alone
/// cannot distinguish a masked write from a faithful one when reader and writer share the same lossy encoding.
/// <para>定长文件系统字段及其容量规则：EXT4 的 <c>s_volume_name</c> 只能存放 16 个 UTF-8 字节；EROFS
/// 把符号链接目标内联在 inode 记录里，因此记录必须落在单个块内；F2FS 的 <c>volume_name</c> 是 512 个
/// <c>__le16</c> 元素，必须写入完整的 UTF-16 码元而不是被截断成单字节；F2FS 目录块预留槽位所用的名称长度
/// 必须与真正写入的字节数一致。本组测试钉住生成镜像里的实际字节与失败信息，因为当读写两端共用同一种有损编码时，
/// 仅靠仓库内部的往返测试无法区分“按位截断写入”与“忠实写入”。</para>
/// </summary>
public class FileSystemFieldCapacityTests
{
    private const int Ext4VolumeNameFieldBytes = 16;

    private const int Ext4VolumeNameSlotOffset = Ext4Superblock.SuperblockOffset + 0x78;

    private const int F2fsVolumeNameFieldOffset = 1024 + 124;

    private const int SmallErofsBlockSize = 512;

    private const int DefaultErofsBlockSize = 4096;

    private const string SymlinkSkipReason =
        "The filesystem builders only produce symlink nodes from a real host symbolic link, and creating one "
        + "requires Developer Mode or elevation on Windows; without it there is no link to pack.";

    [Fact]
    public void Ext4Builder_CjkVolumeLabelOverSixteenBytes_ThrowsWithByteCountAndFieldLimit()
    {
        var root = CreateTempDirectory("ext4-label-over");
        var outputDirectory = CreateTempDirectory("ext4-label-over-output");
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "卷标超出超级块槽位的镜像");
            var label = new string('数', 6);
            Assert.Equal(18, Encoding.UTF8.GetByteCount(label));
            var imagePath = Path.Combine(outputDirectory, "over-label.ext4");

            var error = Assert.Throws<InvalidOperationException>(
                () => Ext4Builder.Build(root, imagePath, new Ext4Options { VolumeName = label }));

            Assert.Contains("s_volume_name", error.Message);
            Assert.Contains("18 UTF-8 bytes", error.Message);
            Assert.Contains("16-byte", error.Message);
            Assert.Contains(label, error.Message);
            Assert.False(File.Exists(imagePath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void Ext4Builder_CjkVolumeLabelWithinSixteenBytes_WritesUtf8IntoSuperblockSlot()
    {
        var root = CreateTempDirectory("ext4-label-fit");
        var outputDirectory = CreateTempDirectory("ext4-label-fit-output");
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "卷标落在槽位内的镜像");
            var label = "启动镜像";
            var labelBytes = Encoding.UTF8.GetBytes(label);
            Assert.Equal(12, labelBytes.Length);
            var imagePath = Path.Combine(outputDirectory, "fits-label.ext4");

            Ext4Builder.Build(root, imagePath, new Ext4Options { VolumeName = label });

            var expectedSlot = new byte[Ext4VolumeNameFieldBytes];
            labelBytes.CopyTo(expectedSlot.AsSpan());
            var image = File.ReadAllBytes(imagePath);
            Assert.Equal(expectedSlot, image.AsSpan(Ext4VolumeNameSlotOffset, Ext4VolumeNameFieldBytes).ToArray());

            using var parser = new Ext4Parser(imagePath);
            Assert.Equal(label, parser.Superblock.VolumeName);
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void ErofsBuilder_SymlinkTargetBeyondInlineBlockCapacity_ThrowsWithTargetAndBlockNumbers()
    {
        var root = CreateTempDirectory("erofs-symlink-over");
        var outputDirectory = CreateTempDirectory("erofs-symlink-over-output");
        try
        {
            var linkName = "long-target-link";
            var linkPath = Path.Combine(root, linkName);
            var created = TryCreateSymlink(linkPath, new string('链', 200));
            var recordedTarget = TryReadLinkTarget(linkPath);
            Assert.SkipUnless(created && recordedTarget is not null, SymlinkSkipReason);

            var targetBytes = Encoding.UTF8.GetByteCount(recordedTarget!);
            Assert.True(
                targetBytes > SmallErofsBlockSize - ErofsInode.CompactSize,
                $"The recorded symlink target must exceed the inline capacity of a {SmallErofsBlockSize}-byte block; measured {targetBytes} bytes.");
            var imagePath = Path.Combine(outputDirectory, "over-capacity.erofs");

            var error = Assert.Throws<InvalidOperationException>(
                () => new ErofsBuilder().Build(root, imagePath, new ErofsOptions { BlockSize = SmallErofsBlockSize, Compress = false }));

            Assert.Contains(linkName, error.Message);
            Assert.Contains(targetBytes.ToString(), error.Message);
            Assert.Contains(SmallErofsBlockSize.ToString(), error.Message);
            Assert.False(File.Exists(imagePath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void ErofsBuilder_SymlinkTargetWithinInlineCapacity_RoundTripsTargetAndSize()
    {
        var root = CreateTempDirectory("erofs-symlink-fit");
        var outputDirectory = CreateTempDirectory("erofs-symlink-fit-output");
        try
        {
            var linkName = "short-target-link";
            var linkPath = Path.Combine(root, linkName);
            var created = TryCreateSymlink(linkPath, "数据目标.txt");
            var recordedTarget = TryReadLinkTarget(linkPath);
            Assert.SkipUnless(created && recordedTarget is not null, SymlinkSkipReason);

            var targetBytes = Encoding.UTF8.GetByteCount(recordedTarget!);
            Assert.True(
                targetBytes + ErofsInode.CompactSize <= DefaultErofsBlockSize,
                $"The recorded symlink target plus its {ErofsInode.CompactSize}-byte inode header must stay inside one {DefaultErofsBlockSize}-byte block; measured {targetBytes} bytes.");
            var imagePath = Path.Combine(outputDirectory, "fits-target.erofs");

            new ErofsBuilder().Build(root, imagePath, new ErofsOptions { Compress = false });

            using var parser = new ErofsParser(imagePath);
            var entry = Assert.Single(parser.EnumerateFiles());
            Assert.Equal(linkName, entry.Path);
            Assert.True(entry.IsSymlink);
            Assert.Equal(recordedTarget, entry.SymlinkTarget);
            Assert.Equal((long)targetBytes, entry.Size);
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void F2fsBuilder_CjkVolumeName_WritesFullLittleEndianCodeUnitsIntoSuperblock()
    {
        var root = CreateTempDirectory("f2fs-utf16-label");
        var outputDirectory = CreateTempDirectory("f2fs-utf16-label-output");
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "卷标按 UTF-16 码元写入的镜像");
            var label = "中数据";
            var imagePath = Path.Combine(outputDirectory, "utf16-label.f2fs");

            F2fsBuilder.Build(root, imagePath, new F2fsOptions { VolumeName = label });

            var expectedUnits = Encoding.Unicode.GetBytes(label);
            Assert.Equal([0x2D, 0x4E, 0x70, 0x65, 0x6E, 0x63], expectedUnits);
            var image = File.ReadAllBytes(imagePath);
            Assert.Equal(expectedUnits, image.AsSpan(F2fsVolumeNameFieldOffset, expectedUnits.Length).ToArray());
            Assert.Equal((byte)0x4E, image[F2fsVolumeNameFieldOffset + 1]);
            Assert.Equal([0, 0], image.AsSpan(F2fsVolumeNameFieldOffset + expectedUnits.Length, 2).ToArray());
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void F2fsBuilder_CjkVolumeName_RoundTripsThroughParserVolumeName()
    {
        var root = CreateTempDirectory("f2fs-label-roundtrip");
        var outputDirectory = CreateTempDirectory("f2fs-label-roundtrip-output");
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "卷标往返读取的镜像");
            var label = "中数据卷";
            var imagePath = Path.Combine(outputDirectory, "label.f2fs");

            F2fsBuilder.Build(root, imagePath, new F2fsOptions { VolumeName = label });

            using var parser = new F2fsParser(imagePath);
            Assert.Equal(label, parser.VolumeName);
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void F2fsDirectoryEntryBuildBlock_StoredNameLenBelowUtf8ByteLength_Throws()
    {
        var block = new byte[F2fsNode.NodeSize];
        IReadOnlyList<F2fsDirectoryEntry> entries = [new F2fsDirectoryEntry
        {
            HashCode = 0x5A7E_1234,
            Ino = 12,
            NameLen = 0,
            FileType = F2fsDirectoryEntry.TypeRegularFile,
            Name = "数据",
        }];

        var error = Assert.Throws<InvalidOperationException>(
            () => F2fsDirectoryEntry.BuildBlock(block, entries, F2fsNode.NodeSize));

        Assert.Contains("数据", error.Message);
        Assert.Contains("0 stored", error.Message);
        Assert.Contains("6 encoded", error.Message);
    }

    [Fact]
    public void F2fsDirectoryEntryBuildBlock_ConsistentCjkNameLen_WritesUtf8ByteCountAndSlots()
    {
        var name = "数据";
        var nameBytes = Encoding.UTF8.GetBytes(name);
        IReadOnlyList<F2fsDirectoryEntry> entries = [new F2fsDirectoryEntry
        {
            HashCode = 0x5A7E_1234,
            Ino = 12,
            NameLen = (ushort)nameBytes.Length,
            FileType = F2fsDirectoryEntry.TypeDirectory,
            Name = name,
        }];
        var block = new byte[F2fsNode.NodeSize];

        F2fsDirectoryEntry.BuildBlock(block, entries, F2fsNode.NodeSize);

        var dentry = block.AsSpan(F2fsDirectoryEntry.DentriesOffset, F2fsDirectoryEntry.DirectoryEntrySize);
        Assert.Equal(0x5A7E_1234u, BinaryPrimitives.ReadUInt32LittleEndian(dentry));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(dentry.Slice(4)));
        Assert.Equal((ushort)nameBytes.Length, BinaryPrimitives.ReadUInt16LittleEndian(dentry.Slice(8)));
        Assert.Equal(F2fsDirectoryEntry.TypeDirectory, dentry[10]);

        var expectedSlot = new byte[F2fsDirectoryEntry.SlotLength];
        nameBytes.CopyTo(expectedSlot.AsSpan());
        Assert.Equal(expectedSlot, block.AsSpan(F2fsDirectoryEntry.FilenamesOffset, F2fsDirectoryEntry.SlotLength).ToArray());
        Assert.True(F2fsDirectoryEntry.TestBit(block.AsSpan(0, F2fsDirectoryEntry.BitmapSize), 0));
        Assert.False(F2fsDirectoryEntry.TestBit(block.AsSpan(0, F2fsDirectoryEntry.BitmapSize), 1));
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-field-capacity-{prefix}-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool TryCreateSymlink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadLinkTarget(string linkPath)
    {
        try
        {
            return new FileInfo(linkPath).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}