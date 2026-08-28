using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EROFS 打包 → 解析往返测试（含压缩路径）。
/// <para>EROFS build → parse round-trip tests (including the compressed path).</para>
/// </summary>
public class ErofsTests
{
    /// <summary>
    /// Writes a text file without a UTF-8 BOM (TempDirectory.Write uses Encoding.UTF8 which emits
    /// a BOM, breaking byte-exact round-trip assertions).
    /// <para>写入不带 UTF-8 BOM 的文本文件（TempDirectory.Write 使用会带 BOM 的 Encoding.UTF8，
    /// 破坏逐字节一致的往返断言）。</para>
    /// </summary>
    private static void WriteText(TempDirectory temp, string relativePath, string content)
    {
        var full = System.IO.Path.Combine(temp.Path, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    [Fact]
    public void BuildParse_Compressed_RoundTripsFilesAndSubdirectories()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs\n");
        temp.WriteBytes("dir/nested.bin", [0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F]);
        WriteText(temp, "dir/sub/inner.txt", "inner content");

        var random = new Random(42);
        var big = new byte[20 * 1024];
        random.NextBytes(big);
        temp.WriteBytes("dir/big.bin", big);

        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400));
        WriteText(temp, "dir/text.txt", text);

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true, CompressionLevel = 3 });
        var parser = new ErofsParser(image);

        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { "dir/big.bin", "dir/nested.bin", "dir/sub/inner.txt", "dir/text.txt", "hello.txt" },
            paths);

        Assert.Equal("hello erofs\n", Encoding.UTF8.GetString(parser.ReadFileBytes("hello.txt")));
        Assert.Equal(big, parser.ReadFileBytes("dir/big.bin"));
        Assert.Equal(new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F }, parser.ReadFileBytes("dir/nested.bin"));
        Assert.Equal(text, Encoding.UTF8.GetString(parser.ReadFileBytes("dir/text.txt")));
        Assert.Equal("inner content", Encoding.UTF8.GetString(parser.ReadFileBytes("dir/sub/inner.txt")));
    }

    [Fact]
    public void BuildParse_Uncompressed_RoundTripsContent()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "alpha");
        WriteText(temp, "b.txt", "beta");

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var parser = new ErofsParser(image);

        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "a.txt", "b.txt" }, paths);
        Assert.Equal("alpha", Encoding.UTF8.GetString(parser.ReadFileBytes("a.txt")));
        Assert.Equal("beta", Encoding.UTF8.GetString(parser.ReadFileBytes("b.txt")));
    }

    [Fact]
    public void BuildParse_LargeFile_AcrossMultipleBlocks()
    {
        using var temp = new TempDirectory();
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 2000));
        WriteText(temp, "large.txt", payload);

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = true, CompressionLevel = 9 });
        var parser = new ErofsParser(image);

        Assert.Equal(payload, Encoding.UTF8.GetString(parser.ReadFileBytes("large.txt")));
        Assert.Equal(payload.Length, parser.EnumerateFiles().Single().Size);
    }

    [Fact]
    public void BuildParse_EmptyDirectory_ReturnsNoFiles()
    {
        using var temp = new TempDirectory();

        var image = new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
        var parser = new ErofsParser(image);

        Assert.Empty(parser.EnumerateFiles());
    }

    [Fact]
    public void Parser_RejectsImageWithoutMagic()
    {
        Assert.Throws<FormatException>(() => new ErofsParser(new byte[4096]));
    }

    [Fact]
    public async Task FormatHandler_CanHandleAndReadMetadata()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "x.txt", "payload");
        var imagePath = System.IO.Path.Combine(temp.Path, "image.erofs");
        File.WriteAllBytes(imagePath, new ErofsBuilder().Build(temp.Path, new ErofsOptions()));

        var handler = new ErofsFormatHandler();
        Assert.True(handler.CanHandle(imagePath));

        var result = await handler.ReadMetadataAsync(imagePath, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("1", result.Metadata["FileCount"]);
        Assert.Contains("EROFS", result.Summary);
    }

    [Fact]
    public async Task FormatHandler_PackAndUnpack_RoundTrip()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "packed/file1.txt", "one");
        temp.WriteBytes("packed/sub/file2.bin", [1, 2, 3, 4, 5]);
        var sourceDir = System.IO.Path.Combine(temp.Path, "packed");
        var imagePath = System.IO.Path.Combine(temp.Path, "out.erofs");
        var unpackDir = System.IO.Path.Combine(temp.Path, "unpacked");

        var handler = new ErofsFormatHandler();
        var pack = await handler.PackAsync(sourceDir, imagePath, TestContext.Current.CancellationToken);
        Assert.True(pack.Success);
        Assert.True(File.Exists(imagePath));

        var unpack = await handler.UnpackAsync(imagePath, unpackDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success);
        Assert.Equal("one", File.ReadAllText(System.IO.Path.Combine(unpackDir, "file1.txt")));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(System.IO.Path.Combine(unpackDir, "sub", "file2.bin")));
    }
}