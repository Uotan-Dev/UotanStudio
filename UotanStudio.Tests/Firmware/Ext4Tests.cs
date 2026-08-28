using System.Text;
using UotanStudio.Firmware.Ext4;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EXT4 builder → parser round-trip tests: builds an image from a temporary directory
/// containing files, a sub-directory, a symlink and a large file, then reads it back
/// and asserts the file tree, file contents and sparse-hole zero-filling.
/// <para>EXT4 构建器 → 解析器往返测试：从含文件、子目录、符号链接与大文件的临时目录构建镜像，
/// 再读回并断言文件树、文件内容与稀疏洞补零。</para>
/// </summary>
public class Ext4Tests
{
    private const int BlockSize = 4096;

    private static readonly byte[] BinaryBlob = [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F, 0x10, 0x20, 0x30];

    [Fact]
    public void BuildAndParse_RoundTripsFileTreeContentsAndSymlink()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner content\n");
            var hasSymlink = TryCreateSymlink(Path.Combine(root, "link"), "hello.txt");

            var largeBytes = new byte[3 * BlockSize + 123];
            for (var i = 0; i < largeBytes.Length; i++)
            {
                largeBytes[i] = (byte)(i * 31 + 7);
            }

            File.WriteAllBytes(Path.Combine(root, "large.bin"), largeBytes);

            var image = Ext4Builder.Build(root);
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var expected = new SortedSet<string> { "hello.txt", "blob.bin", "subdir", "subdir/inner.txt", "large.bin" };
            if (hasSymlink)
            {
                expected.Add("link");
            }

            var actual = new SortedSet<string>(parser.ListFiles().Select(static e => e.Path));
            Assert.Equal(expected, actual);

            var entries = parser.ListFiles().ToDictionary(static e => e.Path, StringComparer.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes("hello ext4\n"), ReadFile(parser, "hello.txt"));
            Assert.Equal(BinaryBlob, ReadFile(parser, "blob.bin"));
            Assert.Equal(Encoding.UTF8.GetBytes("inner content\n"), ReadFile(parser, "subdir/inner.txt"));
            Assert.Equal(largeBytes, ReadFile(parser, "large.bin"));

            var subdir = entries["subdir"];
            Assert.Equal(Ext4Parser.Ext4EntryKind.Directory, subdir.Kind);
            Assert.True(subdir.Size > 0);

            if (hasSymlink)
            {
                var link = entries["link"];
                Assert.Equal(Ext4Parser.Ext4EntryKind.Symlink, link.Kind);
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

            var image = Ext4Builder.Build(root, new Ext4Options { EnableSparse = true });
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));

            var readBack = ReadFile(parser, "sparse.bin");
            Assert.Equal(content.Length, readBack.Length);
            Assert.Equal(content, readBack);

            // The all-zero middle block must come back as zeros.
            // 全零中间块读回必须为零。
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
                File.WriteAllBytes(imagePath, Ext4Builder.Build(root));
                var handler = new Ext4FormatHandler();
                Assert.True(handler.CanHandle(imagePath));
                Assert.Equal("EXT4", handler.FormatName);
                Assert.Contains("img", handler.FileExtensions);

                var metadata = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
                Assert.True(metadata.Success);
                Assert.Equal(BlockSize.ToString(), metadata.Metadata["BlockSize"]);
                Assert.True(long.Parse(metadata.Metadata["PartitionSize"]) > 0);
                Assert.True(int.Parse(metadata.Metadata["FileCount"]) >= 2);
                Assert.Contains("EXT4", metadata.Summary);

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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-" + Path.GetRandomFileName());
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

    private static byte[] ReadFile(Ext4Parser parser, string path)
    {
        using var ms = new MemoryStream();
        parser.ReadFile(path, ms);
        return ms.ToArray();
    }
}