using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Extraction overloads for an EROFS image opened from a real file: image ranges are read positionally, so the
/// worker count must not change any extracted byte and a file-backed source must match an in-memory one.
/// <para>针对从真实文件打开的 EROFS 镜像的解包重载：镜像区间按定位读取，因此工作者数量不得改变任何解出的字节，
/// 且文件数据源必须与内存数据源一致。</para>
/// </summary>
[Collection("Firmware concurrency")]
public class ErofsParallelReadIsolationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ExtractToDirectory_FileStreamSource_ReturnsEveryFileByteExact(int workers)
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp);
        var image = BuildImage(source);
        var imagePath = temp.WriteBytes("image.erofs", image);
        var target = Path.Combine(temp.Path, "out-" + workers);
        var expected = ReadDirectory(source);

        var previous = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = workers;
        try
        {
            using var stream = File.OpenRead(imagePath);
            using var parser = new ErofsParser(stream);
            parser.ExtractToDirectory(target);
        }
        finally
        {
            FirmwareParallelism.Workers = previous;
        }

        var actual = ReadDirectory(target);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var entry in expected)
        {
            Assert.Equal(entry.Value, actual[entry.Key]);
        }
    }

    [Fact]
    public void ExtractToDirectory_FileStreamSource_MatchesMemoryStreamSource()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(CreateSource(temp));
        var imagePath = temp.WriteBytes("image.erofs", image);
        var fromMemory = Path.Combine(temp.Path, "memory");
        var fromFile = Path.Combine(temp.Path, "file");

        using (var parser = new ErofsParser(image))
        {
            parser.ExtractToDirectory(fromMemory);
        }

        using (var stream = File.OpenRead(imagePath))
        using (var parser = new ErofsParser(stream))
        {
            parser.ExtractToDirectory(fromFile);
        }

        Assert.Equal(ReadDirectory(fromMemory), ReadDirectory(fromFile));
    }

    [Fact]
    public void EnumerateEntries_FileStreamSource_MatchesMemoryStreamSource()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(CreateSource(temp));
        var imagePath = temp.WriteBytes("image.erofs", image);

        List<string> fromMemory;
        List<string> fromFile;
        using (var parser = new ErofsParser(image))
        {
            fromMemory = [.. parser.EnumerateEntries().Select(entry => entry.Path)];
        }

        using (var stream = File.OpenRead(imagePath))
        using (var parser = new ErofsParser(stream))
        {
            fromFile = [.. parser.EnumerateEntries().Select(entry => entry.Path)];
        }

        Assert.Equal(fromMemory, fromFile);
    }

    private static byte[] BuildImage(string source)
        => new ErofsBuilder().Build(source, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });

    private static string CreateSource(TempDirectory temp)
    {
        var source = Path.Combine(temp.Path, "tree");
        Directory.CreateDirectory(source);
        var random = new Random(0x5EED);
        for (var index = 0; index < 12; index++)
        {
            var length = index % 3 == 0 ? 8 * 1024 : 300 * 1024;
            var payload = new byte[length];
            random.NextBytes(payload);
            for (var i = 0; i < length / 2; i++)
            {
                payload[i] = payload[length - 1 - i];
            }

            File.WriteAllBytes(Path.Combine(source, $"blob-{index}.bin"), payload);
        }

        File.WriteAllText(Path.Combine(source, "tiny.txt"), "tiny");
        return source;
    }

    private static Dictionary<string, byte[]> ReadDirectory(string root)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).StartsWith(".uotan", StringComparison.Ordinal)) continue;
            result[Path.GetRelativePath(root, path)] = File.ReadAllBytes(path);
        }

        return result;
    }
}