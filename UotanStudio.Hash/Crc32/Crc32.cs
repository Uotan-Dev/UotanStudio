using System.Buffers.Binary;

namespace UotanStudio.Hash.Crc32;

/// <summary>
/// Reflected CRC-32 (polynomial <c>0xEDB88320</c>) with register seed semantics and no final XOR, so non-standard
/// initial values such as the F2FS superblock magic seed stay expressible; <c>System.IO.Hashing.Crc32</c>
/// cannot express them.
/// <para>反射式 CRC-32（多项式 <c>0xEDB88320</c>），寄存器 seed 语义且无最终异或，因此可以表达 F2FS 超级块魔数种子
/// 这类非标准初值；<c>System.IO.Hashing.Crc32</c> 无法表达这些初值。</para>
/// </summary>
/// <remarks>
/// The hot loop consumes eight bytes per iteration through eight 256-entry tables (slice-by-8) and falls back to
/// the byte-at-a-time table for the trailing remainder, so the register semantics stay identical for every length.
/// <para>热循环借助 8 张 256 项表每轮消费 8 字节（slice-by-8），尾部余数字节回退到逐字节查表，
/// 因此任意长度下的寄存器语义完全一致。</para>
/// </remarks>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    private static readonly uint[] Table = InitTable();
    private static readonly uint[] SliceTable = InitSliceTable();

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
        var slice = SliceTable;
        while (data.Length >= SliceSize)
        {
            var head = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var tail = BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(uint)..]);
            var mixed = crc ^ head;
            var m0 = (byte)mixed;
            var m1 = (byte)(mixed >> 8);
            var m2 = (byte)(mixed >> 16);
            var m3 = (byte)(mixed >> 24);
            var n0 = (byte)tail;
            var n1 = (byte)(tail >> 8);
            var n2 = (byte)(tail >> 16);
            var n3 = (byte)(tail >> 24);
            crc = slice[0x700 + m0]
                ^ slice[0x600 + m1]
                ^ slice[0x500 + m2]
                ^ slice[0x400 + m3]
                ^ slice[0x300 + n0]
                ^ slice[0x200 + n1]
                ^ slice[0x100 + n2]
                ^ slice[n3];
            data = data[SliceSize..];
        }

        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] InitTable()
    {
        var table = new uint[TableEntries];
        for (uint i = 0; i < TableEntries; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    private static uint[] InitSliceTable()
    {
        var slice = new uint[SliceTableEntries];
        Table.CopyTo(slice, 0);
        for (var step = 1; step < SliceSize; step++)
        {
            var previous = (step - 1) * TableEntries;
            var current = step * TableEntries;
            for (var i = 0; i < TableEntries; i++)
            {
                var carry = slice[previous + i];
                slice[current + i] = (carry >> 8) ^ Table[carry & 0xFF];
            }
        }

        return slice;
    }
}