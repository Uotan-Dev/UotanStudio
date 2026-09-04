using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 设备槽位（128 字节，对齐 Linux 内核 <c>struct erofs_deviceslot</c>）。
/// 设备表从 s_devt_slotoff * block_size 起，每个设备占一个 128 字节槽位；
/// 设备 0 即主镜像，设备 1..N 为额外设备。
/// <para>EROFS device slot (128 bytes, matching the Linux kernel
/// <c>struct erofs_deviceslot</c>). The device table starts at s_devt_slotoff * block_size; each
/// device occupies one 128-byte slot. Device 0 is the primary image, devices 1..N are extra
/// devices.</para>
/// </summary>
internal sealed class ErofsDeviceSlot
{
    /// <summary>设备槽位大小（128 字节）。Device slot size (128 bytes).</summary>
    public const int Size = 128;

    /// <summary>设备标签最大长度（64 字节）。Maximum device tag length (64 bytes).</summary>
    private const int TagLength = 64;

    /// <summary>保留区域长度（56 字节）。Reserved area length (56 bytes).</summary>
    private const int ReservedLength = 56;

    /// <summary>
    /// 设备标签（64 字节，NUL 填充）。用于多设备镜像标识各设备。
    /// <para>Device tag (64 bytes, NUL-padded). Used to identify each device in a multi-device
    /// image.</para>
    /// </summary>
    public byte[] Tag { get; init; } = new byte[TagLength];

    /// <summary>
    /// 设备总块数（s_blocks 设备本地部分）。Total blocks on this device.
    /// </summary>
    public uint Blocks { get; init; }

    /// <summary>
    /// 映射的起始块号（主镜像内该设备数据的起始块地址）。
    /// <para>Mapped start block address (the start of this device's data within the primary
    /// image).</para>
    /// </summary>
    public uint MappedBlkAddr { get; init; }

    /// <summary>
    /// 从字节解析设备槽位（小端，至少 128 字节）。
    /// <para>Parses a device slot from bytes (little-endian, at least 128 bytes).</para>
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
    /// 将设备槽位序列化到缓冲区（至少 128 字节）。
    /// <para>Serializes the device slot into a buffer of at least 128 bytes.</para>
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