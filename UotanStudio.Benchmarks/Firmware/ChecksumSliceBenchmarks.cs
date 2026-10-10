using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Core.Checksums;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures the produced slice-by-8 CRC implementations against the original byte-at-a-time reference, which is
/// what decides whether the checksum fast path keeps paying for itself after later changes.
/// <para>度量改造后的 slice-by-8 CRC 实现与原始逐字节参考实现的差异，该成本决定了校验和快路径在后续改动后
/// 是否仍然划算。</para>
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release --project UotanStudio.Benchmarks -- --filter "*ChecksumSlice*" --job short</c>.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ChecksumSliceBenchmarks
{
    private byte[] _data = [];

    [Params(16, 64, 256)]
    public int TotalMebibytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[TotalMebibytes * 1024 * 1024];
        Random.Shared.NextBytes(_data);
    }

    /// <summary>
    /// Measures the current <see cref="Crc32c"/> implementation over the whole buffer.
    /// <para>度量当前 <see cref="Crc32c"/> 实现在整个缓冲上的表现。</para>
    /// </summary>
    /// <returns>The checksum register value. <para>校验和寄存器值。</para></returns>
    [Benchmark(Baseline = true)]
    public uint Crc32cCurrent() => Crc32c.Compute(_data);

    /// <summary>
    /// Measures the byte-at-a-time reference <see cref="Crc32c"/> over the whole buffer.
    /// <para>度量逐字节参考实现 <see cref="Crc32c"/> 在整个缓冲上的表现。</para>
    /// </summary>
    /// <returns>The checksum register value. <para>校验和寄存器值。</para></returns>
    [Benchmark]
    public uint Crc32cByteWise() => Crc32cByteWiseReference(_data);

    /// <summary>
    /// Measures the current <see cref="Crc32"/> implementation over the whole buffer.
    /// <para>度量当前 <see cref="Crc32"/> 实现在整个缓冲上的表现。</para>
    /// </summary>
    /// <returns>The checksum register value. <para>校验和寄存器值。</para></returns>
    [Benchmark]
    public uint Crc32Current() => Crc32.Compute(_data, 0xFFFF_FFFFu);

    /// <summary>
    /// Measures the byte-at-a-time reference <see cref="Crc32"/> over the whole buffer.
    /// <para>度量逐字节参考实现 <see cref="Crc32"/> 在整个缓冲上的表现。</para>
    /// </summary>
    /// <returns>The checksum register value. <para>校验和寄存器值。</para></returns>
    [Benchmark]
    public uint Crc32ByteWise() => Crc32ByteWiseReference(_data, 0xFFFF_FFFFu);

    private static uint Crc32cByteWiseReference(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFFu)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint Crc32ByteWiseReference(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] Crc32cTable = BuildCrc32cTable();
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32cTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
