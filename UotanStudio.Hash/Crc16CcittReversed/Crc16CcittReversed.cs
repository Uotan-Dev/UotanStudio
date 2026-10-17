namespace UotanStudio.Hash.Crc16CcittReversed;

/// <summary>
/// Reflected CRC-16 (low-bit-first polynomial <c>0x8408</c>, init <c>0xFFFF</c>, final XOR <c>0xFFFF</c>) required by
/// HDLC-framed DIAG packets and by Unisoc UpdateApp entry checksums. The check value over the ASCII bytes
/// <c>123456789</c> is <c>0x906E</c>, and empty data yields <c>0x0000</c>.
/// <para>反射式 CRC-16（低位优先多项式 <c>0x8408</c>，初值 <c>0xFFFF</c>，最终异或 <c>0xFFFF</c>），HDLC 帧封装的 DIAG
/// 报文与展讯 UpdateApp 条目校验都要求这种参数。对 ASCII 字节 <c>123456789</c> 的校验值为 <c>0x906E</c>，空数据的结果为
/// <c>0x0000</c>。</para>
/// </summary>
/// <remarks>
/// Spans of at least eight bytes fold eight bytes per iteration through eight 256-entry tables (slice-by-8); shorter
/// frames keep the byte-at-a-time table, and both forms apply the same initial value and final XOR.
/// <para>不少于 8 字节的区间借助 8 张 256 项表每轮折叠 8 字节（slice-by-8）；更短的帧保留逐字节查表，
/// 两种形式套用相同的初值与最终异或。</para>
/// </remarks>
public static class Crc16CcittReversed
{
    private const ushort Polynomial = 0x8408;
    private const ushort InitialValue = 0xFFFF;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    private static readonly ushort[] SliceTable = BuildSliceTable();

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
        var crc = InitialValue;
        var table = SliceTable;
        while (data.Length >= SliceSize)
        {
            crc = (ushort)(table[0x700 + ((crc ^ data[0]) & 0xFF)]
                ^ table[0x600 + (((crc >> 8) ^ data[1]) & 0xFF)]
                ^ table[0x500 + data[2]]
                ^ table[0x400 + data[3]]
                ^ table[0x300 + data[4]]
                ^ table[0x200 + data[5]]
                ^ table[0x100 + data[6]]
                ^ table[data[7]]);
            data = data[SliceSize..];
        }

        foreach (var value in data)
        {
            crc = (ushort)((crc >> 8) ^ table[(crc ^ value) & 0xFF]);
        }

        return (ushort)(crc ^ 0xFFFF);
    }

    private static ushort[] BuildSliceTable()
    {
        var table = new ushort[SliceTableEntries];
        for (var value = 0; value < TableEntries; value++)
        {
            var entry = (ushort)value;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (ushort)((entry >> 1) ^ Polynomial) : (ushort)(entry >> 1);
            }

            table[value] = entry;
        }

        for (var step = 1; step < SliceSize; step++)
        {
            var previous = (step - 1) * TableEntries;
            var current = step * TableEntries;
            for (var index = 0; index < TableEntries; index++)
            {
                var carry = table[previous + index];
                table[current + index] = (ushort)((carry >> 8) ^ table[carry & 0xFF]);
            }
        }

        return table;
    }
}