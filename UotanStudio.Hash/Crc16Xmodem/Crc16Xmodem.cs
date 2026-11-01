namespace UotanStudio.Hash.Crc16Xmodem;

/// <summary>
/// Non-reflected CRC-16 (high-bit-first polynomial <c>0x1021</c>, init <c>0x0000</c>, no final XOR) used by the
/// legacy RK28 ID block the RKDL loader-upgrade path writes. The check value over the ASCII bytes <c>123456789</c>
/// is <c>0x31C3</c>, and empty data yields <c>0x0000</c>.
/// <para>非反射式 CRC-16（高位优先多项式 <c>0x1021</c>，初值 <c>0x0000</c>，无最终异或），RKDL loader 升级路径写入的
/// 旧式 RK28 ID block 使用这种参数。对 ASCII 字节 <c>123456789</c> 的校验值为 <c>0x31C3</c>，空数据的结果为
/// <c>0x0000</c>。</para>
/// </summary>
/// <remarks>
/// This is the <c>CRC_16</c> routine of the vendor tool, which seeds the register to zero. It is a distinct type
/// from <c>Crc16CcittFalse</c> even though only the initial value differs: sharing one type would let a missing seed
/// silently change every ID block checksum instead of failing a round-trip test.
/// <para>这是厂商工具中把寄存器初值置零的 <c>CRC_16</c> 例程。它虽然只与 <c>Crc16CcittFalse</c> 相差初值，仍必须是独立
/// 类型：合并后漏写初值只会静默改掉每个 ID block 校验和，而不会让往返测试变红。</para>
/// </remarks>
public static class Crc16Xmodem
{
    private const int TableEntries = 256;

    private static readonly ushort[] Table = BuildTable();

    /// <summary>
    /// Computes the non-reflected CRC-16 of the given data with the register seeded to <c>0x0000</c> and no final XOR.
    /// <para>以 <c>0x0000</c> 为寄存器初值计算数据的非反射 CRC-16，不做最终异或。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty span returns the unchanged seed <c>0x0000</c>.
    /// <para>参与计算的字节；空区间原样返回初值 <c>0x0000</c>。</para>
    /// </param>
    /// <returns>
    /// The 16-bit register value after every byte has been folded in from the high end.
    /// <para>所有字节自高位依次并入后的 16 位寄存器值。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = (ushort)0x0000;
        var table = Table;
        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ table[(byte)(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[TableEntries];
        for (var value = 0; value < TableEntries; value++)
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