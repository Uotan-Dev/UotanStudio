using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.BootSecurity.Avb;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// AVB dm-verity hash-tree build throughput by image size.
/// <para>按镜像大小度量 AVB dm-verity 哈希树的构建吞吐。</para>
/// </summary>
[MemoryDiagnoser]
public class AvbHashtreeBenchmarks
{
    [Params(16, 64, 256)] public int MegaBytes;

    private string _image = "";
    private string _root = "";

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan_bench_hashtree_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _image = Path.Combine(_root, "system.img");
        var bytes = new byte[MegaBytes * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(_image, bytes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public long BuildHashtree()
    {
        using var tree = AvbHashtreeBuilder.Build(_image, [1, 2, 3, 4], 4096, 4096);
        return tree.TreeSize;
    }
}