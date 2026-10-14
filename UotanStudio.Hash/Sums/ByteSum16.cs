namespace UotanStudio.Hash.Sums;

/// <summary>
/// Plain additive checksum of every byte, truncating to 16 bits, which is what the MediaTek legacy and XFlash
/// storage frames carry beside each packet.
/// <para>逐字节纯累加校验和并截断到 16 位，MediaTek legacy 与 XFlash 存储帧在每个数据包旁携带的就是该值。</para>
/// </summary>
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
        foreach (var value in data)
        {
            sum += value;
        }

        return sum;
    }
}