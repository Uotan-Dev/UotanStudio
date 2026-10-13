using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Compressors;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures the two zstd stream container shapes on a seekable input: one streaming frame versus one
/// frame per 1 MiB chunk compressed across slots, across levels and payload compressibility.
/// <para>在可寻址输入上度量 zstd 流式容器的两种形态：单个流式帧，与按 1 MiB 分块、跨槽并发压缩的多帧，
/// 覆盖不同级别与数据可压缩性。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ZstdStreamChunkBenchmarks
{
    private const int ChunkSize = 1 << 20;

    private byte[] _payload = [];
    private CompressionOptions? _sequential;
    private CompressionOptions? _chunked;

    [Params(3, 9, 19)]
    public int Level { get; set; }

    [Params(16, 64)]
    public int PayloadMebibytes { get; set; }

    [Params(true, false)]
    public bool Compressible { get; set; }

    [Params(true, false)]
    public bool Chunked { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = BuildPayload(PayloadMebibytes * ChunkSize, Compressible);
        _sequential = new CompressionOptions { Level = Level };
        _chunked = new CompressionOptions { Level = Level, MaxDegreeOfParallelism = Environment.ProcessorCount };
    }

    [Benchmark]
    public long Compress()
    {
        using var input = new MemoryStream(_payload, writable: false);
        using var output = new MemoryStream(_payload.Length);
        ZstdCompressor.Compress(input, output, Chunked ? _chunked : _sequential);
        return output.Length;
    }

    private static byte[] BuildPayload(int length, bool compressible)
    {
        var data = new byte[length];
        var seed = 0x12345678u;
        for (var i = 0; i < data.Length; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            data[i] = (byte)(seed >> 24);
        }

        if (!compressible) return data;

        for (var i = 0; i < 4096; i++)
        {
            data[i] = (byte)((i * 7) & 0xFF);
        }

        for (var block = 4096; block < data.Length; block += 4096)
        {
            Array.Copy(data, 0, data, block, 4096);
        }

        return data;
    }
}
