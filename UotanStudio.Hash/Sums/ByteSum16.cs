using System.Numerics;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, truncating to 16 bits, which is what the MediaTek legacy and XFlash
/// storage frames carry beside each packet.
/// <para>逐字节纯累加校验和并截断到 16 位，MediaTek legacy 与 XFlash 存储帧在每个数据包旁携带的就是该值。</para>
/// </summary>
/// <remarks>
/// Spans wide enough for a hardware vector widen their bytes into 16-bit lanes and accumulate there. A lane that wraps
/// past 2^16 stays congruent with the exact sum, so the final modulo-2^16 result matches the scalar accumulator for
/// every input length.
/// <para>宽度足够容纳硬件向量的区间把字节扩宽到 16 位分道累加。通道越过 2^16 后与真实和仍同余，
/// 因此任意输入长度下最终的 mod 2^16 结果与标量累加器一致。</para>
/// </remarks>
public static class ByteSum16
{
    /// <summary>
    /// Adds every byte of the data, discarding carries beyond 16 bits.
    /// <para>累加数据的每个字节，丢弃超过 16 位的进位。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to sum. <para>待累加的字节。</para>
    /// </param>
    /// <returns>
    /// The sum modulo 2^16; an empty span returns <c>0</c>. <para>mod 2^16 的和；空区间返回 <c>0</c>。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort sum = 0;
        var index = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            var lower = Vector<ushort>.Zero;
            var upper = Vector<ushort>.Zero;
            var limit = data.Length - Vector<byte>.Count;
            while (index <= limit)
            {
                var bytes = new Vector<byte>(data[index..]);
                Vector.Widen(bytes, out Vector<ushort> widenedLower, out Vector<ushort> widenedUpper);
                lower += widenedLower;
                upper += widenedUpper;
                index += Vector<byte>.Count;
            }

            for (var lane = 0; lane < Vector<ushort>.Count; lane++)
            {
                sum += lower[lane];
                sum += upper[lane];
            }
        }

        for (; index < data.Length; index++)
        {
            sum += data[index];
        }

        return sum;
    }
}