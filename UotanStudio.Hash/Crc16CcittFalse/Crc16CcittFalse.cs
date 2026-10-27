using System.Buffers.Binary;

namespace UotanStudio.Hash.Crc16CcittFalse;

/// <summary>
/// Non-reflected CRC-16 (high-bit-first polynomial <c>0x1021</c>, init <c>0xFFFF</c>, no final XOR) required by the
/// RKDL loader download frames. The check value over the ASCII bytes <c>123456789</c> is <c>0x29B1</c>, the bytes
/// <c>01</c> through <c>10</c> yield <c>0x0FEF</c>, and empty data yields <c>0xFFFF</c>.
/// <para>非反射式 CRC-16（高位优先多项式 <c>0x1021</c>，初值 <c>0xFFFF</c>，无最终异或），RKDL loader 下载帧要求这种参数。
/// 对 ASCII 字节 <c>123456789</c> 的校验值为 <c>0x29B1</c>，字节 <c>01</c> 到 <c>10</c> 的结果为 <c>0x0FEF</c>，空数据的结果为
/// <c>0xFFFF</c>。</para>
/// </summary>
/// <remarks>
/// The main loop consumes eight bytes per iteration through eight 256-entry tables (slice-by-8): the register is
/// XOR-ed with the first two big-endian bytes and the mixed bytes index the two highest tables while the remaining
/// six bytes index the lower ones directly. The trailing remainder falls back to the byte-at-a-time table, and both
/// forms produce the same register value for every length, so the protocol semantics stay bit-identical.
/// <para>主循环借助 8 张 256 项表每轮消费 8 字节（slice-by-8）：寄存器先与前两个大端字节异或，混合出的两个字节索引
/// 最高的两张表，其余 6 个字节直接索引较低的表。尾部余数回退到逐字节查表，两种形态对任意长度都产出相同的寄存器值，
/// 协议语义逐位不变。</para>
/// </remarks>
public static class Crc16CcittFalse
{
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    private static readonly ushort[] Table = BuildTable();
    private static readonly ushort[] SliceTable = BuildSliceTable();

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
        var slice = SliceTable;
        while (data.Length >= SliceSize)
        {
            crc ^= BinaryPrimitives.ReadUInt16BigEndian(data);
            crc = (ushort)(slice[0x700 + (crc >> 8)]
                ^ slice[0x600 + (byte)crc]
                ^ slice[0x500 + data[2]]
                ^ slice[0x400 + data[3]]
                ^ slice[0x300 + data[4]]
                ^ slice[0x200 + data[5]]
                ^ slice[0x100 + data[6]]
                ^ slice[data[7]]);
            data = data[SliceSize..];
        }

        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ Table[(byte)(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[TableEntries];
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

    /// <summary>
    /// Builds the slice-by-8 tables: row <c>k</c> holds the register after consuming one index byte followed by
    /// <c>k</c> zero bytes.
    /// <para>构建 slice-by-8 表：第 <c>k</c> 行存的是先消费一个索引字节再消费 <c>k</c> 个零字节后的寄存器值。</para>
    /// </summary>
    /// <returns>The 8×256 interleaved table. <para>8×256 交织表。</para></returns>
    private static ushort[] BuildSliceTable()
    {
        var slice = new ushort[SliceTableEntries];
        Table.CopyTo(slice, 0);
        for (var step = 1; step < SliceSize; step++)
        {
            var previous = (step - 1) * TableEntries;
            var current = step * TableEntries;
            for (var i = 0; i < TableEntries; i++)
            {
                var carry = slice[previous + i];
                slice[current + i] = (ushort)((carry << 8) ^ Table[carry >> 8]);
            }
        }

        return slice;
    }
}