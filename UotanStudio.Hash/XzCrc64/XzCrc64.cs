using System.Buffers.Binary;

namespace UotanStudio.Hash.XzCrc64;

/// <summary>
/// XZ's CRC-64: reflected polynomial <c>0xc96c5795d7870f42</c>, all-one register initialization and a final
/// complement. <c>System.IO.Hashing.Crc64</c> implements the non-reflected ECMA-182 variant, so it cannot stand in
/// for the check field an <c>.xz</c> container stores.
/// <para>XZ 使用的 CRC-64：反射多项式 <c>0xc96c5795d7870f42</c>、寄存器全 1 初值并做最终取反。
/// <c>System.IO.Hashing.Crc64</c> 实现的是非反射的 ECMA-182 变体，因此无法代替 <c>.xz</c> 容器所存储的校验字段。</para>
/// </summary>
/// <remarks>
/// The hot loop folds eight bytes per iteration through eight 256-entry tables (slice-by-8) and keeps the byte-at-a-time
/// table for the trailing remainder, so any chunking of a stream yields the same register as the byte-at-a-time form.
/// The eight tables are one flat array to keep them adjacent in memory; together they hold <c>8 * 256 * 8</c> bytes.
/// <para>热循环借助 8 张 256 项表每轮合并 8 字节（slice-by-8），尾部余数仍用逐字节表，因此任意分块方式都得到与逐字节形态
/// 相同的寄存器值。8 张表放在一个扁平数组中以保证内存相邻，合计 <c>8 * 256 * 8</c> 字节。</para>
/// </remarks>
public sealed class XzCrc64
{
    private const int TableEntries = 256;
    private const int SliceCount = 8;

    private static readonly ulong[] Tables = CreateTables();

    private ulong _state = ulong.MaxValue;

    /// <summary>
    /// Folds more bytes into the running checksum, continuing from whatever has been appended so far.
    /// <para>把更多字节并入进行中的校验和，从此前已累加的位置继续。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold; an empty span leaves the state unchanged.
    /// <para>要并入的字节；空区间不改变状态。</para>
    /// </param>
    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        var index = 0;
        while (data.Length - index >= sizeof(ulong))
        {
            var mixed = state ^ BinaryPrimitives.ReadUInt64LittleEndian(data[index..]);
            state = Tables[0x700 + (byte)mixed]
                ^ Tables[0x600 + (byte)(mixed >> 8)]
                ^ Tables[0x500 + (byte)(mixed >> 16)]
                ^ Tables[0x400 + (byte)(mixed >> 24)]
                ^ Tables[0x300 + (byte)(mixed >> 32)]
                ^ Tables[0x200 + (byte)(mixed >> 40)]
                ^ Tables[0x100 + (byte)(mixed >> 48)]
                ^ Tables[(byte)(mixed >> 56)];
            index += sizeof(ulong);
        }

        for (; index < data.Length; index++)
        {
            state = Tables[(byte)(state ^ data[index])] ^ (state >> 8);
        }

        _state = state;
    }

    /// <summary>
    /// Returns the finalized checksum without changing the accumulated state, so more bytes may still be appended.
    /// <para>返回定稿校验和且不改变已累加状态，因此仍可继续并入字节。</para>
    /// </summary>
    /// <returns>
    /// The complement of the register, which is the value the XZ container stores.
    /// <para>寄存器取反值，即 XZ 容器所存储的数值。</para>
    /// </returns>
    public ulong GetCurrentHashAsUInt64() => ~_state;

    /// <summary>
    /// Computes the XZ CRC-64 of a complete buffer in one call.
    /// <para>对完整缓冲区一次性计算 XZ CRC-64。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to checksum; an empty span yields <c>0</c>.
    /// <para>参与校验的字节；空区间得到 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The stored checksum value. <para>容器所存储的校验和值。</para>
    /// </returns>
    public static ulong HashToUInt64(ReadOnlySpan<byte> data)
    {
        var crc = new XzCrc64();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Builds the eight slice tables from the reflected polynomial: the first slice is the byte-at-a-time table and each
    /// following slice folds one more byte of the same recurrence.
    /// <para>由反射多项式构造 8 张切片表：第一张是逐字节表，其后每一张在同一递推上再多折叠一个字节。</para>
    /// </summary>
    /// <returns>The flat table array, eight 256-entry slices laid out back to back.
    /// <para>扁平表数组，八张 256 项切片依次相邻。</para></returns>
    private static ulong[] CreateTables()
    {
        const ulong reflectedPolynomial = 0xc96c5795d7870f42;
        var tables = new ulong[TableEntries * SliceCount];
        for (var i = 0; i < TableEntries; i++)
        {
            var value = (ulong)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) != 0 ? reflectedPolynomial : 0);
            }

            tables[i] = value;
        }

        for (var slice = 1; slice < SliceCount; slice++)
        {
            for (var i = 0; i < TableEntries; i++)
            {
                var previous = tables[((slice - 1) * TableEntries) + i];
                tables[(slice * TableEntries) + i] = (previous >> 8) ^ tables[(byte)previous];
            }
        }

        return tables;
    }
}