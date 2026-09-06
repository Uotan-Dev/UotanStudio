using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

internal sealed class ErofsDeviceSlot
{
    public const int Size = 128;

    private const int TagLength = 64;

    private const int ReservedLength = 56;

    public byte[] Tag { get; init; } = new byte[TagLength];

    public uint Blocks { get; init; }

    public uint MappedBlkAddr { get; init; }

    /// <summary>
    /// Parses a device slot from bytes (little-endian, at least 128 bytes).
    /// <para>从字节解析设备槽位（小端，至少 128 字节）。</para>
    /// </summary>
    public static ErofsDeviceSlot FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException($"EROFS device slot region too short: {data.Length} bytes.");
        }

        return new ErofsDeviceSlot
        {
            Tag = data[..TagLength].ToArray(),
            Blocks = BinaryPrimitives.ReadUInt32LittleEndian(data[TagLength..]),
            MappedBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(data[(TagLength + 4)..]),
        };
    }

    /// <summary>
    /// Serializes the device slot into a buffer of at least 128 bytes.
    /// <para>将设备槽位序列化到缓冲区（至少 128 字节）。</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Device slot destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        Tag.AsSpan(0, Math.Min(TagLength, Tag.Length)).CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TagLength..], Blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(TagLength + 4)..], MappedBlkAddr);
    }
}
