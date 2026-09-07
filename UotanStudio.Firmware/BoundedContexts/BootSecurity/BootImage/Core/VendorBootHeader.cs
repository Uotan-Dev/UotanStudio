using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public class VendorBootHeader
{
    public uint HeaderVersion { get; set; }

    public uint PageSize { get; set; }

    public uint KernelLoadAddress { get; set; }

    public uint RamdiskLoadAddress { get; set; }

    public uint VendorRamdiskSize { get; set; }

    public string VendorCmdline { get; set; } = string.Empty;

    /// <summary>Gets or sets the vendor boot product name.<para>获取或设置 Vendor Boot 产品名称。</para></summary>
    public string VendorName { get; set; } = string.Empty;

    public uint TagsLoadAddress { get; set; }

    public uint VendorHeaderSize { get; set; }

    public uint DtbSize { get; set; }

    public ulong DtbAddress { get; set; }

    public uint VendorRamdiskTableSize { get; set; }

    public uint VendorRamdiskTableEntryNum { get; set; }

    public uint VendorRamdiskTableEntrySize { get; set; }

    public uint VendorRamdiskTableOffset { get; set; }

    public uint VendorBootconfigSize { get; set; }

    public CompressionType VendorRamdiskCompression { get; set; }

    /// <summary>
    /// Converts to a string format, outputting header information.
    /// <para>转换为字符串格式，输出头部信息。</para>
    /// </summary>
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

            sb.AppendLine("(V4-only fields omitted: Ramdisk Table, Bootconfig)");
        }

        return sb.ToString();
    }
}
