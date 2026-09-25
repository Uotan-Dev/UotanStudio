using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>compacted 4B 索引写端回环测试：UseCompactedIndexes 打包 → Parser 读回 SHA256 一致。
/// Compacted-4B index writer round-trip: pack with UseCompactedIndexes, read back and compare
/// SHA256 with the source files.</summary>
public class ErofsCompactedIndexesTests
{
    private static void WriteRandom(string path, int size, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[size];
        rng.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void CompactedIndexes_Roundtrip_MatchesSource()
    {
        using var temp = new TempDirectory();
        temp.Write("text.txt", string.Concat(Enumerable.Repeat("The quick brown fox. ", 2000)));
        WriteRandom(Path.Combine(temp.Path, "rand1.bin"), 300_000, 1);
        WriteRandom(Path.Combine(temp.Path, "rand2.bin"), 70_000, 2);
        temp.Write("small.txt", "tiny");

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = true,
            CompressionLevel = 1,
            UseCompactedIndexes = true,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var entries = parser.EnumerateFiles();
        Assert.Contains(entries, e => e.Path == "text.txt");
        var failures = new List<string>();
        foreach (var e in entries)
        {
            if (e.IsSymlink)
            {
                continue;
            }

            var expected = File.ReadAllBytes(Path.Combine(temp.Path, e.Path.Replace('/', Path.DirectorySeparatorChar)));
            var actual = parser.ReadFileBytes(e.Path);
            var a = Convert.ToHexString(SHA256.HashData(expected));
            var b = Convert.ToHexString(SHA256.HashData(actual));
            if (a != b)
            {
                failures.Add($"{e.Path}: {a} != {b}");
            }
        }

        Assert.Empty(failures);
    }
}