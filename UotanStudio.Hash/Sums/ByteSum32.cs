using System.Numerics;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, wrapping modulo 2^32: not a CRC, because no polynomial division and no
/// reflection participate. ADB message headers, the <c>070702</c> CPIO payload sum and the 512-byte TAR header block
/// all store this value.
/// <para>逐字节纯累加校验和，按 mod 2^32 回卷：不是 CRC，因为没有多项式除法也不做反射。ADB 消息头、
/// <c>070702</c> CPIO 负载和 512 字节 TAR 头块存储的都是该值。</para>
/// </summary>
/// <remarks>
/// Field-layout rules stay with the caller: ADB reserves <c>0</c> to mean "no checksum present", and TAR sums the
/// header block only after the eight checksum bytes have been replaced with spaces. An empty input yields <c>0</c>,
/// which is why the zero sentinel and this checksum cannot be distinguished. Spans wide enough for a hardware vector
/// widen their bytes into 32-bit lanes, whose wrapping addition is the same modulo-2^32 sum as the scalar accumulator.
/// <para>字段布局规则留在调用方：ADB 用 <c>0</c> 表示“无校验和”，TAR 必须先把 8 个校验字节替换为空格再求和。
/// 空输入得到 <c>0</c>，因此零哨兵与该校验和无法区分。宽度足够容纳硬件向量的区间把字节扩宽到 32 位分道累加，
/// 其回卷加法与标量累加器得到相同的 mod 2^32 和。</para>
/// </remarks>
public static class ByteSum32
{
    /// <summary>
    /// Adds every byte of the data, discarding carries beyond 32 bits.
    /// <para>累加数据的每个字节，丢弃超过 32 位的进位。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to sum. <para>待累加的字节。</para>
    /// </param>
    /// <returns>
    /// The sum modulo 2^32; an empty span returns <c>0</c>. <para>mod 2^32 的和；空区间返回 <c>0</c>。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            var accumulator = Vector<uint>.Zero;
            var limit = data.Length - Vector<byte>.Count;
            while (index <= limit)
            {
                var bytes = new Vector<byte>(data[index..]);
                Vector.Widen(bytes, out Vector<ushort> lower, out Vector<ushort> upper);
                Vector.Widen(lower, out Vector<uint> lowerLow, out Vector<uint> lowerHigh);
                Vector.Widen(upper, out Vector<uint> upperLow, out Vector<uint> upperHigh);
                accumulator += lowerLow + lowerHigh + upperLow + upperHigh;
                index += Vector<byte>.Count;
            }

            for (var lane = 0; lane < Vector<uint>.Count; lane++)
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