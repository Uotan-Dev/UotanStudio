using System.Buffers.Binary;
using System.IO.Hashing;
using BenchmarkDotNet.Attributes;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures feeding a CRC32 accumulator for an expanded sparse FILL region at several pattern-table widths,
/// which is what decides the cost of writing a checksummed sparse image.
/// <para>度量以不同模式表宽度把已展开稀疏 FILL 区域喂给 CRC32 累加器的成本，该成本决定写带校验稀疏镜像的开销。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class SparseFillCrcBenchmarks
{
    private byte[] _table = [];
    private long _totalBytes;

    [Params(8 * 1024 * 1024)]
    public int TotalMebibytes { get; set; }

    [Params(4, 4096, 65536)]
    public int PatternWidth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _totalBytes = TotalMebibytes * 1024L * 1024L;
        _table = new byte[PatternWidth];
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, 0xDEADBEEF);
        for (var offset = 0; offset + 4 <= PatternWidth; offset += 4)
        {
            pattern.CopyTo(_table.AsSpan(offset, 4));
        }
    }

    [Benchmark(Description = "AppendFillCrc")]
    public uint Append()
    {
        var crc = new Crc32();
        var remaining = _totalBytes;
        while (remaining > 0)
        {
            var size = (int)Math.Min(PatternWidth, remaining);
            crc.Append(_table.AsSpan(0, size));
            remaining -= size;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(crc.GetCurrentHash());
    }
}
