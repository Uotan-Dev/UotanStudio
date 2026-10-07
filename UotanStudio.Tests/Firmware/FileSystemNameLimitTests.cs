using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Directory-entry name limits for the three packable filesystems: EXT4_NAME_LEN, EROFS_NAME_LEN and
/// F2FS_NAME_LEN are all 255 <em>bytes</em> because names are stored as UTF-8, so a CJK name is legal up
/// to 85 characters (85 × 3 = 255) and must be rejected from 86 characters (258 bytes) upward. These tests
/// round-trip in-limit Unicode names through every builder and parser pair, and pin that an over-limit name
/// fails with a UTF-8 byte measurement before an image is published instead of wrapping the on-disk length
/// field, emitting a directory the reader lists as empty, or writing the unrepresentable name.
/// <para>三种可打包文件系统的目录项名称上限：EXT4_NAME_LEN、EROFS_NAME_LEN 与 F2FS_NAME_LEN 都是 255
/// <em>字节</em>，因为名称按 UTF-8 存放，所以中文名称在 85 个字符（85 × 3 = 255）内合法，从 86 个字符
/// （258 字节）起必须被拒绝。本组测试让三种构建器与解析器组合往返校验上限内的 Unicode 名称，并钉住超限名称
/// 会在镜像发布之前按 UTF-8 字节数失败，而不是让磁盘长度字段回绕、写出解析器读为空目录的目录块，
/// 或把无法表示的名称直接写入镜像。</para>
/// </summary>
public class FileSystemNameLimitTests
{
    private const int NameLimitBytes = 255;

    [Fact]
    public void Build_CjkNamesWithinByteLimit_RoundTripsInAllThreeFormats()
    {
        var root = CreateTempDirectory("source");
        var outputDirectory = CreateTempDirectory("roundtrip-output");
        try
        {
            var maximumName = new string('文', 85);
            var maximumNameWithExtension = new string('文', 83) + ".txt";
            var spacedName = "启动日志 2026-01-02 记录.txt";
            var directoryName = "配置目录";
            var nestedName = "内部文件.txt";
            var nestedPath = directoryName + "/" + nestedName;
            Assert.Equal(NameLimitBytes, Encoding.UTF8.GetByteCount(maximumName));
            Assert.Equal(83 * 3 + 4, Encoding.UTF8.GetByteCount(maximumNameWithExtension));
            Assert.True(Encoding.UTF8.GetByteCount(spacedName) < NameLimitBytes);

            var maximumContent = Encoding.UTF8.GetBytes("正好二百五十五字节的名称");
            var nestedContent = Encoding.UTF8.GetBytes("中文目录里的文件");
            File.WriteAllBytes(Path.Combine(root, maximumName), maximumContent);
            File.WriteAllText(Path.Combine(root, maximumNameWithExtension), "扩展名之后的上限内名称");
            File.WriteAllText(Path.Combine(root, spacedName), "空格与 ASCII 数字混排");
            Directory.CreateDirectory(Path.Combine(root, directoryName));
            File.WriteAllBytes(Path.Combine(root, directoryName, nestedName), nestedContent);

            var expected = SortedPaths([maximumName, maximumNameWithExtension, spacedName, nestedPath]);
            var ext4ImagePath = Path.Combine(outputDirectory, "names.ext4");
            var erofsImagePath = Path.Combine(outputDirectory, "names.erofs");
            var f2fsImagePath = Path.Combine(outputDirectory, "names.f2fs");

            Ext4Builder.Build(root, ext4ImagePath);
            new ErofsBuilder().Build(root, erofsImagePath, new ErofsOptions { Compress = false });
            F2fsBuilder.Build(root, f2fsImagePath);

            using (var parser = new Ext4Parser(ext4ImagePath))
            {
                var paths = parser.ListFiles()
                    .Where(static entry => entry.Kind != Ext4Parser.Ext4EntryKind.Directory)
                    .Select(static entry => entry.Path);
                Assert.Equal(expected, SortedPaths(paths));
                Assert.Equal(maximumContent, ReadExt4(parser, maximumName));
                Assert.Equal(nestedContent, ReadExt4(parser, nestedPath));
            }

            using (var parser = new ErofsParser(erofsImagePath))
            {
                var paths = parser.EnumerateFiles().Select(static entry => entry.Path);
                Assert.Equal(expected, SortedPaths(paths));
                Assert.Equal(maximumContent, parser.ReadFileBytes(maximumName));
                Assert.Equal(nestedContent, parser.ReadFileBytes(nestedPath));
            }

            using (var parser = new F2fsParser(f2fsImagePath))
            {
                var paths = parser.ListFiles()
                    .Where(static entry => entry.Kind != F2fsParser.F2fsEntryKind.Directory)
                    .Select(static entry => entry.Path);
                Assert.Equal(expected, SortedPaths(paths));
                Assert.Equal(maximumContent, ReadF2fs(parser, maximumName));
                Assert.Equal(nestedContent, ReadF2fs(parser, nestedPath));
            }
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void Ext4Builder_CjkNameOverByteLimit_ThrowsBeforeWriting()
    {
        var root = CreateTempDirectory("ext4-over");
        var outputDirectory = CreateTempDirectory("ext4-over-output");
        try
        {
            var overLimitName = new string('文', 90) + ".txt";
            File.WriteAllText(Path.Combine(root, overLimitName), "超出名称上限的中文文件名");
            var imagePath = Path.Combine(outputDirectory, "over-limit.ext4");

            var error = Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(root, imagePath));

            Assert.Contains("EXT4 file name is not representable", error.Message);
            Assert.Contains("255", error.Message);
            Assert.Contains("UTF-8 bytes", error.Message);
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
    public void ErofsBuilder_CjkNameOverByteLimit_ThrowsBeforeWriting()
    {
        var root = CreateTempDirectory("erofs-over");
        var outputDirectory = CreateTempDirectory("erofs-over-output");
        try
        {
            var overLimitName = new string('文', 90) + ".txt";
            File.WriteAllText(Path.Combine(root, overLimitName), "超出名称上限的中文文件名");
            var imagePath = Path.Combine(outputDirectory, "over-limit.erofs");

            var error = Assert.Throws<InvalidOperationException>(
                () => new ErofsBuilder().Build(root, imagePath, new ErofsOptions { Compress = false }));

            Assert.Contains("EROFS file name is not representable", error.Message);
            Assert.Contains("255", error.Message);
            Assert.Contains("UTF-8 bytes", error.Message);
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
    public void F2fsBuilder_CjkNameOverByteLimit_ThrowsBeforeWriting()
    {
        var root = CreateTempDirectory("f2fs-over");
        var outputDirectory = CreateTempDirectory("f2fs-over-output");
        try
        {
            var overLimitName = new string('文', 90) + ".txt";
            File.WriteAllText(Path.Combine(root, overLimitName), "超出名称上限的中文文件名");
            var imagePath = Path.Combine(outputDirectory, "over-limit.f2fs");

            var error = Assert.Throws<InvalidOperationException>(() => F2fsBuilder.Build(root, imagePath));

            Assert.Contains("F2FS file name is not representable", error.Message);
            Assert.Contains("255", error.Message);
            Assert.Contains("UTF-8 bytes", error.Message);
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
    public void Ext4Builder_CjkNameOverByteLimit_ReportedInUtf8BytesNotCharacters()
    {
        var root = CreateTempDirectory("ext4-units");
        var outputDirectory = CreateTempDirectory("ext4-units-output");
        try
        {
            var overLimitName = new string('文', 90) + ".txt";
            var utf8ByteCount = Encoding.UTF8.GetByteCount(overLimitName);
            Assert.Equal(90 * 3 + 4, utf8ByteCount);
            Assert.NotEqual(overLimitName.Length, utf8ByteCount);
            File.WriteAllText(Path.Combine(root, overLimitName), "字节计数而非字符计数");
            var imagePath = Path.Combine(outputDirectory, "units.ext4");

            var error = Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(root, imagePath));

            Assert.Contains($"{utf8ByteCount} UTF-8 bytes exceed the {NameLimitBytes}-byte name limit", error.Message);
            Assert.DoesNotContain($"{overLimitName.Length} UTF-8 bytes", error.Message);
            Assert.Contains(overLimitName, error.Message);
            Assert.False(File.Exists(imagePath));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    [Fact]
    public void Build_AccentedAndCjkNamesAcrossSubdirectories_RoundTrips()
    {
        var root = CreateTempDirectory("accents");
        var outputDirectory = CreateTempDirectory("accents-output");
        try
        {
            var directoryName = "外部 目录";
            var names = new[] { "café.txt", "中文说明.md", "Straße.cfg", "日本語 テスト.txt" };
            Directory.CreateDirectory(Path.Combine(root, directoryName));
            var expected = new string[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                File.WriteAllText(Path.Combine(root, directoryName, names[i]), names[i]);
                expected[i] = directoryName + "/" + names[i];
                Assert.True(Encoding.UTF8.GetByteCount(names[i]) <= NameLimitBytes);
            }

            var imagePath = Path.Combine(outputDirectory, "accents.ext4");
            Ext4Builder.Build(root, imagePath);

            using var parser = new Ext4Parser(imagePath);
            var paths = parser.ListFiles()
                .Where(static entry => entry.Kind != Ext4Parser.Ext4EntryKind.Directory)
                .Select(static entry => entry.Path);
            Assert.Equal(SortedPaths(expected), SortedPaths(paths));
            foreach (var path in expected)
            {
                var name = path[(directoryName.Length + 1)..];
                Assert.Equal(name, Encoding.UTF8.GetString(ReadExt4(parser, path)));
            }
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(outputDirectory);
        }
    }

    private static string[] SortedPaths(IEnumerable<string> paths)
        => [.. paths.OrderBy(static path => path, StringComparer.Ordinal)];

    private static byte[] ReadExt4(Ext4Parser parser, string path)
    {
        using var buffer = new MemoryStream();
        parser.ReadFile(path, buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadF2fs(F2fsParser parser, string path)
    {
        using var buffer = new MemoryStream();
        parser.ReadFile(path, buffer);
        return buffer.ToArray();
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-name-limit-{prefix}-{Path.GetRandomFileName()}");
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