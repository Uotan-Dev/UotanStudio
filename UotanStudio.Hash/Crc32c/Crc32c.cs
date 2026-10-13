using System.Buffers.Binary;

namespace UotanStudio.Hash.Crc32c;

/// <summary>
/// CRC32c (Castagnoli, polynomial <c>0x82F63B78</c>) following the Linux kernel's <c>crc32c_le</c> convention:
/// register seed semantics, no final XOR, so callers can continue a checksum across disjoint spans.
/// <para>CRC32c（Castagnoli，多项式 <c>0x82F63B78</c>），与 Linux 内核 <c>crc32c_le</c> 约定一致：寄存器 seed 语义、
/// 无最终异或，调用方可以对不相邻的数据分段续算。</para>
/// </summary>
/// <remarks>
/// The hot loop consumes eight bytes per iteration through eight 256-entry tables (slice-by-8) and falls back to
/// the byte-at-a-time table for the trailing remainder, so the register semantics stay identical for every length.
/// <para>热循环借助 8 张 256 项表每轮消费 8 字节（slice-by-8），尾部余数字节回退到逐字节查表，
/// 因此任意长度下的寄存器语义完全一致。</para>
/// </remarks>
internal static class Crc32c
{
    private const uint Polynomial = 0x82F63B78;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    /// <summary>
    /// Register initial value (<c>~0</c>) used to start a fresh CRC32c checksum under the crc32c_le convention.
    /// <para>crc32c_le 约定下开始一次全新 CRC32c 计算所用的寄存器初值（<c>~0</c>）。</para>
    /// </summary>
    internal const uint InitialSeed = 0xFFFF_FFFF;

    private static readonly uint[] Table = InitTable();
    private static readonly uint[] SliceTable = InitSliceTable();

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
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = InitialSeed)
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