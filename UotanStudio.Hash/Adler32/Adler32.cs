using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace UotanStudio.Hash.Adler32;

/// <summary>
/// Adler-32 checksum computation used by zlib-style payloads, reduced modulo <c>65521</c> in blocks of <c>5552</c> bytes.
/// <para>用于 zlib 风格载荷的 Adler-32 校验实现，按 <c>5552</c> 字节分块并对模数 <c>65521</c> 取余。</para>
/// </summary>
/// <remarks>
/// On x86 the addition runs through <c>pmaddubsw</c>/<c>pmaddwd</c> over 32-byte blocks with AVX2, or 64-byte blocks
/// with AVX512BW under a narrower <c>5504</c>-byte reduction window, on ARM64 with AdvSIMD through widening adds and
/// lane multiplies over the same 32-byte blocks, and every other processor runs the byte-at-a-time loop. All forms
/// produce the same value for every length and starting value, and the per-form block boundaries keep the <c>b</c>
/// accumulator inside 32 bits.
/// <para>x86 上经 <c>pmaddubsw</c>/<c>pmaddwd</c> 分块累加：AVX2 用 32 字节块，AVX512BW 用 64 字节块并以更窄的
/// <c>5504</c> 字节取余窗口；ARM64 且支持 AdvSIMD 时，经加宽加法与通道乘法处理相同的 32 字节块；其余处理器执行
/// 逐字节循环。各种形态对任意长度与初值都给出相同结果，且各形态的分块边界使 <c>b</c> 累加量都留在 32 位内。</para>
/// </remarks>
public static class Adler32
{
    private const uint Mod = 65521;
    private const int Nmax = 5552;
    private const int NmaxAvx512 = 5504;
    private const int VectorBlockBytes = 32;
    private const int Vector512BlockBytes = 64;
    private const int MinimumVectorizedBytes = 64;

    /// <summary>
    /// Whether the processor offers a supported vector ISA for the block form: AVX512BW or AVX2 or SSSE3 on x86, or
    /// AdvSIMD on ARM64.
    /// <para>处理器是否提供分块形态所需的向量 ISA：x86 上为 AVX512BW、AVX2 或 SSSE3，ARM64 上为 AdvSIMD。</para>
    /// </summary>
    private static bool CanVectorize =>
        Avx512BW.IsSupported || Avx2.IsSupported || (Sse2.IsSupported && Ssse3.IsSupported) || AdvSimd.IsSupported;

    /// <summary>
    /// The signed byte weights for the low 16 bytes of a 32-byte block, <c>32</c> down to <c>17</c>.
    /// <para>32 字节块低 16 字节的有符号权重，从 <c>32</c> 递减到 <c>17</c>。</para>
    /// </summary>
    private static readonly Vector128<sbyte> WeightLow =
        Vector128.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17);

    /// <summary>
    /// The signed byte weights for the high 16 bytes of a 32-byte block, <c>16</c> down to <c>1</c>.
    /// <para>32 字节块高 16 字节的有符号权重，从 <c>16</c> 递减到 <c>1</c>。</para>
    /// </summary>
    private static readonly Vector128<sbyte> WeightHigh =
        Vector128.Create((sbyte)16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);

    /// <summary>
    /// The <c>1</c> multiplier that turns <c>pmaddubsw</c> into pairwise byte sums.
    /// <para>把 <c>pmaddubsw</c> 变为成对字节求和的 <c>1</c> 乘数。</para>
    /// </summary>
    private static readonly Vector128<sbyte> Ones8 = Vector128.Create((sbyte)1);

    /// <summary>
    /// The <c>1</c> multiplier that widens pairwise 16-bit sums into 32-bit lanes.
    /// <para>把成对 16 位和加宽为 32 位通道的 <c>1</c> 乘数。</para>
    /// </summary>
    private static readonly Vector128<short> Ones16 = Vector128.Create((short)1);

    /// <summary>
    /// The unsigned lane weights for block byte positions <c>0</c> to <c>7</c>, <c>32</c> down to <c>25</c>.
    /// <para>块内字节位置 <c>0</c> 到 <c>7</c> 的无符号通道权重，从 <c>32</c> 递减到 <c>25</c>。</para>
    /// </summary>
    private static readonly Vector128<ushort> WeightLanes0To7 = Vector128.Create((ushort)32, 31, 30, 29, 28, 27, 26, 25);

    /// <summary>
    /// The unsigned lane weights for block byte positions <c>8</c> to <c>15</c>, <c>24</c> down to <c>17</c>.
    /// <para>块内字节位置 <c>8</c> 到 <c>15</c> 的无符号通道权重，从 <c>24</c> 递减到 <c>17</c>。</para>
    /// </summary>
    private static readonly Vector128<ushort> WeightLanes8To15 = Vector128.Create((ushort)24, 23, 22, 21, 20, 19, 18, 17);

    /// <summary>
    /// The unsigned lane weights for block byte positions <c>16</c> to <c>23</c>, <c>16</c> down to <c>9</c>.
    /// <para>块内字节位置 <c>16</c> 到 <c>23</c> 的无符号通道权重，从 <c>16</c> 递减到 <c>9</c>。</para>
    /// </summary>
    private static readonly Vector128<ushort> WeightLanes16To23 = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);

    /// <summary>
    /// The unsigned lane weights for block byte positions <c>24</c> to <c>31</c>, <c>8</c> down to <c>1</c>.
    /// <para>块内字节位置 <c>24</c> 到 <c>31</c> 的无符号通道权重，从 <c>8</c> 递减到 <c>1</c>。</para>
    /// </summary>
    private static readonly Vector128<ushort> WeightLanes24To31 = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);

    /// <summary>
    /// The signed byte weights for one 32-byte AVX2 block, <c>32</c> down to <c>1</c>; the largest pair sum
    /// <c>32 * 255 + 31 * 255 = 16065</c> stays inside the <c>pmaddubsw</c> saturation bound.
    /// <para>一个 32 字节 AVX2 块的有符号字节权重，从 <c>32</c> 递减到 <c>1</c>；最大对和
    /// <c>32 * 255 + 31 * 255 = 16065</c> 留在 <c>pmaddubsw</c> 的饱和界内。</para>
    /// </summary>
    private static readonly Vector256<sbyte> Weight256 =
        Vector256.Create(
            Vector128.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17),
            Vector128.Create((sbyte)16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1));

    /// <summary>
    /// The <c>1</c> multipliers that turn <c>pmaddubsw</c> and <c>pmaddwd</c> into pairwise sums on 256-bit lanes.
    /// <para>把 256 位通道上的 <c>pmaddubsw</c> 与 <c>pmaddwd</c> 变为成对求和的 <c>1</c> 乘数。</para>
    /// </summary>
    private static readonly Vector256<sbyte> Ones8_256 = Vector256.Create(Ones8, Ones8);

    private static readonly Vector256<short> Ones16_256 = Vector256.Create(Ones16, Ones16);

    /// <summary>
    /// The signed byte weights for one 64-byte AVX512BW block, <c>64</c> down to <c>1</c>; the largest pair sum
    /// <c>64 * 255 + 63 * 255 = 32385</c> stays inside the <c>pmaddubsw</c> saturation bound.
    /// <para>一个 64 字节 AVX512BW 块的有符号字节权重，从 <c>64</c> 递减到 <c>1</c>；最大对和
    /// <c>64 * 255 + 63 * 255 = 32385</c> 留在 <c>pmaddubsw</c> 的饱和界内。</para>
    /// </summary>
    private static readonly Vector512<sbyte> Weight512 = Vector512.Create(
        (sbyte)64, 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49,
        48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33,
        32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
        16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);

    private static readonly Vector512<sbyte> Ones8_512 = Vector512.Create(Ones8_256, Ones8_256);

    private static readonly Vector512<short> Ones16_512 = Vector512.Create(Ones16_256, Ones16_256);

    /// <summary>
    /// Computes the Adler-32 checksum of the given data.
    /// <para>计算数据的 Adler-32 校验和。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span yields the initial value <c>1</c>.
    /// <para>参与计算的字节；空区间返回初始值 <c>1</c>。</para>
    /// </param>
    /// <returns>
    /// The 32-bit Adler-32 value with the <c>b</c> accumulation in the high half, reduced modulo <c>65521</c>.
    /// <para>32 位 Adler-32 值，高半部为 <c>b</c> 累加结果，均按模数 <c>65521</c> 取余。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        if (CanVectorize && data.Length >= MinimumVectorizedBytes)
        {
            return ComputeVectorized(data);
        }

        return ComputeScalar(data);
    }

    /// <summary>
    /// Computes the Adler-32 checksum of a byte array slice.
    /// <para>计算字节数组切片的 Adler-32 校验和。</para>
    /// </summary>
    /// <param name="data">Source array; the slice is read without copying.
    /// <para>源数组；读取切片时不进行复制。</para></param>
    /// <param name="offset">Index of the first byte to include.<para>参与计算的首字节索引。</para></param>
    /// <param name="length">Number of bytes to include.<para>参与计算的字节数。</para></param>
    /// <returns>The 32-bit Adler-32 value computed over the requested slice.
    /// <para>对指定切片计算得到的 32 位 Adler-32 值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative, or the range exceeds the array.
    /// <para><paramref name="offset"/> 或 <paramref name="length"/> 为负，或区间超出数组边界。</para></exception>
    public static uint Compute(byte[] data, int offset, int length)
        => Compute(data.AsSpan(offset, length));

    /// <summary>
    /// Computes the Adler-32 checksum of the data continuing from <paramref name="previousResult"/>. To checksum a stream in
    /// chunks, pass the previous return value as the next <paramref name="previousResult"/>.
    /// <para>在 <paramref name="previousResult"/> 的基础上继续计算数据的 Adler-32 校验和。分块计算流数据时，把上一次的
    /// 返回值作为下一次的 <paramref name="previousResult"/> 传入即可续算。</para>
    /// </summary>
    /// <param name="data">Bytes to hash; an empty span returns <paramref name="previousResult"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="previousResult"/>。</para></param>
    /// <param name="previousResult">Packed Adler-32 value of every earlier byte, holding <c>a</c> in the low half and <c>b</c>
    /// in the high half; the value <c>1</c> starts a new checksum.
    /// <para>已并入的全部字节的打包 Adler-32 值，低半部为 <c>a</c>，高半部为 <c>b</c>；取值 <c>1</c> 表示重新开始计算。</para></param>
    /// <returns>The Adler-32 value of the bytes already covered by <paramref name="previousResult"/> followed by
    /// <paramref name="data"/>, identical to <see cref="Compute(ReadOnlySpan{byte})"/> over the concatenated bytes.
    /// <para>返回 <paramref name="previousResult"/> 已覆盖字节后接 <paramref name="data"/> 的 Adler-32 值，与对拼接后的
    /// 完整字节调用 <see cref="Compute(ReadOnlySpan{byte})"/> 的结果一致。</para></returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint previousResult)
    {
        if (data.IsEmpty)
        {
            return previousResult;
        }

        var added = Compute(data);
        var carry = (long)(previousResult & 0xFFFF) - 1;
        var low = ((added & 0xFFFF) + carry) % Mod;
        var high = ((added >> 16) + (previousResult >> 16) + (long)data.Length * carry) % Mod;
        return (uint)((high << 16) | low);
    }

    /// <summary>
    /// The portable byte-at-a-time reference implementation, also the fallback when no vector ISA is available.
    /// <para>可移植的逐字节参考实现，同时是所有向量 ISA 不可用时的回退路径。</para>
    /// </summary>
    /// <param name="data">Bytes to hash. <para>参与计算的字节。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    internal static uint ComputeScalar(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + Nmax, data.Length);
            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// The vectorized form: AVX512BW 64-byte blocks, AVX2 32-byte blocks, SSSE3 32-byte blocks on x86, AdvSIMD blocks
    /// on ARM64, and the scalar reference result when no vector ISA is present.
    /// <para>向量化形态：x86 上依次走 AVX512BW 64 字节块、AVX2 32 字节块、SSSE3 32 字节块，ARM64 上走 AdvSIMD 分块，
    /// 无向量 ISA 时返回标量参考结果。</para>
    /// </summary>
    /// <param name="data">Bytes to hash; any length, short inputs fall through to the byte loop.
    /// <para>参与计算的字节；任意长度，过短的输入落入逐字节循环。</para></param>
    /// <returns>The Adler-32 value of the data, identical to <see cref="ComputeScalar"/> for every length.
    /// <para>数据的 Adler-32 值，任意长度下都与 <see cref="ComputeScalar"/> 相同。</para></returns>
    internal static uint ComputeVectorized(ReadOnlySpan<byte> data)
    {
        if (Avx512BW.IsSupported)
        {
            return ComputeAvx512Bw(data);
        }

        if (Avx2.IsSupported)
        {
            return ComputeAvx2(data);
        }

        if (Sse2.IsSupported && Ssse3.IsSupported)
        {
            return ComputeSsse3(data);
        }

        if (AdvSimd.IsSupported)
        {
            return ComputeAdvSimd(data);
        }

        return ComputeScalar(data);
    }

    /// <summary>
    /// The AVX512BW implementation: one <c>pmaddubsw</c>/<c>pmaddwd</c> chain consumes a whole 64-byte block with the
    /// descending weights <c>64</c> to <c>1</c>, under the narrower <see cref="NmaxAvx512"/> reduction window that
    /// keeps the <c>b</c> accumulator inside 32 bits at the wider block width.
    /// <para>AVX512BW 实现：一条 <c>pmaddubsw</c>/<c>pmaddwd</c> 链按 <c>64</c> 递减到 <c>1</c> 的权重消费整个 64 字节块，
    /// 并采用更窄的 <see cref="NmaxAvx512"/> 取余窗口，使更宽块宽下的 <c>b</c> 累加量留在 32 位内。</para>
    /// </summary>
    /// <param name="data">Bytes to hash. <para>参与计算的字节。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    private static uint ComputeAvx512Bw(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + NmaxAvx512, data.Length);
            while (end - i >= Vector512BlockBytes)
            {
                var block = Vector512.Create(data.Slice(i, Vector512BlockBytes));
                var byteSums = Avx512BW.MultiplyAddAdjacent(block, Ones8_512);
                var plain = Avx512BW.MultiplyAddAdjacent(byteSums.AsInt16(), Ones16_512);
                var weightedSums = Avx512BW.MultiplyAddAdjacent(block, Weight512);
                var weighted = Avx512BW.MultiplyAddAdjacent(weightedSums.AsInt16(), Ones16_512);

                var bytes = 0u;
                var weights = 0u;
                for (var lane = 0; lane < Vector512<int>.Count; lane++)
                {
                    bytes += (uint)plain.GetElement(lane);
                    weights += (uint)weighted.GetElement(lane);
                }

                b += (uint)Vector512BlockBytes * a + weights;
                a += bytes;
                i += Vector512BlockBytes;
            }

            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// The AVX2 implementation: <c>pmaddubsw</c>/<c>pmaddwd</c> over 32-byte blocks with the descending weights
    /// <c>32</c> to <c>1</c>, the same recurrence and window the SSSE3 form uses.
    /// <para>AVX2 实现：按权重 <c>32</c> 递减到 <c>1</c> 对 32 字节块执行 <c>pmaddubsw</c>/<c>pmaddwd</c>，
    /// 递推与取余窗口和 SSSE3 形态一致。</para>
    /// </summary>
    /// <param name="data">Bytes to hash. <para>参与计算的字节。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    private static uint ComputeAvx2(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + Nmax, data.Length);
            while (end - i >= VectorBlockBytes)
            {
                var block = Vector256.Create(data.Slice(i, VectorBlockBytes));
                var byteSums = Avx2.MultiplyAddAdjacent(block, Ones8_256);
                var plain = Avx2.MultiplyAddAdjacent(byteSums.AsInt16(), Ones16_256);
                var weightedSums = Avx2.MultiplyAddAdjacent(block, Weight256);
                var weighted = Avx2.MultiplyAddAdjacent(weightedSums.AsInt16(), Ones16_256);

                var bytes = 0u;
                var weights = 0u;
                for (var lane = 0; lane < Vector256<int>.Count; lane++)
                {
                    bytes += (uint)plain.GetElement(lane);
                    weights += (uint)weighted.GetElement(lane);
                }

                b += (uint)VectorBlockBytes * a + weights;
                a += bytes;
                i += VectorBlockBytes;
            }

            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// The SSSE3 implementation: <c>pmaddubsw</c> with the <c>1</c> multiplier yields pairwise byte sums,
    /// <c>pmaddwd</c> widens them into 32-bit lanes, and the same two steps with the descending weight vector yield the
    /// weighted sum the <c>b</c> accumulator needs.
    /// <para>SSSE3 实现：<c>pmaddubsw</c> 配合 <c>1</c> 乘数得到成对字节和，<c>pmaddwd</c> 将其加宽为 32 位通道；同样两步再配
    /// 递减权重向量，得到 <c>b</c> 累加所需的加权和。</para>
    /// </summary>
    /// <remarks>
    /// <c>pmaddubsw</c> saturates at 16 bits, so the weights must keep every pair sum inside <see cref="short.MaxValue"/>:
    /// the largest pair is <c>32 * 255 + 31 * 255 = 16065</c>, which is why a block is 32 bytes with weights bounded by 32.
    /// <para><c>pmaddubsw</c> 在 16 位处饱和，因此权重必须让每对之和留在 <see cref="short.MaxValue"/> 内：最大的一对是
    /// <c>32 * 255 + 31 * 255 = 16065</c>，这也是块取 32 字节、权重上界取 32 的原因。</para>
    /// </remarks>
    /// <param name="data">Bytes to hash. <para>参与计算的字节。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    private static uint ComputeSsse3(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + Nmax, data.Length);
            while (end - i >= VectorBlockBytes)
            {
                var low = Vector128.Create(data.Slice(i, 16));
                var high = Vector128.Create(data.Slice(i + 16, 16));

                var byteSums = Sse2.Add(
                    Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(low, Ones8), Ones16),
                    Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(high, Ones8), Ones16));
                var weightedSums = Sse2.Add(
                    Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(low, WeightLow), Ones16),
                    Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(high, WeightHigh), Ones16));

                var weighted = (uint)(
                    weightedSums.GetElement(0) + weightedSums.GetElement(1)
                    + weightedSums.GetElement(2) + weightedSums.GetElement(3));
                var bytes = (uint)(
                    byteSums.GetElement(0) + byteSums.GetElement(1)
                    + byteSums.GetElement(2) + byteSums.GetElement(3));

                b += (uint)VectorBlockBytes * a + weighted;
                a += bytes;
                i += VectorBlockBytes;
            }

            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// The AdvSIMD implementation for ARM64: <c>Vector128.Widen</c> splits each 16-byte half into two 16-bit lane
    /// vectors, adds and lane multiplies build the plain byte sum and the sum weighted by the descending block weights,
    /// and the same 32-byte block recurrence as the SSSE3 form updates both accumulators.
    /// <para>ARM64 的 AdvSIMD 实现：<c>Vector128.Widen</c> 把每个 16 字节半块拆成两个 16 位通道向量，加法与通道乘法得到
    /// 字节总和与按块内递减权重加权的加权和，并用与 SSSE3 形态相同的 32 字节块递推更新两个累加器。</para>
    /// </summary>
    /// <remarks>
    /// Widening a byte vector is exact, every lane product stays inside 16 bits because the largest product is
    /// <c>32 * 255 = 8160</c>, and the widest lane sum <c>255 * 392 = 99960</c> fits 32 bits, so no intermediate can
    /// overflow and the block boundary keeps the <c>b</c> accumulator inside 32 bits.
    /// <para>字节向量加宽是精确的；最大通道积 <c>32 * 255 = 8160</c> 与最宽通道和 <c>255 * 392 = 99960</c> 都不溢出，
    /// 因此所有中间量都不会越界，分块边界使 <c>b</c> 累加量留在 32 位内。</para>
    /// </remarks>
    /// <param name="data">Bytes to hash. <para>参与计算的字节。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    private static uint ComputeAdvSimd(ReadOnlySpan<byte> data)
    {
        var a = 1u;
        var b = 0u;
        var i = 0;
        while (i < data.Length)
        {
            var end = Math.Min(i + Nmax, data.Length);
            while (end - i >= VectorBlockBytes)
            {
                var bytes = AdvSimdBlockSums(data.Slice(i, VectorBlockBytes), out var weighted);
                b += (uint)VectorBlockBytes * a + weighted;
                a += bytes;
                i += VectorBlockBytes;
            }

            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Sums one 32-byte block plainly and weighted by the descending position weights <c>32</c> to <c>1</c>.
    /// <para>对一个 32 字节块求普通总和与按位置权重 <c>32</c> 到 <c>1</c> 递减的加权和。</para>
    /// </summary>
    /// <param name="block">Exactly <see cref="VectorBlockBytes"/> bytes. <para>恰好 <see cref="VectorBlockBytes"/> 字节。</para></param>
    /// <param name="weighted">Receives the position-weighted byte sum of the block.
    /// <para>接收块内按位置权重加权的字节和。</para></param>
    /// <returns>The plain byte sum of the block. <para>块的普通字节总和。</para></returns>
    private static uint AdvSimdBlockSums(ReadOnlySpan<byte> block, out uint weighted)
    {
        var bytes = AdvSimdHalfSums(block.Slice(0, 16), WeightLanes0To7, WeightLanes8To15, out var weightedLow)
            + AdvSimdHalfSums(block.Slice(16, 16), WeightLanes16To23, WeightLanes24To31, out var weightedHigh);
        weighted = weightedLow + weightedHigh;
        return bytes;
    }

    /// <summary>
    /// Sums one 16-byte half plainly and weighted, with <paramref name="weightLanesLow"/> giving the weights of the
    /// first eight bytes and <paramref name="weightLanesHigh"/> the weights of the last eight.
    /// <para>对一个 16 字节半块求普通和与加权和；<paramref name="weightLanesLow"/> 给出前八字节权重，
    /// <paramref name="weightLanesHigh"/> 给出后八字节权重。</para>
    /// </summary>
    /// <param name="half">Exactly sixteen bytes. <para>恰好十六字节。</para></param>
    /// <param name="weightLanesLow">Per-lane weights matching bytes zero to seven.
    /// <para>与字节零到七对应的逐通道权重。</para></param>
    /// <param name="weightLanesHigh">Per-lane weights matching bytes eight to fifteen.
    /// <para>与字节八到十五对应的逐通道权重。</para></param>
    /// <param name="weighted">Receives the position-weighted byte sum of the half.
    /// <para>接收半块内按位置权重加权的字节和。</para></param>
    /// <returns>The plain byte sum of the half. <para>半块的普通字节总和。</para></returns>
    private static uint AdvSimdHalfSums(ReadOnlySpan<byte> half, Vector128<ushort> weightLanesLow,
        Vector128<ushort> weightLanesHigh, out uint weighted)
    {
        var value = Vector128.Create(half);
        (var lowBytes, var highBytes) = Vector128.Widen(value);

        (var pairLow, var pairHigh) = Vector128.Widen(Vector128.Add(lowBytes, highBytes));
        var byteSums = Vector128.Add(pairLow, pairHigh);

        (var productLowLow, var productLowHigh) = Vector128.Widen(Vector128.Multiply(lowBytes, weightLanesLow));
        (var productHighLow, var productHighHigh) = Vector128.Widen(Vector128.Multiply(highBytes, weightLanesHigh));
        var weightedSums = Vector128.Add(
            Vector128.Add(productLowLow, productLowHigh),
            Vector128.Add(productHighLow, productHighHigh));

        weighted = weightedSums.GetElement(0) + weightedSums.GetElement(1)
            + weightedSums.GetElement(2) + weightedSums.GetElement(3);
        return byteSums.GetElement(0) + byteSums.GetElement(1)
            + byteSums.GetElement(2) + byteSums.GetElement(3);
    }
}