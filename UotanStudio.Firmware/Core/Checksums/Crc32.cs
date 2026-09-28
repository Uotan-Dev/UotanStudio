namespace UotanStudio.Firmware.Core.Checksums;

/// <summary>
/// Reflected CRC-32 (polynomial <c>0xEDB88320</c>) with register seed semantics and no final XOR, so non-standard
/// initial values such as the F2FS superblock magic seed stay expressible; <see cref="System.IO.Hashing.Crc32"/>
/// cannot express them.
/// <para>反射式 CRC-32（多项式 <c>0xEDB88320</c>），寄存器 seed 语义且无最终异或，因此可以表达 F2FS 超级块魔数种子
/// 这类非标准初值；<see cref="System.IO.Hashing.Crc32"/> 无法表达这些初值。</para>
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = InitTable();

    /// <summary>
    /// Computes the CRC-32 of the data starting from <paramref name="seed"/> (register semantics, no final XOR).
    /// <para>以 <paramref name="seed"/> 为寄存器初值计算数据的 CRC-32（寄存器语义，无最终异或）。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <param name="seed">
    /// Register initial value, such as <c>0xFFFF_FFFF</c> or a format-specific constant.
    /// <para>寄存器初值，例如 <c>0xFFFF_FFFF</c> 或格式专属常量。</para>
    /// </param>
    /// <returns>
    /// The CRC-32 register value after consuming the data.
    /// <para>消费完数据后的 CRC-32 寄存器值。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] InitTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}