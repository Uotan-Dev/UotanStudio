using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Builds a single-partition REPLACE payload across worker counts and codec selections, which separates the
/// chunk fan-out gain from the serial per-partition hash chain that bounds it.
/// <para>以不同工作者数量与编解码选择构建单分区 REPLACE 载荷，用以把分块并发收益与限制它的分区级串行哈希链区分开。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class PayloadSlotReuseBenchmarks
{
    private byte[] _image = [];

    [Params(1, 2, 4, 8)]
    public int Workers { get; set; }

    [Params(CompressionType.None, CompressionType.Zstd, CompressionType.BZip2)]
    public CompressionType Compression { get; set; }

    [Params(8, 32)]
    public int ImageMebibytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var length = ImageMebibytes * 1024 * 1024;
        _image = new byte[length];
        var random = new Random(0x5EED);
        random.NextBytes(_image);
        var pattern = "uotan payload benchmark block pattern"u8.ToArray();
        for (var i = length / 2; i < length; i++)
        {
            _image[i] = pattern[i % pattern.Length];
        }
    }

    [Benchmark]
    public async Task<long> Build()
    {
        using var image = new MemoryStream(_image, writable: false);
        using var payload = new MemoryStream();
        using var builder = new PayloadBuilder().WithCompressionWorkers(Workers);
        await builder.AddPartitionCompressed("system", image, Compression).BuildAsync(payload, CancellationToken.None);
        return payload.Length;
    }
}