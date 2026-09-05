using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android Vendor Boot 镜像头部信息。
/// <para>
/// Android vendor boot image header information.</para>
/// <para>
/// 存储解析后的 Vendor Boot 镜像元数据，包括页面大小、RAMDisk 信息、DTB 信息等。</para>
/// <para>
/// Stores parsed vendor boot image metadata, including page size, ramdisk info, DTB info, etc.</para>
/// </summary>
public class VendorBootHeader
{
    /// <summary>
    /// 头部版本（3 或 4）。
    /// <para>The header version (3 or 4).</para>
    /// </summary>
    public uint HeaderVersion { get; set; }

    /// <summary>
    /// 页面大小（字节数）。
    /// <para>The page size in bytes.</para>
    /// </summary>
    public uint PageSize { get; set; }

    /// <summary>
    /// 内核加载地址。
    /// <para>The kernel load address.</para>
    /// </summary>
    public uint KernelLoadAddress { get; set; }

    /// <summary>
    /// Vendor RAMDisk 加载地址。
    /// <para>The vendor ramdisk load address.</para>
    /// </summary>
    public uint RamdiskLoadAddress { get; set; }

    /// <summary>
    /// Vendor RAMDisk 大小（字节数）。
    /// <para>The vendor ramdisk size in bytes.</para>
    /// </summary>
    public uint VendorRamdiskSize { get; set; }

    /// <summary>
    /// Vendor 命令行参数。
    /// <para>The vendor command line.</para>
    /// </summary>
    public string VendorCmdline { get; set; } = string.Empty;

    /// <summary>
    /// 标签加载地址。
    /// <para>The tags load address.</para>
    /// </summary>
    public uint TagsLoadAddress { get; set; }

    /// <summary>
    /// Vendor 头部大小（字节数）。
    /// <para>The vendor header size in bytes.</para>
    /// </summary>
    public uint VendorHeaderSize { get; set; }

    /// <summary>
    /// DTB 大小（字节数）。
    /// <para>The DTB size in bytes.</para>
    /// </summary>
    public uint DtbSize { get; set; }

    /// <summary>
    /// DTB 加载地址。
    /// <para>The DTB load address.</para>
    /// </summary>
    public ulong DtbAddress { get; set; }

    /// <summary>
    /// Vendor RAMDisk 表总大小（字节数，仅 V4）。
    /// <para>The total size of the vendor ramdisk table in bytes (V4 only).</para>
    /// </summary>
    public uint VendorRamdiskTableSize { get; set; }

    /// <summary>
    /// Vendor RAMDisk 表条目数量（仅 V4）。
    /// <para>The number of vendor ramdisk table entries (V4 only).</para>
    /// </summary>
    public uint VendorRamdiskTableEntryNum { get; set; }

    /// <summary>
    /// Vendor RAMDisk 表条目大小（字节数，仅 V4）。
    /// <para>The size of each vendor ramdisk table entry in bytes (V4 only).</para>
    /// </summary>
    public uint VendorRamdiskTableEntrySize { get; set; }

    /// <summary>
    /// Vendor RAMDisk 表偏移量（仅 V4）。
    /// <para>The vendor ramdisk table offset (V4 only).</para>
    /// </summary>
    public uint VendorRamdiskTableOffset { get; set; }

    /// <summary>
    /// Vendor Bootconfig 大小（字节数，仅 V4）。
    /// <para>The vendor bootconfig size in bytes (V4 only).</para>
    /// </summary>
    public uint VendorBootconfigSize { get; set; }

    /// <summary>
    /// Vendor RAMDisk 压缩格式。
    /// <para>The vendor ramdisk compression type.</para>
    /// </summary>
    public CompressionType VendorRamdiskCompression { get; set; }

    /// <summary>
    /// 转换为字符串格式，输出头部信息。
    /// <para>Converts to a string format, outputting header information.</para>
    /// </summary>
    /// <returns>头部信息的字符串表示。<para>The string representation of the header information.</para></returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Header Version: {HeaderVersion}");
        sb.AppendLine($"Page Size: {PageSize}");
        sb.AppendLine($"Kernel Load Address: 0x{KernelLoadAddress:X8}");
        sb.AppendLine($"Ramdisk Load Address: 0x{RamdiskLoadAddress:X8}");
        sb.AppendLine($"Vendor Ramdisk Size: {VendorRamdiskSize}");
        sb.AppendLine($"Vendor Cmdline: {VendorCmdline}");
        sb.AppendLine($"Tags Load Address: 0x{TagsLoadAddress:X8}");
        sb.AppendLine($"Vendor Header Size: {VendorHeaderSize}");
        sb.AppendLine($"DTB Size: {DtbSize}");
        sb.AppendLine($"DTB Address: 0x{DtbAddress:X16}");

        if (HeaderVersion >= 4)
        {
            sb.AppendLine($"Vendor Ramdisk Table Entry Num: {VendorRamdiskTableEntryNum}");
            sb.AppendLine($"Vendor Ramdisk Table Entry Size: {VendorRamdiskTableEntrySize}");
            sb.AppendLine($"Vendor Ramdisk Table Offset: {VendorRamdiskTableOffset}");
            sb.AppendLine($"Vendor Bootconfig Size: {VendorBootconfigSize}");
        }
        else
        {
            // V3 头不含 V4 专属字段，提示说明省略原因而非缺失
            sb.AppendLine("(V4-only fields omitted: Ramdisk Table, Bootconfig)");
        }

        return sb.ToString();
    }
}