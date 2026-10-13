namespace UotanStudio.Hash.Crc16;

/// <summary>
/// Reflected CRC-16 used by PAC header and data checksums.
/// <para>PAC 头部与数据校验所使用的反射式 CRC-16。</para>
/// </summary>
internal static class Crc16
{
    /// <summary>
    /// Calculates the CRC-16 checksum of the data with the given seed.
    /// <para>使用给定种子对数据计算 CRC-16 校验和。</para>
    /// </summary>
    /// <param name="seed">
    /// Starting register value; passing the result of a previous call continues the same checksum over further bytes.
    /// <para>起始寄存器值；把上一次调用的结果传入即可对后续字节继续累计同一校验和。</para>
    /// </param>
    /// <param name="data">
    /// Bytes to fold into the checksum; processed from index <c>0</c> to the end of the array, so an empty array returns <paramref name="seed"/> unchanged.
    /// <para>要参与校验和的字节；按下标 <c>0</c> 到数组末尾依次处理，因此空数组会原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <returns>
    /// The 16-bit register after processing every byte with the low-bit-first shift and polynomial <c>0xA001</c>.
    /// <para>以低位优先移位、多项式 <c>0xA001</c> 处理完所有字节后的 16 位寄存器值。</para>
    /// </returns>
    public static ushort Calculate(ushort seed, byte[] data)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }
}