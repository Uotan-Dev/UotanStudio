namespace UotanStudio.Firmware.Core.Checksums;

/// <summary>
/// CRC32c (Castagnoli, polynomial <c>0x82F63B78</c>) following the Linux kernel's <c>crc32c_le</c> convention:
/// register seed semantics, no final XOR, so callers can continue a checksum across disjoint spans.
/// <para>CRC32c（Castagnoli，多项式 <c>0x82F63B78</c>），与 Linux 内核 <c>crc32c_le</c> 约定一致：寄存器 seed 语义、
/// 无最终异或，调用方可以对不相邻的数据分段续算。</para>
/// </summary>
internal static class Crc32c
{
    private static readonly uint[] Table = InitTable();

    /// <summary>
    /// Computes the CRC32c of the data starting from <paramref name="seed"/> (crc32c_le register semantics).
    /// <para>以 <paramref name="seed"/> 为寄存器初值计算数据的 CRC32c（crc32c_le 寄存器语义）。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <param name="seed">
    /// Register initial value; use <c>0xFFFF_FFFF</c> to start a fresh checksum or a previous result to continue it.
    /// <para>寄存器初值；首次计算用 <c>0xFFFF_FFFF</c>，续算传入上一次的结果。</para>
    /// </param>
    /// <returns>
    /// The CRC32c register value after consuming the data.
    /// <para>消费完数据后的 CRC32c 寄存器值。</para>
    /// </returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0xFFFF_FFFF)
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
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}