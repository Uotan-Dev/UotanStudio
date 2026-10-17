namespace UotanStudio.Hash.Crc16Arc;

/// <summary>
/// Reflected CRC-16 (low-bit-first polynomial <c>0xA001</c>, the reflection of <c>0x8005</c>) with a caller-supplied
/// register seed and no final XOR, which is the checksum PAC header and data fields store.
/// <para>反射式 CRC-16（低位优先多项式 <c>0xA001</c>，即 <c>0x8005</c> 的反射），寄存器初值由调用方给出且无最终异或，
/// 正是 PAC 头部与数据字段所存储的校验和。</para>
/// </summary>
/// <remarks>
/// The name is deliberately parameter-shaped rather than a catalog alias: sources disagree on what "CRC-16" means,
/// so this type pins only the polynomial, the seed convention and the absence of a final XOR.
/// <para>类型名刻意按参数描述而非目录别名：不同资料对 “CRC-16” 的含义并不一致，因此本类型只固定多项式、seed 约定
/// 以及“不做末异或”这一点。</para>
/// </remarks>
public static class Crc16Arc
{
    private const ushort Polynomial = 0xA001;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    private static readonly ushort[] SliceTable = BuildSliceTable();

    /// <summary>
    /// Computes the CRC-16 register value of the data starting from <paramref name="seed"/>.
    /// <para>以 <paramref name="seed"/> 为寄存器初值计算数据的 CRC-16 寄存器值。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold into the checksum; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>要参与校验和的字节；空区间原样返回 <paramref name="seed"/>。</para>
    /// </param>
    /// <param name="seed">
    /// Starting register value; passing the result of a previous call continues the same checksum over further bytes.
    /// PAC stores a fresh checksum, so those callers pass <c>0</c>.
    /// <para>起始寄存器值；把上一次调用的结果传入即可对后续字节续算同一校验和。PAC 存放的是全新校验和，
    /// 因此那些调用点传 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The 16-bit register after processing every byte with the low-bit-first shift.
    /// <para>以低位优先移位处理完所有字节后的 16 位寄存器值。</para>
    /// </returns>
    /// <remarks>
    /// Spans of at least eight bytes fold eight bytes per iteration through eight 256-entry tables (slice-by-8), while
    /// shorter frames keep the byte-at-a-time table. Both forms return exactly what the equivalent eight-shift loop
    /// returns: <c>(byte)crc ^ value</c> is the same partial register that applying the low-bit-first shift eight times
    /// reaches from <c>(crc ^ value)</c>, and a caller-supplied seed still enters through the low byte as before.
    /// <para>不少于 8 字节的区间借助 8 张 256 项表每轮折叠 8 字节（slice-by-8），更短的帧保留逐字节查表。
    /// 两种形式的结果都与等价的八次移位循环完全一致：<c>(byte)crc ^ value</c> 就是从 <c>(crc ^ value)</c>
    /// 起做八次低位优先移位后到达的部分寄存器；调用方给定的 seed 仍照旧从低字节进入。</para>
    /// </remarks>
    public static ushort Compute(ReadOnlySpan<byte> data, ushort seed)
    {
        var crc = seed;
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
            crc = (ushort)((crc >> 8) ^ table[(byte)crc ^ value]);
        }

        return crc;
    }

    /// <summary>
    /// Builds the reflected byte-at-a-time table and derives the seven additional slice tables from it, keeping the
    /// polynomial in exactly one place.
    /// <para>构造逐字节反射查表并由它派生另外 7 张 slice 表，保证多项式只写在一处。</para>
    /// </summary>
    private static ushort[] BuildSliceTable()
    {
        var table = new ushort[SliceTableEntries];
        for (var index = 0; index < TableEntries; index++)
        {
            var entry = (ushort)index;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (ushort)((entry >> 1) ^ Polynomial) : (ushort)(entry >> 1);
            }

            table[index] = entry;
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