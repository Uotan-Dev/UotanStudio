using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Core;

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
