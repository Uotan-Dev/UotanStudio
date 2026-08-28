using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

/// <summary>
/// ext4 block group descriptor, parsed in little-endian order with support for both
/// legacy 32-byte and 64-bit (64-byte) descriptors.
/// <para>ext4 块组描述符，按小端解析，同时支持传统 32 字节与 64 位（64 字节）两种描述符。</para>
/// </summary>
internal sealed record Ext4BlockGroup
{
    /// <summary>
    /// Low 32 bits of the block bitmap block number.
    /// <para>块位图块号低 32 位。</para>
    /// </summary>
    public uint BlockBitmapLo { get; init; }

    /// <summary>
    /// High 32 bits of the block bitmap block number.
    /// <para>块位图块号高 32 位。</para>
    /// </summary>
    public uint BlockBitmapHi { get; init; }

    /// <summary>
    /// Low 32 bits of the inode bitmap block number.
    /// <para>inode 位图块号低 32 位。</para>
    /// </summary>
    public uint InodeBitmapLo { get; init; }

    /// <summary>
    /// High 32 bits of the inode bitmap block number.
    /// <para>inode 位图块号高 32 位。</para>
    /// </summary>
    public uint InodeBitmapHi { get; init; }

    /// <summary>
    /// Low 32 bits of the inode table block number.
    /// <para>inode 表块号低 32 位。</para>
    /// </summary>
    public uint InodeTableLo { get; init; }

    /// <summary>
    /// High 32 bits of the inode table block number.
    /// <para>inode 表块号高 32 位。</para>
    /// </summary>
    public uint InodeTableHi { get; init; }

    /// <summary>
    /// Free block count within the group, low 16 bits.
    /// <para>组内空闲块数低 16 位。</para>
    /// </summary>
    public ushort FreeBlocksCountLo { get; init; }

    /// <summary>
    /// Free block count within the group, high 16 bits.
    /// <para>组内空闲块数高 16 位。</para>
    /// </summary>
    public ushort FreeBlocksCountHi { get; init; }

    /// <summary>
    /// Free inode count within the group, low 16 bits.
    /// <para>组内空闲 inode 数低 16 位。</para>
    /// </summary>
    public ushort FreeInodesCountLo { get; init; }

    /// <summary>
    /// Free inode count within the group, high 16 bits.
    /// <para>组内空闲 inode 数高 16 位。</para>
    /// </summary>
    public ushort FreeInodesCountHi { get; init; }

    /// <summary>
    /// Number of directories in the group, low 16 bits.
    /// <para>组内目录数低 16 位。</para>
    /// </summary>
    public ushort UsedDirsCountLo { get; init; }

    /// <summary>
    /// Number of directories in the group, high 16 bits.
    /// <para>组内目录数高 16 位。</para>
    /// </summary>
    public ushort UsedDirsCountHi { get; init; }

    /// <summary>
    /// Absolute block bitmap block number.
    /// <para>块位图块绝对块号。</para>
    /// </summary>
    public ulong BlockBitmap => ((ulong)BlockBitmapHi << 32) | BlockBitmapLo;

    /// <summary>
    /// Absolute inode bitmap block number.
    /// <para>inode 位图块绝对块号。</para>
    /// </summary>
    public ulong InodeBitmap => ((ulong)InodeBitmapHi << 32) | InodeBitmapLo;

    /// <summary>
    /// Absolute inode table block number.
    /// <para>inode 表块绝对块号。</para>
    /// </summary>
    public ulong InodeTable => ((ulong)InodeTableHi << 32) | InodeTableLo;

    /// <summary>
    /// Parses one group descriptor. <paramref name="data"/> must be at least
    /// <paramref name="descSize"/> bytes; 64-bit fields are only read when
    /// <paramref name="descSize"/> is 64 or greater.
    /// <para>解析单个组描述符。<paramref name="data"/> 至少须为 <paramref name="descSize"/> 字节；
    /// 仅当 <paramref name="descSize"/> 大于等于 64 时读取 64 位扩展字段。</para>
    /// </summary>
    public static Ext4BlockGroup FromBytes(ReadOnlySpan<byte> data, int descSize)
    {
        var size = Math.Max(descSize, 32);
        if (data.Length < size)
        {
            throw new InvalidDataException($"Block group descriptor is truncated (need {size} bytes, got {data.Length}).");
        }

        var result = new Ext4BlockGroup
        {
            BlockBitmapLo = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]),
            InodeBitmapLo = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]),
            InodeTableLo = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]),
            FreeBlocksCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]),
            FreeInodesCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]),
            UsedDirsCountLo = BinaryPrimitives.ReadUInt16LittleEndian(data[16..18]),
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