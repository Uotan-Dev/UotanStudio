using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace UotanStudio.Hash.Adler32;

/// <summary>
/// Adler-32 checksum computation used by zlib-style payloads, reduced modulo <c>65521</c> in blocks of <c>5552</c> bytes.
/// <para>用于 zlib 风格载荷的 Adler-32 校验实现，按 <c>5552</c> 字节分块并对模数 <c>65521</c> 取余。</para>
/// </summary>
/// <remarks>
/// When the processor exposes SSSE3 the addition runs through <c>pmaddubsw</c>/<c>pmaddwd</c>, which advances 32 bytes per
/// iteration instead of one; otherwise the byte-at-a-time loop runs. Both forms produce the same value for every length
/// and starting value, and the <c>5552</c>-byte block boundary keeps the <c>b</c> accumulator inside 32 bits either way.
/// <para>处理器支持 SSSE3 时，累加经 <c>pmaddubsw</c>/<c>pmaddwd</c> 每轮推进 32 字节而非 1 字节；否则执行逐字节循环。
/// 两种形态对任意长度与初值都给出相同结果，且 <c>5552</c> 字节的分块边界使 <c>b</c> 累加量在两种形态下都留在 32 位内。</para>
/// </remarks>
public static class Adler32
{
    private const uint Mod = 65521;
    private const int Nmax = 5552;
    private const int VectorBlockBytes = 32;
    private const int MinimumVectorizedBytes = 64;

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
        if (Ssse3.IsSupported && Sse2.IsSupported && data.Length >= MinimumVectorizedBytes)
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
    /// <para>此前所有字节的打包 Adler-32 值，低半部为 <c>a</c>，高半部为 <c>b</c>；取值 <c>1</c> 表示重新开始计算。</para></param>
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
    /// The portable byte-at-a-time reference implementation, also the fallback when SSSE3 is unavailable.
    /// <para>可移植的逐字节参考实现，同时是 SSSE3 不可用时的回退路径。</para>
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
    /// <param name="data">Bytes to hash; the caller guarantees at least <see cref="MinimumVectorizedBytes"/>.
    /// <para>参与计算的字节；调用方保证长度至少为 <see cref="MinimumVectorizedBytes"/>。</para></param>
    /// <returns>The Adler-32 value of the data. <para>数据的 Adler-32 值。</para></returns>
    internal static uint ComputeVectorized(ReadOnlySpan<byte> data)
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
}