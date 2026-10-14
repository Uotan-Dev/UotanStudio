namespace UotanStudio.Hash.Crc16CcittFalse;

/// <summary>
/// Non-reflected CRC-16 (high-bit-first polynomial <c>0x1021</c>, init <c>0xFFFF</c>, no final XOR) required by the
/// RKDL loader download frames. The check value over the ASCII bytes <c>123456789</c> is <c>0x29B1</c>, the bytes
/// <c>01</c> through <c>10</c> yield <c>0x0FEF</c>, and empty data yields <c>0xFFFF</c>.
/// <para>非反射式 CRC-16（高位优先多项式 <c>0x1021</c>，初值 <c>0xFFFF</c>，无最终异或），RKDL loader 下载帧要求这种参数。
/// 对 ASCII 字节 <c>123456789</c> 的校验值为 <c>0x29B1</c>，字节 <c>01</c> 到 <c>10</c> 的结果为 <c>0x0FEF</c>，空数据的结果为
/// <c>0xFFFF</c>。</para>
/// </summary>
public static class Crc16CcittFalse
{
    private static readonly ushort[] Table = BuildTable();

    /// <summary>
    /// Computes the non-reflected CRC-16 of the given data with the register seeded to <c>0xFFFF</c> and no final XOR.
    /// <para>以 <c>0xFFFF</c> 为寄存器初值计算数据的非反射 CRC-16，不做最终异或。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span returns the unchanged seed <c>0xFFFF</c>.
    /// <para>参与计算的字节；空区间原样返回初值 <c>0xFFFF</c>。</para>
    /// </param>
    /// <returns>
    /// The 16-bit register value after every byte has been folded in from the high end.
    /// <para>所有字节自高位依次并入后的 16 位寄存器值。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ Table[(byte)(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var value = 0; value < 256; value++)
        {
            var entry = (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 0x8000) != 0 ? (ushort)((entry << 1) ^ 0x1021) : (ushort)(entry << 1);
            }

            table[value] = entry;
        }

        return table;
    }
}