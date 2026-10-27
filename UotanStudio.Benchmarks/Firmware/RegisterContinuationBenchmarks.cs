using BenchmarkDotNet.Attributes;
using UotanStudio.Compress.Codecs.Xz;
using UotanStudio.Hash.Crc16CcittFalse;
using UotanStudio.Hash.Crc32Register;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures the register-continuation and small-checksum optimizations against the scalar software paths that do the same work byte by byte:
/// a non-standard CRC-32 seed over a block-shaped buffer (the AMLogic/F2FS continuation shape), the RKDL
/// CRC-16 over loader-sized frames, and the inverse XZ delta filter over a 4 MiB decode chunk.
/// <para>度量寄存器续算与小型校验和优化相对逐字节软件路径的收益：非标准 CRC-32 seed 对块状缓冲（AMLogic/F2FS 续算
/// 形态）、RKDL CRC-16 对 loader 级帧、逆 XZ 差分过滤器对 4 MiB 解码块。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class RegisterContinuationBenchmarks
{
    private byte[] _block = [];
    private byte[] _frame = [];
    private byte[] _delta = [];

    [GlobalSetup]
    public void Setup()
    {
        _block = new byte[4 * 1024 * 1024];
        _frame = new byte[1024 * 1024];
        _delta = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(_block);
        Random.Shared.NextBytes(_frame);
        Random.Shared.NextBytes(_delta);
    }

    /// <summary>
    /// The optimized continuation path: any seed over 128 KiB reaches the hardware CRC through the zero-run matrix.
    /// <para>优化后的续算路径：任意 seed 超过 128 KiB 即经零串矩阵到达硬件 CRC。</para>
    /// </summary>
    [Benchmark(Baseline = true)]
    public uint Crc32Continued() => Crc32Register.Compute(_block, 0xDEAD_BEEFu);

    /// <summary>
    /// The software slice-by-8 walk that served every non-standard seed before the hardware path existed.
    /// <para>硬件路径出现之前服务所有非标准 seed 的 slice-by-8 软件查表。</para>
    /// </summary>
    [Benchmark]
    public uint Crc32ContinuedSoftware() => Crc32Register.ComputeSoftware(_block, 0xDEAD_BEEFu);

    /// <summary>
    /// The slice-by-8 CRC-16 that backs the RKDL download frames.
    /// <para>支撑 RKDL 下载帧的 slice-by-8 CRC-16。</para>
    /// </summary>
    [Benchmark]
    public ushort Crc16Frame() => Crc16CcittFalse.Compute(_frame);

    /// <summary>
    /// The byte-at-a-time CRC-16 that served the RKDL download frames before the table slice existed.
    /// <para>查表切片出现之前服务 RKDL 下载帧的逐字节 CRC-16。</para>
    /// </summary>
    [Benchmark]
    public ushort Crc16FrameByteWise() => Crc16ByteWiseReference(_frame);

    /// <summary>
    /// The vectorized inverse XZ delta filter (distance 32) over one 4 MiB decode chunk.
    /// <para>向量化逆 XZ 差分过滤器（步长 32）处理一个 4 MiB 解码块。</para>
    /// </summary>
    [Benchmark]
    public int DeltaVectorized()
    {
        var filter = new XzDeltaFilter(32);
        return filter.Transform(_delta, _delta.Length, 0);
    }

    /// <summary>
    /// The byte-at-a-time reference of the same decode, which the whole output ran through before vectorization.
    /// <para>同一解码的逐字节参考实现，向量化之前全部输出都走这条路径。</para>
    /// </summary>
    [Benchmark]
    public int DeltaScalar()
    {
        var filter = new XzDeltaFilter(32);
        filter.TransformScalar(_delta, _delta.Length);
        return _delta.Length;
    }

    /// <summary>
    /// The byte-at-a-time table walk that served the RKDL download frames before the slice existed.
    /// <para>查表切片出现之前服务 RKDL 下载帧的逐字节查表实现。</para>
    /// </summary>
    [Benchmark]
    public ushort Crc16FrameTableByteWise() => Crc16TableByteWiseReference(_frame);

    private static ushort Crc16TableByteWiseReference(ReadOnlySpan<byte> data)
    {
        var table = BuildCrc16Table();
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ table[(byte)(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (var value = 0; value < 256; value++)
        {
            var entry = (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 0x8000) != 0 ? (ushort)((entry << 1) ^ 0x1021) : (ushort)(entry << 1);
            }

            table[value] = entry;
        }

        return table;
    }

    private static ushort Crc16ByteWiseReference(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}