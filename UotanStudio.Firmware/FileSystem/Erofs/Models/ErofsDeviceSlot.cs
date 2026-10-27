using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// On-disk device slot (struct erofs_deviceslot, 128 bytes): tag[64], blocks_lo, uniaddr_lo,
/// blocks_hi, uniaddr_hi, reserved[52]. The 48-bit accessors follow the INCOMPAT_48BIT layout.
/// <para>磁盘设备槽位（struct erofs_deviceslot，128 字节）：tag[64]、blocks_lo、uniaddr_lo、
/// blocks_hi、uniaddr_hi、reserved[52]。48 位访问器遵循 INCOMPAT_48BIT 布局。</para>
/// </summary>
internal sealed class ErofsDeviceSlot
{
    public const int Size = 128;

    /// <summary>Byte width of the on-disk device tag field (NUL-padded).<para>磁盘设备 tag 字段的字节宽度（以 NUL 填充）。</para></summary>
    public const int TagSize = 64;

    private const int TagLength = TagSize;

    private const int OffsetBlocksLo = 64;

    private const int OffsetUniAddrLo = 68;

    private const int OffsetBlocksHi = 72;

    private const int OffsetUniAddrHi = 74;

    private const int ReservedLength = 52;

    public byte[] Tag { get; init; } = new byte[TagLength];

    /// <summary>Total block count of this device (48-bit across blocks_lo/blocks_hi).<para>该设备的总块数（blocks_lo/blocks_hi 组成的 48 位）。</para></summary>
    public ulong Blocks { get; init; }

    /// <summary>Unified starting block of this device (48-bit across uniaddr_lo/uniaddr_hi).<para>该设备的统一起始块号（uniaddr_lo/uniaddr_hi 组成的 48 位）。</para></summary>
    public ulong UniAddr { get; init; }

    /// <summary>Reads one device slot, honouring the image's 48-bit feature state. The reader folds
    /// <c>blocks_hi</c>/<c>uniaddr_hi</c> in only while <c>INCOMPAT_48BIT</c> is declared, and those two words
    /// otherwise sit in space the format reserves, so an image without the feature may carry arbitrary bytes
    /// there that must be ignored rather than treated as an address extension.
    /// <para>按镜像的 48 位特性状态读取一个设备槽位。仅在声明 <c>INCOMPAT_48BIT</c> 时才并入
    /// <c>blocks_hi</c>/<c>uniaddr_hi</c>；未声明时这两个字位于格式保留的空间内，因此未携带该特性的镜像可以在
    /// 其中存放任意字节，必须忽略它们而不是当作地址扩展。</para>
    /// </summary>
    /// <param name="data">Slot bytes, at least <see cref="Size"/> long.
    /// <para>槽位字节，至少 <see cref="Size"/> 长。</para></param>
    /// <param name="has48Bit">Whether the containing image declares <c>INCOMPAT_48BIT</c>.
    /// <para>所属镜像是否声明 <c>INCOMPAT_48BIT</c>。</para></param>
    /// <returns>The decoded slot; the high halves are zero when <paramref name="has48Bit"/> is clear.
    /// <para>解码得到的槽位；<paramref name="has48Bit"/> 未置位时高半部为零。</para></returns>
    /// <exception cref="FormatException">The region is shorter than <see cref="Size"/>.
    /// <para>区域短于 <see cref="Size"/>。</para></exception>
    public static ErofsDeviceSlot FromBytes(ReadOnlySpan<byte> data, bool has48Bit)
    {
        if (data.Length < Size)
        {
            throw new FormatException($"EROFS device slot region too short: {data.Length} bytes.");
        }

        return new ErofsDeviceSlot
        {
            Tag = data[..TagLength].ToArray(),
            Blocks = ReadAddress(data, OffsetBlocksLo, OffsetBlocksHi, has48Bit),
            UniAddr = ReadAddress(data, OffsetUniAddrLo, OffsetUniAddrHi, has48Bit),
        };
    }

    private static ulong ReadAddress(ReadOnlySpan<byte> data, int lowOffset, int highOffset, bool has48Bit)
        => has48Bit
            ? ReadLe48(data, lowOffset, highOffset)
            : BinaryPrimitives.ReadUInt32LittleEndian(data[lowOffset..]);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Device slot destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        Tag.AsSpan(0, Math.Min(TagLength, Tag.Length)).CopyTo(destination);
        WriteLe48(destination, OffsetBlocksLo, OffsetBlocksHi, Blocks);
        WriteLe48(destination, OffsetUniAddrLo, OffsetUniAddrHi, UniAddr);
    }

    private static ulong ReadLe48(ReadOnlySpan<byte> data, int lowOffset, int highOffset)
    {
        var lo = BinaryPrimitives.ReadUInt32LittleEndian(data[lowOffset..]);
        var hi = BinaryPrimitives.ReadUInt16LittleEndian(data[highOffset..]);
        return lo | ((ulong)hi << 32);
    }

    private static void WriteLe48(Span<byte> destination, int lowOffset, int highOffset, ulong value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[lowOffset..], (uint)value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[highOffset..], (ushort)(value >> 32));
    }
}