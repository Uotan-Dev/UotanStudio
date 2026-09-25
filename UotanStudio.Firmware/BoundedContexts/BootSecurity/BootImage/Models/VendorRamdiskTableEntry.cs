namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

/// <summary>
/// One record of the version 4 vendor boot ramdisk table: a named sub-ramdisk located inside the main vendor ramdisk.
/// <para>版本 4 Vendor Boot RAMDisk 表中的一条记录：定位在主 Vendor RAMDisk 内部的具牌子 RAMDisk。</para>
/// </summary>
/// <remarks>
/// Plain mutable data transfer object; range validation against the vendor ramdisk happens in the parser and packer, not here.
/// <para>普通可变数据传输对象；针对 Vendor RAMDisk 的范围校验由解析器和打包器完成，而非本类型。</para>
/// </remarks>
public class VendorRamdiskTableEntry
{
    /// <summary>
    /// Declared byte length of this sub-ramdisk, read as a little-endian 32-bit field.
    /// <para>该子 RAMDisk 的声明字节长度，按小端 32 位字段读取。</para>
    /// </summary>
    /// <value>
    /// <c>0</c> names an empty region; the range <see cref="RamdiskOffset"/> plus <see cref="RamdiskSize"/> must stay inside the vendor ramdisk.
    /// <para><c>0</c> 表示空区域；<see cref="RamdiskOffset"/> 加 <see cref="RamdiskSize"/> 必须落在 Vendor RAMDisk 范围内。</para>
    /// </value>
    public uint RamdiskSize { get; set; }

    /// <summary>
    /// Byte offset of this sub-ramdisk relative to the start of the main vendor ramdisk, not the start of the image.
    /// <para>该子 RAMDisk 相对于主 Vendor RAMDisk 起始处的字节偏移，而非相对于镜像起始处。</para>
    /// </summary>
    /// <value>
    /// Little-endian 32-bit field; zero-initialized before a parse. Offsets pushing the entry outside the ramdisk are rejected by the parser.
    /// <para>小端 32 位字段；解析前为零初始化。使条目超出 RAMDisk 的偏移会被解析器拒绝。</para>
    /// </value>
    public uint RamdiskOffset { get; set; }

    /// <summary>
    /// Raw 32-bit ramdisk type code recorded in the table entry.
    /// <para>表条目中记录的原始 32 位 RAMDisk 类型代码。</para>
    /// </summary>
    /// <value>
    /// Interpretation is vendor-defined; the parser stores the little-endian value verbatim and the packer re-emits it unchanged.
    /// <para>解释方式由厂商定义；解析器按小端序原样保存，打包器原样写回。</para>
    /// </value>
    public uint RamdiskType { get; set; }

    /// <summary>
    /// Display name stored in the fixed 32-byte ASCII name field of the entry.
    /// <para>存储在条目固定 32 字节 ASCII 名称字段中的显示名称。</para>
    /// </summary>
    /// <value>
    /// Trailing NUL characters are trimmed on parse; <see cref="string.Empty"/> when the field is blank or before any parse.
    /// <para>解析时会去除尾部 NUL 字符；字段为空或尚未解析时为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string RamdiskName { get; set; } = string.Empty;

    /// <summary>
    /// Sixteen little-endian 32-bit board identifier codes that follow the name field in the entry record.
    /// <para>条目记录中位于名称字段之后的 16 个小端 32 位板级标识码。</para>
    /// </summary>
    /// <value>
    /// Always exactly 16 elements; initialized to all zeros and repopulated in place by the parser. Packing serializes all 16 slots.
    /// <para>始终恰好 16 个元素；初始化为全零并由解析器就地重新填充。打包时会序列化全部 16 个槽位。</para>
    /// </value>
    public uint[] RamdiskBoardId { get; set; } = new uint[16];
}