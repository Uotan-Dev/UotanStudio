using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards ext4 sparse_super2 read compliance: when COMPAT_SPARSE_SUPER2 is set, only the two groups
/// named by s_backup_bgs (plus group 0) may carry superblock and group-descriptor copies, the feature
/// mandates s_first_data_block == 0, and backup group ids beyond the group count are corrupt input.
/// The corpus also shells the Alpine WSL mke2fs toolchain under UOTAN_NATIVE_TOOLS=1 to confirm the
/// reader against real sparse_super2 layouts.
/// <para>守护 ext4 的 sparse_super2 读取合规：COMPAT_SPARSE_SUPER2 置位时，仅 s_backup_bgs 命名的两个组
/// （加上组 0）可携带超级块与组描述符副本；该特性要求 s_first_data_block == 0；备份组编号超出组总数即
/// 视为损坏输入。语料还在 UOTAN_NATIVE_TOOLS=1 下调用 Alpine WSL 的 mke2fs，用真实 sparse_super2 布局
/// 验证读取端。</para>
/// </summary>
public sealed class Ext4SparseSuper2Tests
{
    private const int BlockSize = 4096;
    private const int BlocksPerGroup = 32;
    private const int Groups = 4;
    private const int InodesPerGroup = 16;
    private const int InodeSize = 256;
    private const int SuperblockByteOffset = 1024;

    [Fact]
    public void ListFiles_HandBuiltSparseSuper2Image_LoadsAndEnumerates()
    {
        var image = BuildSparseSuper2Image();
        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        Assert.True(parser.Superblock.HasSparseSuper2);
        Assert.Equal(1u, parser.Superblock.BackupGroup1);
        Assert.Equal(3u, parser.Superblock.BackupGroup2);
        var entry = Assert.Single(parser.ListFiles());
        Assert.Equal("hello.txt", entry.Path);
        Assert.Equal((long)FilePayload().Length, entry.Size);
    }

    [Fact]
    public void ReadFile_HandBuiltSparseSuper2Image_ReturnsExactContents()
    {
        var image = BuildSparseSuper2Image();
        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        using var output = new MemoryStream();
        parser.ReadFile("hello.txt", output);
        Assert.Equal(FilePayload(), output.ToArray());
    }

    [Fact]
    public void GroupHasSuperblock_HandBuiltSparseSuper2Image_AcceptsOnlyDeclaredBackupsAndGroupZero()
    {
        var image = BuildSparseSuper2Image();
        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        Assert.True(parser.GroupHasSuperblock(0));
        Assert.True(parser.GroupHasSuperblock(1));
        Assert.False(parser.GroupHasSuperblock(2));
        Assert.True(parser.GroupHasSuperblock(3));
    }

    [Fact]
    public void GroupHasSuperblock_LegacySparseSuperImage_KeepsPowerOfThreeFiveSevenSequence()
    {
        var image = BuildSparseSuper2Image(sparseSuper2: false);
        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        Assert.False(parser.Superblock.HasSparseSuper2);
        foreach (var group in new uint[] { 0, 1, 3, 5, 7, 9, 25, 27, 49 })
        {
            Assert.True(parser.GroupHasSuperblock(group));
        }
        foreach (var group in new uint[] { 2, 4, 6, 8, 10, 11, 24, 26 })
        {
            Assert.False(parser.GroupHasSuperblock(group));
        }
    }

    [Fact]
    public void Ctor_SparseSuper2BackupGroupBeyondGroupCount_ThrowsInvalidData()
    {
        var image = BuildSparseSuper2Image(backup2: Groups);
        using var stream = new MemoryStream(image, writable: false);
        var ex = Assert.Throws<InvalidDataException>(() => new Ext4Parser(stream));
        Assert.Contains("backup group", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_SparseSuper2WithNonZeroFirstDataBlock_ThrowsInvalidData()
    {
        var image = BuildSparseSuper2Image(firstDataBlock: 1);
        using var stream = new MemoryStream(image, writable: false);
        var ex = Assert.Throws<InvalidDataException>(() => new Ext4Parser(stream));
        Assert.Contains("first_data_block", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_NativeMke2fsSparseSuper2WhenAvailable_MatchesManagedParser()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL e2fsprogs to run the sparse_super2 cross-check.");
        }
        var probe = await RunWsl("which", "mke2fs");
        if (probe.ExitCode != 0)
        {
            Assert.Skip("Alpine e2fsprogs are unavailable (UOTAN_NATIVE_TOOLS interop).");
        }

        using var temp = new TempDirectory();
        var sourceDirectory = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(sourceDirectory);
        var payload = FilePayload();
        await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "hello.bin"), payload, TestContext.Current.CancellationToken);
        var imagePath = Path.Combine(temp.Path, "sparse-super2.img");
        var mkfs = await RunWsl("mke2fs", "-q", "-t", "ext4", "-b", "4096", "-g", "512",
            "-O", "sparse_super2,^has_journal,^metadata_csum,^64bit", "-d", WslPath(sourceDirectory),
            WslPath(imagePath), "8M");
        if (mkfs.ExitCode != 0)
        {
            Assert.Skip("Alpine mke2fs cannot create sparse_super2 images with an embedded directory (UOTAN_NATIVE_TOOLS interop).");
        }
        using var parser = new Ext4Parser(imagePath);
        var paths = parser.ListFiles().Select(static entry => entry.Path).ToArray();
        Assert.Contains("hello.bin", paths, StringComparer.Ordinal);
        Assert.True(parser.Superblock.HasSparseSuper2);
        Assert.True(parser.GroupHasSuperblock(0));
        Assert.True(parser.GroupHasSuperblock(parser.Superblock.BackupGroup1));
        Assert.True(parser.GroupHasSuperblock(parser.Superblock.BackupGroup2));
        using var output = new MemoryStream();
        parser.ReadFile("hello.bin", output);
        Assert.Equal(payload, output.ToArray());
    }

    private static byte[] FilePayload()
    {
        var bytes = new byte[8192];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 7) + 1);
        }
        return bytes;
    }

    private static byte[] BuildSparseSuper2Image(uint firstDataBlock = 0, uint backup1 = 1, uint backup2 = 3, bool sparseSuper2 = true)
    {
        var image = new byte[BlocksPerGroup * Groups * BlockSize];
        WriteUInt32(image, SuperblockByteOffset + 0x00, (uint)(InodesPerGroup * Groups));
        WriteUInt32(image, SuperblockByteOffset + 0x04, (uint)(BlocksPerGroup * Groups));
        WriteUInt32(image, SuperblockByteOffset + 0x0C, 96);
        WriteUInt32(image, SuperblockByteOffset + 0x10, (uint)(InodesPerGroup * Groups - 2));
        WriteUInt32(image, SuperblockByteOffset + 0x14, firstDataBlock);
        WriteUInt32(image, SuperblockByteOffset + 0x18, 2);
        WriteUInt32(image, SuperblockByteOffset + 0x20, BlocksPerGroup);
        WriteUInt32(image, SuperblockByteOffset + 0x28, InodesPerGroup);
        WriteUInt16(image, SuperblockByteOffset + 0x38, 0xEF53);
        WriteUInt16(image, SuperblockByteOffset + 0x3A, 1);
        WriteUInt32(image, SuperblockByteOffset + 0x4C, 1);
        WriteUInt16(image, SuperblockByteOffset + 0x58, InodeSize);
        WriteUInt32(image, SuperblockByteOffset + 0x5C, sparseSuper2 ? 0x0200u : 0u);
        WriteUInt32(image, SuperblockByteOffset + 0x60, 0);
        WriteUInt32(image, SuperblockByteOffset + 0x64, 0x1);
        WriteUInt32(image, SuperblockByteOffset + 0x24C, backup1);
        WriteUInt32(image, SuperblockByteOffset + 0x250, backup2);
        for (var group = 0; group < Groups; group++)
        {
            var descriptor = BlockSize + group * 32;
            WriteUInt32(image, descriptor + 0, (uint)(group * BlocksPerGroup + 2));
            WriteUInt32(image, descriptor + 4, (uint)(group * BlocksPerGroup + 3));
            WriteUInt32(image, descriptor + 8, (uint)(group * BlocksPerGroup + 4));
            WriteUInt16(image, descriptor + 12, (ushort)(BlocksPerGroup - 6));
            WriteUInt16(image, descriptor + 14, (ushort)(InodesPerGroup - 2));
            WriteUInt16(image, descriptor + 16, 2);
        }

        var inodeTable = 4 * BlockSize;
        WriteInode(image, inodeTable + (2 - 1) * InodeSize, mode: 0x41ED, size: BlockSize, links: 2, blocks: 1, firstBlock: 5, secondBlock: 0);
        WriteInode(image, inodeTable + (11 - 1) * InodeSize, mode: 0x81A4, size: 8192, links: 1, blocks: 2, firstBlock: 6, secondBlock: 7);
        var directoryBlock = 5 * BlockSize;
        WriteDirectoryEntry(image, directoryBlock, inode: 2, recordLength: 12, name: ".", fileType: 2);
        WriteDirectoryEntry(image, directoryBlock + 12, inode: 2, recordLength: 12, name: "..", fileType: 2);
        WriteDirectoryEntry(image, directoryBlock + 24, inode: 11, recordLength: 4072, name: "hello.txt", fileType: 1);
        FilePayload().CopyTo(image.AsSpan(6 * BlockSize, 8192));
        return image;
    }

    private static void WriteInode(byte[] image, int offset, ushort mode, uint size, ushort links, uint blocks, uint firstBlock, uint secondBlock)
    {
        WriteUInt16(image, offset + 0x00, mode);
        WriteUInt32(image, offset + 0x04, size);
        WriteUInt32(image, offset + 0x08, 1);
        WriteUInt32(image, offset + 0x0C, 1);
        WriteUInt32(image, offset + 0x10, 1);
        WriteUInt16(image, offset + 0x1A, links);
        WriteUInt32(image, offset + 0x1C, blocks);
        WriteUInt32(image, offset + 0x28, firstBlock);
        WriteUInt32(image, offset + 0x2C, secondBlock);
    }

    private static void WriteDirectoryEntry(byte[] image, int offset, uint inode, ushort recordLength, string name, byte fileType)
    {
        WriteUInt32(image, offset + 0, inode);
        WriteUInt16(image, offset + 4, recordLength);
        image[offset + 6] = (byte)name.Length;
        image[offset + 7] = fileType;
        System.Text.Encoding.UTF8.GetBytes(name).CopyTo(image.AsSpan(offset + 8, name.Length));
    }

    private static void WriteUInt16(byte[] image, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] image, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), value);

    private static string WslPath(string path)
        => "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');

    private static async Task<(int ExitCode, string Output)> RunWsl(params string[] arguments)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-d", "Alpine", "--" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }
}