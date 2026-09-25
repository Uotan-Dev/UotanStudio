using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

/// <summary>
/// Mutable model of the vendor boot image header (magic <c>VNDRBOOT</c>, header versions 3 and 4).
/// <para>Vendor Boot 镜像头部（魔数 <c>VNDRBOOT</c>，头部版本 3 与 4）的可变模型。</para>
/// </summary>
/// <remarks>
/// All multi-byte fields are little-endian on disk; numeric fields default to <c>0</c> and string fields to
/// <see cref="string.Empty"/> until a parse or pack assigns them.
/// <para>磁盘上的多字节字段均为小端序；数值字段默认为 <c>0</c>，字符串字段默认为 <see cref="string.Empty"/>，直到被解析或打包赋值。</para>
/// </remarks>
public class VendorBootHeader
{
    /// <summary>
    /// Declared vendor boot header version, read as a little-endian 32-bit field.
    /// <para>声明的 Vendor Boot 头部版本，按小端 32 位字段读取。</para>
    /// </summary>
    /// <value>
    /// The parser accepts only <c>3</c> and <c>4</c>; version <c>4</c> adds the ramdisk table and bootconfig fields.
    /// <para>解析器仅接受 <c>3</c> 与 <c>4</c>；版本 <c>4</c> 额外包含 RAMDisk 表与 bootconfig 字段。</para>
    /// </value>
    public uint HeaderVersion { get; set; }

    /// <summary>
    /// Image page size in bytes used to align the header and every component.
    /// <para>用于对齐头部及各组件的镜像页大小（字节）。</para>
    /// </summary>
    /// <value>
    /// The parser rejects values that are zero or not a power of two; a value of <c>0</c> makes the packer fall back to 4096.
    /// <para>解析器会拒绝为零或非 2 的幂的值；值为 <c>0</c> 时打包器回退为 4096。</para>
    /// </value>
    public uint PageSize { get; set; }

    /// <summary>
    /// 32-bit kernel load address field recorded in the header.
    /// <para>头部中记录的 32 位内核加载地址字段。</para>
    /// </summary>
    /// <value>
    /// Stored and re-serialized verbatim; a vendor boot image carries no kernel payload, so the value receives no interpretation.
    /// <para>原样保存并重新序列化；Vendor Boot 镜像不含内核载荷，因此该值不做任何解释。</para>
    /// </value>
    public uint KernelLoadAddress { get; set; }

    /// <summary>
    /// 32-bit ramdisk load address field recorded in the header.
    /// <para>头部中记录的 32 位 RAMDisk 加载地址字段。</para>
    /// </summary>
    /// <value>
    /// Little-endian field preserved verbatim by parse and pack round-trips without further meaning.
    /// <para>小端字段，在解析与打包往返中原样保留，不附加其他含义。</para>
    /// </value>
    public uint RamdiskLoadAddress { get; set; }

    /// <summary>
    /// Declared byte size of the main vendor ramdisk component.
    /// <para>主 Vendor RAMDisk 组件的声明字节大小。</para>
    /// </summary>
    /// <value>
    /// The component starts at the page boundary after the header; <c>0</c> means the ramdisk is absent, and reads are capped at 512 MiB.
    /// <para>该组件位于头部之后按页对齐的位置；<c>0</c> 表示 RAMDisk 缺失，读取上限为 512 MiB。</para>
    /// </value>
    public uint VendorRamdiskSize { get; set; }

    /// <summary>
    /// Vendor command line kept in the fixed 2048-byte ASCII header field.
    /// <para>保存在固定 2048 字节 ASCII 头部字段中的厂商命令行。</para>
    /// </summary>
    /// <value>
    /// Trailing NUL characters are trimmed on parse; <see cref="string.Empty"/> when the field is blank or before any parse.
    /// <para>解析时去除尾部 NUL 字符；字段为空或尚未解析时为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string VendorCmdline { get; set; } = string.Empty;

    /// <summary>Gets or sets the vendor boot product name.<para>获取或设置 Vendor Boot 产品名称。</para></summary>
    /// <value>
    /// Fixed 16-byte ASCII header field with trailing NULs trimmed on parse; <see cref="string.Empty"/> when the field is blank or before any parse.
    /// <para>固定 16 字节 ASCII 头部字段，解析时去除尾部 NUL；字段为空或尚未解析时为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string VendorName { get; set; } = string.Empty;

    /// <summary>
    /// 32-bit tags load address field recorded in the header.
    /// <para>头部中记录的 32 位 tags 加载地址字段。</para>
    /// </summary>
    /// <value>
    /// Little-endian field preserved verbatim by parse and pack round-trips without further meaning.
    /// <para>小端字段，在解析与打包往返中原样保留，不附加其他含义。</para>
    /// </value>
    public uint TagsLoadAddress { get; set; }

    /// <summary>
    /// Declared total header size in bytes; components are placed after it, aligned up to a page boundary.
    /// <para>声明的头部总大小（字节）；各组件位于其后并按页边界对齐。</para>
    /// </summary>
    /// <value>
    /// The parser requires at least the version minimum (2112 for version 3, 2128 for version 4); the packer substitutes the minimum when the value is smaller.
    /// <para>解析器要求不小于版本最小值（版本 3 为 2112，版本 4 为 2128）；值偏小时打包器会用最小值替代。</para>
    /// </value>
    public uint VendorHeaderSize { get; set; }

    /// <summary>
    /// Declared byte size of the embedded DTB blob.
    /// <para>内嵌 DTB 数据块的声明字节大小。</para>
    /// </summary>
    /// <value>
    /// The DTB starts at the page boundary after the vendor ramdisk; <c>0</c> means no DTB is present, and reads are capped at 512 MiB.
    /// <para>DTB 位于 Vendor RAMDisk 之后按页对齐的位置；<c>0</c> 表示无 DTB，读取上限为 512 MiB。</para>
    /// </value>
    public uint DtbSize { get; set; }

    /// <summary>
    /// 64-bit DTB load address field, read as little-endian.
    /// <para>64 位 DTB 加载地址字段，按小端序读取。</para>
    /// </summary>
    /// <value>
    /// Stored and re-serialized verbatim; the parser assigns it from header bytes without interpretation.
    /// <para>原样保存并重新序列化；解析器直接从头部字节赋值，不做解释。</para>
    /// </value>
    public ulong DtbAddress { get; set; }

    /// <summary>
    /// Declared byte size of the version 4 ramdisk table region.
    /// <para>版本 4 RAMDisk 表区域的声明字节大小。</para>
    /// </summary>
    /// <value>
    /// Read only for header version 4 and rejected above 64 MiB; it must cover entry count times entry stride, and stays <c>0</c> for version 3.
    /// <para>仅在头部版本 4 时读取，超过 64 MiB 会被拒绝；必须不小于条目数乘以条目步长，版本 3 时保持为 <c>0</c>。</para>
    /// </value>
    public uint VendorRamdiskTableSize { get; set; }

    /// <summary>
    /// Declared number of entries in the version 4 ramdisk table.
    /// <para>版本 4 RAMDisk 表的声明条目数。</para>
    /// </summary>
    /// <value>
    /// Bounded by 65,536; <c>0</c> names an empty table and the field stays <c>0</c> for version 3.
    /// <para>上限为 65,536；<c>0</c> 表示空表，版本 3 时该字段保持为 <c>0</c>。</para>
    /// </value>
    public uint VendorRamdiskTableEntryNum { get; set; }

    /// <summary>
    /// Declared stride in bytes of each ramdisk table entry record.
    /// <para>每个 RAMDisk 表条目记录的声明字节步长。</para>
    /// </summary>
    /// <value>
    /// The parser accepts 108 through 65,536 bytes; entries shorter than 108 bytes cannot hold the fixed record layout. Version 3 leaves the field <c>0</c>.
    /// <para>解析器接受 108 至 65,536 字节；短于 108 字节的条目无法容纳固定记录布局。版本 3 时该字段为 <c>0</c>。</para>
    /// </value>
    public uint VendorRamdiskTableEntrySize { get; set; }

    /// <summary>
    /// Byte offset of the ramdisk table region measured from the start of the image.
    /// <para>RAMDisk 表区域相对于镜像起始处的字节偏移。</para>
    /// </summary>
    /// <value>
    /// Not read from disk: the parser computes it by page-aligning after the DTB and rejects it beyond <see cref="uint.MaxValue"/>; <c>0</c> for version 3.
    /// <para>并非从磁盘读取：解析器在 DTB 之后按页对齐计算得出，超出 <see cref="uint.MaxValue"/> 会被拒绝；版本 3 时为 <c>0</c>。</para>
    /// </value>
    public uint VendorRamdiskTableOffset { get; set; }

    /// <summary>
    /// Declared byte size of the bootconfig payload.
    /// <para>bootconfig 载荷的声明字节大小。</para>
    /// </summary>
    /// <value>
    /// Version 4 only; the payload starts at the page boundary after the ramdisk table and <c>0</c> means it is absent.
    /// <para>仅版本 4 有效；载荷位于 RAMDisk 表之后按页对齐处，<c>0</c> 表示不存在。</para>
    /// </value>
    public uint VendorBootconfigSize { get; set; }

    /// <summary>
    /// Compression format detected for the main vendor ramdisk.
    /// <para>为主 Vendor RAMDisk 检测出的压缩格式。</para>
    /// </summary>
    /// <value>
    /// Set by magic-byte analysis of the ramdisk's first bytes during a parse; stays <see cref="CompressionType.None"/> when the ramdisk size is <c>0</c> or nothing has been parsed.
    /// <para>由解析时对 RAMDisk 起始字节的魔数分析得出；RAMDisk 大小为 <c>0</c> 或尚未解析时保持 <see cref="CompressionType.None"/>。</para>
    /// </value>
    public CompressionType VendorRamdiskCompression { get; set; }

    /// <summary>
    /// Converts to a string format, outputting header information.
    /// <para>转换为字符串格式，输出头部信息。</para>
    /// </summary>
    /// <returns>
    /// One line per field, addresses rendered in hexadecimal; table and bootconfig fields appear only when <see cref="HeaderVersion"/> is at
    /// least <c>4</c>, and a notice line is printed instead for lower versions.
    /// <para>每字段一行，地址以十六进制呈现；仅当 <see cref="HeaderVersion"/> 不小于 <c>4</c> 时才输出表与 bootconfig 字段，低版本则输出一行提示信息。</para>
    /// </returns>
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