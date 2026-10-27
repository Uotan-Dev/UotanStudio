using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Directory-entry name charset guard shared by the EXT4 and EROFS dirent writers: the Linux directory format never
/// lets a path component reach a dirent when it is empty, when it carries <c>'/'</c> or a NUL byte, or when
/// it is the <c>.</c> or <c>..</c> name the writer synthesises itself, and e2fsck or fsck.erofs reports such
/// a record as corrupt, so the builders must fail before publishing rather than emit an untraversable tree.
/// The length rule stays separate because an extended-attribute short name lives in a different namespace
/// where those characters are ordinary, so only the byte limit governs it. Every name the formats can
/// encode — CJK, accents, spaces, apostrophes, a leading hyphen, a dot in the middle — still builds and
/// round-trips through the matching parser, and the synthesised dot records survive in the written block.
/// <para>EXT4 与 EROFS 目录项写入器共用的名称字符集守卫：Linux 目录格式从不允许空组件、含 <c>'/'</c> 或 NUL
/// 字节的组件，以及写入器自己合成的 <c>.</c> 与 <c>..</c> 名称进入目录项，e2fsck 与 fsck.erofs 会把这类记录
/// 判为损坏，因此构建器必须在发布镜像之前失败，而不是写出无法遍历的目录树。长度规则保持独立，因为扩展属性的
/// 短名称属于另一个命名空间，那些字符在那里只是普通字符，只受字节上限约束。格式能编码的名称——中文、重音
/// 字母、空格、撇号、连字符开头、名中含点——仍然能构建并通过对应解析器往返校验，写入块里合成的点条目也必须
/// 保留。</para>
/// </summary>
public class FileSystemNameCharsetTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void ValidateEntryName_EmptyChildName_ThrowsWithFormatAndQuotedValue()
    {
        var ext4Error = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName(string.Empty, "EXT4"));
        var erofsError = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName(string.Empty, "EROFS"));

        Assert.Equal(
            "EXT4 file name is not representable: a directory entry name cannot be empty: ''.",
            ext4Error.Message);
        Assert.Equal(
            "EROFS file name is not representable: a directory entry name cannot be empty: ''.",
            erofsError.Message);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public void ValidateEntryName_ChildNameWithSeparator_ThrowsWithFormatAndValue(string formatName)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName("bin/ls", formatName));

        Assert.Equal(
            $"{formatName} file name is not representable: a directory entry name cannot contain the path separator '/': 'bin/ls'.",
            error.Message);
        Assert.Contains("'bin/ls'", error.Message);
    }

    [Fact]
    public void ValidateEntryName_ChildNameWithNulCharacter_ThrowsWithFormatAndValue()
    {
        var name = "init\0rc";

        var error = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName(name, "EROFS"));

        Assert.Equal(
            "EROFS file name is not representable: a directory entry name cannot contain the NUL character: 'init\0rc'.",
            error.Message);
        Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void ValidateEntryName_ChildNameIsDotEntry_ThrowsAndExplainsSynthesisedRecords(string name)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName(name, "EXT4"));

        Assert.Equal(
            $"EXT4 file name is not representable: '{name}' would duplicate the '.' and '..' entries the writer synthesises for every directory.",
            error.Message);
        Assert.Contains("would duplicate", error.Message);
        Assert.Contains("synthesises", error.Message);
    }

    [Fact]
    public void ValidateEntryName_ChildNameBothOverlongAndUnsafe_ReportsTheLengthRuleFirst()
    {
        var name = "bin/" + new string('文', 90);

        var error = Assert.Throws<InvalidOperationException>(
            () => FileSystemNameLimits.ValidateEntryName(name, "EROFS"));

        Assert.Contains($"{Encoding.UTF8.GetByteCount(name)} UTF-8 bytes exceed the 255-byte name limit", error.Message);
        Assert.DoesNotContain("path separator", error.Message);
    }

    [Fact]
    public void Validate_XattrShortNameNamespace_StillAppliesOnlyTheLengthRule()
    {
        FileSystemNameLimits.Validate(string.Empty, "EXT4");
        FileSystemNameLimits.Validate(".", "EXT4");
        FileSystemNameLimits.Validate("..", "EXT4");
        FileSystemNameLimits.Validate("selinux/contexts", "EXT4");
        FileSystemNameLimits.Validate("security\0selinux", "EXT4");

        var overLimit = new string('文', 86);
        Assert.Equal(258, Encoding.UTF8.GetByteCount(overLimit));
        var error = Assert.Throws<InvalidOperationException>(() => FileSystemNameLimits.Validate(overLimit, "EXT4"));
        Assert.Equal(
            "EXT4 file name is not representable: 258 UTF-8 bytes exceed the 255-byte name limit: '" + overLimit + "'.",
            error.Message);
    }

    [Fact]
    public void ValidateEntryName_EncodableNamesWithCjkAccentsSpacingAndPunctuation_AcceptsEveryOne()
    {
        foreach (var name in EncodableNames())
        {
            FileSystemNameLimits.ValidateEntryName(name, "EXT4");
            FileSystemNameLimits.ValidateEntryName(name, "EROFS");
        }

        var maximumName = new string('文', 85);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(maximumName));
        FileSystemNameLimits.ValidateEntryName(maximumName, "EROFS");
    }

    [Fact]
    public void Ext4HtreeBuildHtree_ChildNameUnsafeForDirectoryEntries_ThrowsWithFormatAndValue()
    {
        foreach (var name in new[] { string.Empty, "bin/ls", "init\0rc", ".", ".." })
        {
            List<(string Name, uint Inode, byte FileType)> entries = [(name, 12u, Ext4DirectoryEntry.TypeRegular)];

            var error = Assert.Throws<InvalidOperationException>(
                () => Ext4Htree.BuildHtree(entries, BlockSize, [1, 2, 3, 4]));

            Assert.StartsWith("EXT4 file name is not representable", error.Message);
            Assert.Contains($"'{name}'", error.Message);
        }
    }

    [Fact]
    public void Ext4HtreeBuildHtree_EncodableChildNames_KeepsSynthesisedDotRecordsAndWritesEveryName()
    {
        List<(string Name, uint Inode, byte FileType)> entries =
        [
            ("中文 说明.txt", 12u, Ext4DirectoryEntry.TypeRegular),
            ("O'Brien.cfg", 13u, Ext4DirectoryEntry.TypeRegular),
            ("-leading-hyphen.txt", 14u, Ext4DirectoryEntry.TypeRegular),
            ("dot.in.middle.txt", 15u, Ext4DirectoryEntry.TypeRegular),
            ("..double-dot-prefix.cfg", 16u, Ext4DirectoryEntry.TypeRegular),
        ];

        var plan = Ext4Htree.BuildHtree(entries, BlockSize, [1, 2, 3, 4]);

        Assert.Equal((byte)'.', plan.RootBlock[8]);
        Assert.Equal((byte)'.', plan.RootBlock[20]);
        Assert.Equal((byte)'.', plan.RootBlock[21]);
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(plan.RootBlock.AsSpan(4, 2)));
        Assert.NotEmpty(plan.LeafBlocks);
        var leaves = JoinBlocks(plan.LeafBlocks);
        foreach (var (name, _, _) in entries)
        {
            Assert.True(
                leaves.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name)) >= 0,
                $"The leaf blocks lost the child name '{name}'.");
        }
    }

    [Fact]
    public void Ext4Builder_EncodableChildNames_PublishesImageWhoseDirectoryKeepsDotEntries()
    {
        var root = CreateTempDirectory("ext4-charset");
        var outputDirectory = CreateTempDirectory("ext4-charset-output");
        try
        {
            var names = EncodableNames();
            var expected = new List<string>();
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(root, name), name);
                expected.Add(name);
            }

            Directory.CreateDirectory(Path.Combine(root, "配置 目录"));
            File.WriteAllText(Path.Combine(root, "配置 目录", "内部 文件.txt"), "内部内容");
            expected.Add("配置 目录/内部 文件.txt");
            var imagePath = Path.Combine(outputDirectory, "charset.ext4");

            Ext4Builder.Build(root, imagePath);

            Assert.True(File.Exists(imagePath));
            var rootBlock = ReadRootDirectoryBlock(File.ReadAllBytes(imagePath));
            Assert.Equal((byte)'.', rootBlock[8]);
            Assert.Equal((byte)'.', rootBlock[20]);
            Assert.Equal((byte)'.', rootBlock[21]);
            Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(rootBlock.AsSpan(4, 2)));

            using var parser = new Ext4Parser(imagePath);
            var paths = parser.ListFiles()
                .Where(static entry => entry.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static entry => entry.Path);
            Assert.Equal(SortedPaths(expected), SortedPaths(paths));
            foreach (var name in names)
            {
                Assert.Equal(Encoding.UTF8.GetBytes(name), ReadExt4(parser, name));
            }

            Assert.Equal(Encoding.UTF8.GetBytes("内部内容"), ReadExt4(parser, "配置 目录/内部 文件.txt"));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void Ext4Builder_LargeDirectoryWithEncodableNames_RoundTripsThroughIndexedLeafBlocks()
    {
        var root = CreateTempDirectory("ext4-indexed");
        var outputDirectory = CreateTempDirectory("ext4-indexed-output");
        try
        {
            var expected = new string[250];
            for (var i = 0; i < expected.Length; i++)
            {
                var name = $"配置 文件-O'Brien-{i:D4}.txt";
                File.WriteAllText(Path.Combine(root, name), name);
                expected[i] = name;
            }

            var imagePath = Path.Combine(outputDirectory, "indexed.ext4");
            Ext4Builder.Build(root, imagePath);

            Assert.True(File.Exists(imagePath));
            var rootBlock = ReadRootDirectoryBlock(File.ReadAllBytes(imagePath));
            Assert.Equal((byte)'.', rootBlock[8]);
            Assert.Equal((byte)'.', rootBlock[20]);
            Assert.Equal((byte)'.', rootBlock[21]);

            using var parser = new Ext4Parser(imagePath);
            var listed = parser.ListFiles()
                .Where(static entry => entry.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static entry => entry.Path)
                .ToArray();
            Assert.Equal(SortedPaths(expected), SortedPaths(listed));
            Assert.Equal(expected[0], Encoding.UTF8.GetString(ReadExt4(parser, expected[0])));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void ErofsBuilder_EncodableChildNames_PublishesImageAndRoundTripsThroughParser()
    {
        var root = CreateTempDirectory("erofs-charset");
        var outputDirectory = CreateTempDirectory("erofs-charset-output");
        try
        {
            var names = EncodableNames();
            var expected = new List<string>();
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(root, name), name);
                expected.Add(name);
            }

            Directory.CreateDirectory(Path.Combine(root, "配置 目录"));
            File.WriteAllText(Path.Combine(root, "配置 目录", "内部 文件.txt"), "内部内容");
            expected.Add("配置 目录/内部 文件.txt");
            var imagePath = Path.Combine(outputDirectory, "charset.erofs");

            new ErofsBuilder().Build(root, imagePath, new ErofsOptions { Compress = false });

            Assert.True(File.Exists(imagePath));
            using var parser = new ErofsParser(imagePath);
            var paths = parser.EnumerateFiles().Select(static entry => entry.Path);
            Assert.Equal(SortedPaths(expected), SortedPaths(paths));
            foreach (var name in names)
            {
                Assert.Equal(Encoding.UTF8.GetBytes(name), parser.ReadFileBytes(name));
            }

            Assert.Equal(Encoding.UTF8.GetBytes("内部内容"), parser.ReadFileBytes("配置 目录/内部 文件.txt"));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    private static string[] EncodableNames()
    {
        return
        [
            "中文 说明.txt",
            "café.txt",
            "Straße.cfg",
            "日本語 テスト.txt",
            "O'Brien.cfg",
            "-leading-hyphen.txt",
            "dot.in.middle.txt",
            "..double-dot-prefix.cfg",
            "...three-dots.cfg",
            "a  b  c.txt",
        ];
    }

    private static string[] SortedPaths(IEnumerable<string> paths)
        => [.. paths.OrderBy(static path => path, StringComparer.Ordinal)];

    private static byte[] ReadExt4(Ext4Parser parser, string path)
    {
        using var buffer = new MemoryStream();
        parser.ReadFile(path, buffer);
        return buffer.ToArray();
    }

    private static byte[] JoinBlocks(List<byte[]> blocks)
    {
        var joined = new byte[blocks.Sum(static block => block.Length)];
        var offset = 0;
        foreach (var block in blocks)
        {
            block.AsSpan().CopyTo(joined.AsSpan(offset));
            offset += block.Length;
        }

        return joined;
    }

    private static byte[] ReadRootDirectoryBlock(byte[] image)
    {
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x28, 4));
        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 0x58, 2));
        var inodeTableBlock = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(BlockSize + 8, 4));
        var inodeOffset = (int)(inodeTableBlock * BlockSize + (1u % inodesPerGroup) * inodeSize);
        var header = inodeOffset + 40;
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(header + 6, 2)));
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(header + 12 + 6, 2));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(header + 12 + 8, 4));
        var block = checked((int)((((long)startHi << 32) | startLo) * BlockSize));
        return image[block..(block + BlockSize)];
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-name-charset-{prefix}-{Path.GetRandomFileName()}");
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