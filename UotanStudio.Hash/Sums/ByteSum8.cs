using System.Numerics;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, truncated to 8 bits, which is the optional payload checksum of an HDC
/// daemon packet.
/// <para>逐字节纯累加校验和并截断到 8 位，即 HDC daemon 数据包可选的负载校验和。</para>
/// </summary>
/// <remarks>
/// HDC reserves <c>0</c> for "checksum absent" and an all-zero payload also sums to <c>0</c>, so a caller must apply
/// the sentinel rule before comparing. Spans wide enough for a hardware vector accumulate in byte lanes, where every
/// lane wraps modulo 256 just like the scalar accumulator, so the result is the same modulo-256 sum.
/// <para>HDC 用 <c>0</c> 表示“无校验和”，而全零负载的累加和同样是 <c>0</c>，因此调用方必须先套用哨兵规则再比较。
/// 宽度足够容纳硬件向量的区间按字节分道累加，每个通道与标量累加器一样按 mod 256 回卷，因此得到相同的 mod 256 和。</para>
/// </remarks>
public static class ByteSum8
{
    /// <summary>
    /// Adds every byte of the data, discarding carries beyond 8 bits.
    /// <para>累加数据的每个字节，丢弃超过 8 位的进位。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to sum. <para>待累加的字节。</para>
    /// </param>
    /// <returns>
    /// The sum modulo 256; an empty span returns <c>0</c>. <para>mod 256 的和；空区间返回 <c>0</c>。</para>
    /// </returns>
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        var index = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            var accumulator = Vector<byte>.Zero;
            var limit = data.Length - Vector<byte>.Count;
            while (index <= limit)
            {
                accumulator += new Vector<byte>(data[index..]);
                index += Vector<byte>.Count;
            }

            for (var lane = 0; lane < Vector<byte>.Count; lane++)
            {
                sum += accumulator[lane];
            }
        }

        for (; index < data.Length; index++)
        {
            sum += data[index];
        }

        return sum;
    }
}