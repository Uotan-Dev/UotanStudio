using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Tests for the ext4 directory-entry name bound: a name may only occupy bytes inside its own
/// record (<c>rec_len - 8</c>), still intersected with the bytes left in the block, and a record
/// declaring more ends the walk instead of folding the next records into one name.
/// <para>ext4 目录项名称边界测试：名称只能占用自身记录之内的字节（<c>rec_len - 8</c>），并按块剩余字节取交集；
/// 声明更多名称字节的记录会结束扫描，而不是把后续记录折进一个名称。</para>
/// </summary>
public class Ext4DirentNameBoundTests
{
    [Fact]
    public void Enumerate_TwoWellFormedRecords_ListsBothNames()
    {
        var block = new byte[64];
        WriteRecord(block, 0, 11, 16, "abcd", Ext4DirectoryEntry.TypeRegular);
        WriteRecord(block, 16, 12, 48, "hello", Ext4DirectoryEntry.TypeSymlink);

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "abcd", "hello" }, entries.Select(static e => e.Name).ToArray());
        Assert.Equal(new uint[] { 11, 12 }, entries.Select(static e => e.Inode).ToArray());
        Assert.Equal(new ushort[] { 16, 48 }, entries.Select(static e => e.RecordLength).ToArray());
        Assert.Equal(new byte[] { Ext4DirectoryEntry.TypeRegular, Ext4DirectoryEntry.TypeSymlink }, entries.Select(static e => e.FileType).ToArray());
        Assert.Equal(new byte[] { 4, 5 }, entries.Select(static e => e.NameLength).ToArray());
    }

    [Fact]
    public void Enumerate_NameLengthBeyondRecordCapacity_StopsWithoutFoldingNextRecord()
    {
        var block = new byte[64];
        WriteRecord(block, 0, 11, 16, "abcd", Ext4DirectoryEntry.TypeRegular);
        WriteRecord(block, 16, 12, 16, "victim", Ext4DirectoryEntry.TypeRegular);
        WriteRecord(block, 32, 13, 32, "last", Ext4DirectoryEntry.TypeRegular);
        block[22] = 20;

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Single(entries);
        Assert.Equal("abcd", entries[0].Name);
        Assert.DoesNotContain(entries, static entry => entry.Name.Contains("last", StringComparison.Ordinal));
        Assert.DoesNotContain("last", entries.Select(static entry => entry.Name).ToArray());
        Assert.All(entries, static entry => Assert.InRange((int)entry.NameLength, 0, (int)entry.RecordLength - 8));
    }

    [Fact]
    public void Enumerate_NameLengthBeyondBlockEnd_StopsWithoutReadingPastArray()
    {
        var block = new byte[24];
        WriteRecord(block, 0, 5, 24, "0123456789ABCD", Ext4DirectoryEntry.TypeRegular);
        block[6] = 40;

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Empty(entries);
    }

    [Fact]
    public void Enumerate_NameLengthExactlyFillsRecord_ListsName()
    {
        var block = new byte[24];
        WriteRecord(block, 0, 5, 24, "0123456789ABCDEF", Ext4DirectoryEntry.TypeRegular);

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Single(entries);
        Assert.Equal("0123456789ABCDEF", entries[0].Name);
        Assert.Equal((byte)16, entries[0].NameLength);
        Assert.Equal((ushort)24, entries[0].RecordLength);
    }

    [Fact]
    public void Enumerate_DirEntryTailAfterRealRecord_ListsOnlyRealRecord()
    {
        var block = new byte[32];
        WriteRecord(block, 0, 11, 12, "abcd", Ext4DirectoryEntry.TypeRegular);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16, 2), 12);
        block[18] = 0;
        block[19] = 0xDE;

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Single(entries);
        Assert.Equal("abcd", entries[0].Name);
        Assert.Equal((ushort)12, entries[0].RecordLength);
    }

    [Fact]
    public void Enumerate_EmptyNameRecord_StopsListingFollowingRecords()
    {
        var block = new byte[32];
        WriteRecord(block, 0, 7, 8, string.Empty, Ext4DirectoryEntry.TypeRegular);
        WriteRecord(block, 8, 8, 24, "after", Ext4DirectoryEntry.TypeRegular);

        var entries = Ext4DirectoryEntry.Enumerate(block);

        Assert.Empty(entries);
    }

    [Fact]
    public void Parser_DirentNameLengthBeyondRecord_StopsListingLaterEntriesInBlock()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "alpha.txt"), "AAA\n");
            File.WriteAllText(Path.Combine(root, "omega.txt"), "OOO\n");
            File.WriteAllText(Path.Combine(root, "zeta.txt"), "ZZZ\n");

            var image = Ext4Builder.Build(root);
            var position = FindDirentPosition(image, "omega.txt");
            Assert.True(position >= 0, "Test setup failed: 'omega.txt' directory entry not found.");
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(position + 4, 2));
            var declaredNameBytes = (byte)(recordLength - 8 + 4);
            Assert.True(declaredNameBytes > recordLength - 8);
            image[position + 6] = declaredNameBytes;

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var paths = parser.ListFiles()
                .Where(static entry => entry.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static entry => entry.Path)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "alpha.txt" }, paths);
            Assert.DoesNotContain(paths, static path => path.Contains("omega", StringComparison.Ordinal));
            Assert.DoesNotContain("zeta.txt", paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_CjkName_RoundTripsThroughParser()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "中文文件名.txt"), "镜像内容");
            Directory.CreateDirectory(Path.Combine(root, "子目录"));
            File.WriteAllText(Path.Combine(root, "子目录", "内部文件.bin"), "数据");

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var paths = parser.ListFiles()
                .Select(static entry => entry.Path)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "中文文件名.txt", "子目录", "子目录/内部文件.bin" }, paths);

            using var sink = new MemoryStream();
            parser.ReadFile("中文文件名.txt", sink);
            Assert.Equal("镜像内容", Encoding.UTF8.GetString(sink.ToArray()));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static void WriteRecord(byte[] block, int offset, uint inode, ushort recordLength, string name, byte fileType)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset, 4), inode);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 4, 2), recordLength);
        block[offset + 6] = (byte)nameBytes.Length;
        block[offset + 7] = fileType;
        nameBytes.CopyTo(block.AsSpan(offset + 8));
    }

    private static int FindDirentPosition(byte[] image, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        for (var position = 0; position + 8 + nameBytes.Length <= image.Length; position += 4)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(position + 4, 2)) < 8 + nameBytes.Length)
            {
                continue;
            }

            if (image[position + 6] != nameBytes.Length || image[position + 7] != Ext4DirectoryEntry.TypeRegular)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position, 4)) == 0)
            {
                continue;
            }

            if (image.AsSpan(position + 8, nameBytes.Length).SequenceEqual(nameBytes))
            {
                return position;
            }
        }

        return -1;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-dirent-" + Path.GetRandomFileName());
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
}