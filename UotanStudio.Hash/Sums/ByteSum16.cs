using UotanStudio.Hash.Simd;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, truncating to 16 bits, which is what the MediaTek legacy and XFlash
/// storage frames carry beside each packet.
/// <para>逐字节纯累加校验和并截断到 16 位，MediaTek legacy 与 XFlash 存储帧在每个数据包旁携带的就是该值。</para>
/// </summary>
/// <remarks>
/// The shared block kernel widens bytes into 32-bit lanes before adding them, so no lane can wrap during a realistic
/// buffer and the low sixteen bits of the modulo-2^32 result equal the byte-at-a-time sum. The same kernel runs on
/// every processor and falls back to the scalar loop when no vector ISA is available.
/// <para>共享块内核先把字节扩宽到 32 位通道再累加，实际缓冲长度下通道不会回卷，模 2^32 结果的低十六位与逐字节和相等。
/// 所有处理器都执行同一内核，无可用向量指令集时回退到标量循环。</para>
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
    public static ushort Compute(ReadOnlySpan<byte> data) => (ushort)SimdOps.SumBytes(data);
}