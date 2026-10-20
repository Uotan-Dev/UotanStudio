using BenchmarkDotNet.Attributes;
using UotanStudio.Compress.Codecs.Lzo;

namespace UotanStudio.Benchmarks.Compression;

/// <summary>
/// LZO1X decode cost with a caller-owned destination buffer and with an allocated result, which is the pair of
/// shapes SquashFS and F2FS drive per compressed block.
/// <para>LZO1X 在「调用方自有目标缓冲区」与「返回新分配结果」两种形态下的解码开销，即 SquashFS 与 F2FS 逐压缩块驱动的两种调用形态。</para>
/// </summary>
/// <remarks>
/// The decode contract leaves the destination untouched when the bitstream is rejected, so the measurement covers
/// the bookkeeping that contract implies rather than only the happy path.
/// <para>解码契约要求位流被拒绝时目标缓冲区保持不变，因此测量覆盖该契约带来的记账开销，而不只覆盖成功路径。</para>
/// </remarks>
[MemoryDiagnoser]
public class LzoBenchmarks
{
    private byte[] _compressed = [];
    private byte[] _destination = [];
    private int _size;

    /// <summary>
    /// Gets or sets the uncompressed payload size in bytes; the compressed bitstream is derived from it.
    /// <para>获取或设置未压缩载荷的字节数；压缩位流由该尺寸推导。</para>
    /// </summary>
    [Params(64 * 1024, 1024 * 1024)]
    public int Size { get; set; }

    /// <summary>
    /// Builds the mixed-compressibility payload and its LZO1X bitstream once per parameter set.
    /// <para>按参数组一次性构造混合可压缩性载荷及其 LZO1X 位流。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _size = Size;
        var data = new byte[_size];
        for (var offset = 0; offset < data.Length; offset += 4096)
        {
            for (var i = 0; i < 2048 && offset + i < data.Length; i++)
            {
                data[offset + i] = (byte)(i % 61);
            }
        }

        _compressed = Lzo1x.Compress(data);
        _destination = new byte[_size];
    }

    /// <summary>
    /// Decodes into a reused caller-owned buffer, the shape SquashFS uses per compressed block.
    /// <para>解码到复用的调用方缓冲区，即 SquashFS 逐压缩块使用的形态。</para>
    /// </summary>
    /// <returns>The bytes the decoder reported producing. <para>解码器报告的产出字节数。</para></returns>
    [Benchmark]
    public int TryDecompress_ReusedBuffer()
    {
        Lzo1x.TryDecompress(_compressed, _destination, out var written);
        return written;
    }

    /// <summary>
    /// Decodes through the overload that allocates and returns the result, the shape F2FS uses per cluster.
    /// <para>经「分配并返回结果」的重载解码，即 F2FS 逐簇使用的形态。</para>
    /// </summary>
    /// <returns>The decoded byte count. <para>解码得到的字节数。</para></returns>
    [Benchmark]
    public int Decompress_AllocatesResult() => Lzo1x.Decompress(_compressed, _size).Length;
}
