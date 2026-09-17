using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

/// <summary>
/// On-disk device slot (struct erofs_deviceslot, 128 bytes): tag[64], blocks_lo, uniaddr_lo,
/// blocks_hi, uniaddr_hi, reserved[52]. The 48-bit accessors mirror the upstream INCOMPAT_48BIT layout.
/// <para>磁盘设备槽位（struct erofs_deviceslot，128 字节）：tag[64]、blocks_lo、uniaddr_lo、
/// blocks_hi、uniaddr_hi、reserved[52]。48 位访问器与上游 INCOMPAT_48BIT 布局一致。</para>
/// </summary>
internal sealed class ErofsDeviceSlot
{
    public const int Size = 128;

    private const int TagLength = 64;

    private const int OffsetBlocksLo = 64;

    private const int OffsetUniAddrLo = 68;

    private const int OffsetBlocksHi = 72;

    private const int OffsetUniAddrHi = 74;

    private const int ReservedLength = 52;

    public byte[] Tag { get; init; } = new byte[TagLength];

    /// <summary>Total block count of this device (48-bit across blocks_lo/blocks_hi).</summary>
    /// <para>该设备的总块数（blocks_lo/blocks_hi 组成的 48 位）。</para>
    public ulong Blocks { get; init; }

    /// <summary>Unified starting block of this device (48-bit across uniaddr_lo/uniaddr_hi).</summary>
    /// <para>该设备的统一起始块号（uniaddr_lo/uniaddr_hi 组成的 48 位）。</para>
    public ulong UniAddr { get; init; }

    public static ErofsDeviceSlot FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException($"EROFS device slot region too short: {data.Length} bytes.");
        }

        return new ErofsDeviceSlot
        {
            Tag = data[..TagLength].ToArray(),
            Blocks = ReadLe48(data, OffsetBlocksLo),
            UniAddr = ReadLe48(data, OffsetUniAddrLo),
        };
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Device slot destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        Tag.AsSpan(0, Math.Min(TagLength, Tag.Length)).CopyTo(destination);
        WriteLe48(destination, OffsetBlocksLo, Blocks);
        WriteLe48(destination, OffsetUniAddrLo, UniAddr);
    }

    private static ulong ReadLe48(ReadOnlySpan<byte> data, int offset)
    {
        var lo = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        var hi = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
        return lo | ((ulong)hi << 32);
    }

    private static void WriteLe48(Span<byte> destination, int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], (uint)value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + 4)..], (ushort)(value >> 32));
    }
}