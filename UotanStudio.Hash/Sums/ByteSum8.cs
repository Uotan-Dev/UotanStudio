using UotanStudio.Hash.Simd;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, truncated to 8 bits, which is the optional payload checksum of an HDC
/// daemon packet.
/// <para>逐字节纯累加校验和并截断到 8 位，即 HDC daemon 数据包可选的负载校验和。</para>
/// </summary>
/// <remarks>
/// HDC reserves <c>0</c> for "checksum absent" and an all-zero payload also sums to <c>0</c>, so a caller must apply
/// the sentinel rule before comparing. The shared block kernel consumes whole vectors at the widest width the
/// processor offers and returns the modulo-2^32 sum; keeping its low eight bits is the same modulo-256 sum as the
/// byte-at-a-time loop, including when no vector ISA is available.
/// <para>HDC 用 <c>0</c> 表示“无校验和”，而全零负载的累加和同样是 <c>0</c>，因此调用方必须先套用哨兵规则再比较。
/// 共享块内核按处理器支持的最宽向量消费整块并返回模 2^32 的和；取其低八位与逐字节循环得到的模 256 和一致，
/// 在没有任何向量指令集时同样成立。</para>
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
    public static byte Compute(ReadOnlySpan<byte> data) => (byte)SimdOps.SumBytes(data);
}