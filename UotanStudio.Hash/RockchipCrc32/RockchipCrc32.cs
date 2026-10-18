using System.Buffers.Binary;

namespace UotanStudio.Hash.RockchipCrc32;

/// <summary>
/// Non-reflected CRC-32 (polynomial <c>0x04C10DB7</c>, MSB-first, init 0, no final XOR) matching the
/// <c>gTable_Crc32</c> semantics of Rockchip's rkaf/rkdeveloptool <c>crc.cpp</c>, used by RKAF/RKFW
/// container and loader image checksums.
/// <para>非反射式 CRC-32（多项式 <c>0x04C10DB7</c>，高位优先，初值 0，无最终异或），对应 Rockchip
/// rkaf/rkdeveloptool 的 <c>crc.cpp</c> 中 <c>gTable_Crc32</c> 的语义，用于 RKAF/RKFW 容器与 loader
/// 镜像校验。</para>
/// </summary>
/// <remarks>
/// The hot loop consumes eight bytes per iteration through eight 256-entry tables (slice-by-8) and falls back to the
/// byte-at-a-time table for the trailing remainder, so a caller-supplied seed and any trailing length keep the exact
/// register semantics of the upstream byte loop.
/// <para>热循环借助 8 张 256 项表每轮消费 8 字节（slice-by-8），尾部余数字节回退到逐字节查表，
/// 因此调用方给定的 seed 与任意尾部长度都保持上游逐字节循环的寄存器语义。</para>
/// </remarks>
public static class RockchipCrc32
{
    private const uint Polynomial = 0x04C10DB7;
    private const int SliceSize = 8;
    private const int TableEntries = 256;
    private const int SliceTableEntries = TableEntries * SliceSize;

    private static readonly uint[] SliceTable = BuildSliceTable();

    /// <summary>Computes the Rockchip CRC-32 of the data starting from <paramref name="seed"/>. To checksum a stream in chunks, pass the previous return value as the next <paramref name="seed"/>.
    /// <para>以 <paramref name="seed"/> 为初值计算数据的 Rockchip CRC-32。分块计算流数据时，把上一次的 返回值作为下一次的 <paramref name="seed"/> 传入即可续算。</para></summary>
    /// <param name="data">Bytes to hash; an empty span returns <paramref name="seed"/> unchanged.
    /// <para>参与计算的字节；空区间原样返回 <paramref name="seed"/>。</para></param>
    /// <param name="seed">Register initial value; the convention for full images is 0, and the previous return value for chunked continuation.
    /// <para>寄存器初值；整图计算按约定取 0，分块续算时取上一次的返回值。</para></param>
    /// <returns>The Rockchip CRC-32 register value after consuming the data.
    /// <para>消费完数据后的 Rockchip CRC-32 寄存器值。</para></returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var crc = seed;
        var table = SliceTable;
        while (data.Length >= SliceSize)
        {
            var head = BinaryPrimitives.ReadUInt32BigEndian(data);
            var tail = BinaryPrimitives.ReadUInt32BigEndian(data[sizeof(uint)..]);
            var mixed = crc ^ head;
            crc = table[0x700 + (byte)(mixed >> 24)]
                ^ table[0x600 + (byte)(mixed >> 16)]
                ^ table[0x500 + (byte)(mixed >> 8)]
                ^ table[0x400 + (byte)mixed]
                ^ table[0x300 + (byte)(tail >> 24)]
                ^ table[0x200 + (byte)(tail >> 16)]
                ^ table[0x100 + (byte)(tail >> 8)]
                ^ table[(byte)tail];
            data = data[SliceSize..];
        }

        foreach (var value in data)
        {
            crc = (crc << 8) ^ table[(byte)(crc >> 24) ^ value];
        }

        return crc;
    }

    private static uint[] BuildSliceTable()
    {
        var table = new uint[SliceTableEntries];
        for (var value = 0; value < TableEntries; value++)
        {
            var entry = (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 0x80000000) != 0 ? (entry << 1) ^ Polynomial : entry << 1;
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
                table[current + index] = (carry << 8) ^ table[carry >> 24];
            }
        }

        return table;
    }
}