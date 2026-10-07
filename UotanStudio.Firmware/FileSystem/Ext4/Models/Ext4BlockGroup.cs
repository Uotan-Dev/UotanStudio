using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Ext4.Models;

internal sealed record Ext4BlockGroup
{
    public uint BlockBitmapLo { get; init; }

    public uint BlockBitmapHi { get; init; }

    public uint InodeBitmapLo { get; init; }

    public uint InodeBitmapHi { get; init; }

    public uint InodeTableLo { get; init; }

    public uint InodeTableHi { get; init; }

    public ushort FreeBlocksCountLo { get; init; }

    public ushort FreeBlocksCountHi { get; init; }

    public ushort FreeInodesCountLo { get; init; }

    public ushort FreeInodesCountHi { get; init; }

    public ushort UsedDirsCountLo { get; init; }

    public ushort UsedDirsCountHi { get; init; }

    public ulong BlockBitmap => ((ulong)BlockBitmapHi << 32) | BlockBitmapLo;

    public ulong InodeBitmap => ((ulong)InodeBitmapHi << 32) | InodeBitmapLo;

    public ulong InodeTable => ((ulong)InodeTableHi << 32) | InodeTableLo;

    public ushort Checksum { get; init; }

    /// <summary>
    /// Parses one group descriptor. data must be at least descSize bytes; 64-bit fields are only read when descSize is 64 or greater.
    /// <para>解析单个组描述符。</para>
    /// </summary>
    /// <param name="data">Descriptor bytes of one block group, holding the little-endian low halves at offsets 0 to 17, the descriptor checksum at 30 and the high halves at 32 to 49 in the 64-bit layout.
    /// <para>单个块组的描述符字节：偏移 0 至 17 是小端低位半部，30 处是描述符校验和，64 位布局的 32 至 49 处是高位半部。</para></param>
    /// <param name="descSize">Descriptor size announced by the superblock; values below 32 are treated as 32 and only 64 or more reads the high halves of the bitmaps, inode table and counters.
    /// <para>超级块声明的描述符大小；小于 32 时按 32 处理，只有不小于 64 才会读取位图、inode 表与计数的高位半部。</para></param>
    /// <returns>Descriptor whose combined 64-bit <c>BlockBitmap</c>, <c>InodeBitmap</c> and <c>InodeTable</c> values stay 32-bit unless <paramref name="descSize"/> is 64 or greater.
    /// <para>描述符结果；除非 <paramref name="descSize"/> 不小于 64，合并后的 64 位 <c>BlockBitmap</c>、<c>InodeBitmap</c> 与 <c>InodeTable</c> 仍只有 32 位有效。</para></returns>
    public static Ext4BlockGroup FromBytes(ReadOnlySpan<byte> data, int descSize)
    {
        var size = Math.Max(descSize, 32);
        if (data.Length < size)
        {
            throw new FileSystemCorruptedException($"Block group descriptor is truncated (need {size} bytes, got {data.Length}).");
        }

        var result = new Ext4BlockGroup
        {
            BlockBitmapLo = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]),
            InodeBitmapLo = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
            InodeTableLo = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]),
            FreeBlocksCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]),
            FreeInodesCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]),
            UsedDirsCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]),
            Checksum = BinaryPrimitives.ReadUInt16LittleEndian(data[30..32]),
        };

        if (descSize >= 64)
        {
            result = result with
            {
                BlockBitmapHi = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]),
                InodeBitmapHi = BinaryPrimitives.ReadUInt32LittleEndian(data[36..40]),
                InodeTableHi = BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]),
                FreeBlocksCountHi = BinaryPrimitives.ReadUInt16LittleEndian(data[44..46]),
                FreeInodesCountHi = BinaryPrimitives.ReadUInt16LittleEndian(data[46..48]),
                UsedDirsCountHi = BinaryPrimitives.ReadUInt16LittleEndian(data[48..50]),
            };
        }

        return result;
    }
}