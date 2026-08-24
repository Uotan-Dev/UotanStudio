namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Vendor RAMDisk 表条目（V4 格式）。
/// <para>
/// Vendor ramdisk table entry (V4 format).</para>
/// <para>
/// 描述 V4 Vendor Boot 镜像中单个 RAMDisk 的元数据，包括大小、偏移、类型和名称。</para>
/// <para>
/// Describes the metadata of a single ramdisk in a V4 vendor boot image, including size, offset, type, and name.</para>
/// </summary>
public class VendorRamdiskTableEntry
{
    /// <summary>
    /// RAMDisk 大小（字节数）。
    /// <para>The ramdisk size in bytes.</para>
    /// </summary>
    public uint RamdiskSize { get; set; }

    /// <summary>
    /// RAMDisk 在 vendor_ramdisk 区域中的偏移量。
    /// <para>The ramdisk offset within the vendor_ramdisk area.</para>
    /// </summary>
    public uint RamdiskOffset { get; set; }

    /// <summary>
    /// RAMDisk 类型（0=None, 1=Platform, 2=Recovery, 3=DLKM）。
    /// <para>The ramdisk type (0=None, 1=Platform, 2=Recovery, 3=DLKM).</para>
    /// </summary>
    public uint RamdiskType { get; set; }

    /// <summary>
    /// RAMDisk 名称。
    /// <para>The ramdisk name.</para>
    /// </summary>
    public string RamdiskName { get; set; } = string.Empty;

    /// <summary>
    /// 板级 ID 数组（16 个 uint32）。
    /// <para>The board ID array (16 uint32 values).</para>
    /// </summary>
    public uint[] RamdiskBoardId { get; set; } = new uint[16];
}