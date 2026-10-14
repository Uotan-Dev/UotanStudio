namespace UotanStudio.Hash.Crc16CcittReversed;

/// <summary>
/// Reflected CRC-16 (low-bit-first polynomial <c>0x8408</c>, init <c>0xFFFF</c>, final XOR <c>0xFFFF</c>) required by
/// HDLC-framed DIAG packets and by Unisoc UpdateApp entry checksums. The check value over the ASCII bytes
/// <c>123456789</c> is <c>0x906E</c>, and empty data yields <c>0x0000</c>.
/// <para>反射式 CRC-16（低位优先多项式 <c>0x8408</c>，初值 <c>0xFFFF</c>，最终异或 <c>0xFFFF</c>），HDLC 帧封装的 DIAG
/// 报文与展讯 UpdateApp 条目校验都要求这种参数。对 ASCII 字节 <c>123456789</c> 的校验值为 <c>0x906E</c>，空数据的结果为
/// <c>0x0000</c>。</para>
/// </summary>
public static class Crc16CcittReversed
{
    private static readonly ushort[] Table = BuildTable();

    /// <summary>
    /// Computes the reflected CRC-16 of the given data with the register seeded to <c>0xFFFF</c> and inverted at the end.
    /// <para>以 <c>0xFFFF</c> 为寄存器初值计算数据的反射 CRC-16，并在结束时对结果取反。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span yields <c>0x0000</c> because the seed and the final XOR cancel out.
    /// <para>参与计算的字节；空区间返回 <c>0x0000</c>，因为初值与最终异或相互抵消。</para>
    /// </param>
    /// <returns>
    /// The 16-bit value the format expects after the final XOR has been applied.
    /// <para>套用最终异或之后、该格式所要求的 16 位值。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in data)
        {
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ value) & 0xFF]);
        }

        return (ushort)(crc ^ 0xFFFF);
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var value = 0; value < 256; value++)
        {
            var entry = (ushort)value;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (ushort)((entry >> 1) ^ 0x8408) : (ushort)(entry >> 1);
            }

            table[value] = entry;
        }

        return table;
    }
}