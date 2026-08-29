using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// F2FS builder → parser round-trip tests: builds an image from a temporary directory
/// containing files, a sub-directory, a symlink and a large file (crossing into direct nodes),
/// then reads it back and asserts the file tree, file contents, sparse-hole zero-filling,
/// handler detection and bound checks on malformed inputs.
/// <para>F2FS 构建器 → 解析器往返测试：从含文件、子目录、符号链接与大文件（跨越 direct 节点）的
/// 临时目录构建镜像，再读回并断言文件树、文件内容、稀疏洞补零、处理器判定与畸形输入的有界性检查。</para>
/// </summary>
public class F2fsTests
{
    private const int BlockSize = 4096;

    private static readonly byte[] BinaryBlob = [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F, 0x10, 0x20, 0x30];

    [Fact]
    public void BuildAndParse_RoundTripsFileTreeContentsAndSymlink()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello f2fs\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner content\n");
            var hasSymlink = TryCreateSymlink(Path.Combine(root, "link"), "hello.txt");

            // 1000 个数据块（>923 个 i_addr 直接指针）以覆盖 direct 节点 1 的寻址路径。
            // 1000 data blocks (>923 direct inode pointers) to exercise the direct-node-1 path.
            var largeBytes = new byte[1000 * BlockSize + 123];
            for (var i = 0; i < largeBytes.Length; i++)
            {
                largeBytes[i] = (byte)(i * 31 + 7);
            }

            File.WriteAllBytes(Path.Combine(root, "large.bin"), largeBytes);

            var image = F2fsBuilder.Build(root);
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024, 4)) == 0xF2F52010u);

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            Assert.Equal(BlockSize, parser.BlockSize);
            Assert.Equal(2L * 1024 * 1024, parser.SegmentSize);
            Assert.Equal("f2fs", parser.VolumeName);

            var expected = new SortedSet<string> { "hello.txt", "blob.bin", "subdir", "subdir/inner.txt", "large.bin" };
            if (hasSymlink)
            {
                expected.Add("link");
            }

            var actual = new SortedSet<string>(parser.ListFiles().Select(static e => e.Path));
            Assert.Equal(expected, actual);

            var entries = parser.ListFiles().ToDictionary(static e => e.Path, StringComparer.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes("hello f2fs\n"), ReadFile(parser, "hello.txt"));
            Assert.Equal(BinaryBlob, ReadFile(parser, "blob.bin"));
            Assert.Equal(Encoding.UTF8.GetBytes("inner content\n"), ReadFile(parser, "subdir/inner.txt"));
            Assert.Equal(largeBytes, ReadFile(parser, "large.bin"));

            var subdir = entries["subdir"];
            Assert.Equal(F2fsParser.F2fsEntryKind.Directory, subdir.Kind);
            Assert.True(subdir.Size > 0);

            if (hasSymlink)
            {
                var link = entries["link"];
                Assert.Equal(F2fsParser.F2fsEntryKind.Symlink, link.Kind);
                Assert.Equal("hello.txt", link.SymlinkTarget);
                Assert.Equal(Encoding.UTF8.GetBytes("hello.txt"), ReadFile(parser, "link"));
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void BuildWithSparse_ReadsHolesBackAsZeros()
    {
        var root = CreateTempDirectory();
        try
        {
            var content = new byte[BlockSize * 3];
            Encoding.ASCII.GetBytes("sparse head\n", content);
            Encoding.ASCII.GetBytes("sparse tail\n", content.AsSpan(BlockSize * 2));
            File.WriteAllBytes(Path.Combine(root, "sparse.bin"), content);

            var image = F2fsBuilder.Build(root, new F2fsOptions { EnableSparse = true });
            using var parser = new F2fsParser(new MemoryStream(image, writable: false));

            var readBack = ReadFile(parser, "sparse.bin");
            Assert.Equal(content.Length, readBack.Length);
            Assert.Equal(content, readBack);

            // 全零中间块读回必须为零。The all-zero middle block must come back as zeros.
            for (var i = BlockSize; i < BlockSize * 2; i++)
            {
                Assert.Equal(0, readBack[i]);
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task Handler_DetectsMetadataAndUnpacksBuiltImage()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "beta");

            var imagePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".img");
            try
            {
                F2fsBuilder.Build(root, imagePath);
                var handler = new F2fsFormatHandler();
                Assert.True(handler.CanHandle(imagePath));
                Assert.Equal("F2FS", handler.FormatName);
                Assert.Contains("img", handler.FileExtensions);
                Assert.Contains("f2fs", handler.FileExtensions);

                var metadata = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
                Assert.True(metadata.Success);
                Assert.Equal(BlockSize.ToString(), metadata.Metadata["BlockSize"]);
                Assert.True(long.Parse(metadata.Metadata["ImageSize"]) > 0);
                Assert.True(int.Parse(metadata.Metadata["FileCount"]) >= 2);
                Assert.Contains("F2FS", metadata.Summary);

                var unpackDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                var unpackResult = await handler.UnpackAsync(imagePath, unpackDirectory, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(unpackResult.Success);
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(unpackDirectory, "a.txt")));
                Assert.Equal("beta", File.ReadAllText(Path.Combine(unpackDirectory, "nested", "b.txt")));
                DeleteRecursive(unpackDirectory);
            }
            finally
            {
                File.Delete(imagePath);
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_RejectsTruncatedImagesWithFormatError()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            var image = F2fsBuilder.Build(root);

            // 超级块区截断（不足 1024+3072 字节）：读取超级块提前 EOF。
            // Truncated below the superblock region: reading the superblock hits early EOF.
            var tiny = new MemoryStream(image, 0, 2000, writable: false);
            Assert.Throws<InvalidDataException>(() => new F2fsParser(tiny));

            // 半截镜像（checkpoint 区越界）。
            // Half image (the checkpoint area lies beyond the truncated length).
            var half = new MemoryStream(image, 0, image.Length / 2, writable: false);
            Assert.Throws<InvalidDataException>(() => new F2fsParser(half));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_RejectsMalformedSuperblock()
    {
        // 无魔数。No magic.
        var noMagic = new byte[8192];
        Assert.Throws<FormatException>(() => new F2fsParser(new MemoryStream(noMagic, writable: false)));

        // 魔数正确但布局自相矛盾（root_ino 落在保留 nid 内）。
        // Valid magic but an inconsistent layout (root ino inside the reserved nid range).
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            var image = F2fsBuilder.Build(root);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 96, 4), 0);
            Assert.Throws<InvalidDataException>(() => new F2fsParser(new MemoryStream(image, writable: false)));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Parser_RejectsOversizedLength()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            var image = F2fsBuilder.Build(root);

            // 根 inode 是主区第一个块；把其 i_size 篡改为超大值。
            // The root inode is the first main-area block; corrupt its i_size to a huge value.
            var mainBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 92, 4));
            var rootInodeOffset = checked((long)mainBlkaddr * BlockSize + 16);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan((int)rootInodeOffset, 8), 0x0000_0200_0000_0000UL);

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            Assert.Throws<InvalidDataException>(() => parser.ListFiles());
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-f2fs-" + Path.GetRandomFileName());
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
            // Symlink creation may require developer mode / elevation on Windows.
            // Windows 上创建符号链接可能需要开发者模式或管理员权限。
            return false;
        }
    }

    private static byte[] ReadFile(F2fsParser parser, string path)
    {
        using var ms = new MemoryStream();
        parser.ReadFile(path, ms);
        return ms.ToArray();
    }
}