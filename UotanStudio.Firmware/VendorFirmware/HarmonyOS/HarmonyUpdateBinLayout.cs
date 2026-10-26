namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// Component-table generation of a HarmonyOS <c>update.bin</c> package. The generation fixes both the record
/// width and the width of the component-name field.
/// <para>HarmonyOS <c>update.bin</c> 包的组件表代际。代际同时决定记录宽度与组件名字段宽度。</para>
/// </summary>
internal enum HarmonyUpdateBinLayout
{
    /// <summary>Legacy L1 table: a 16-byte name field and 71-byte records, marked by header type <c>0x11</c>.
    /// <para>旧式 L1 表：16 字节名称字段与 71 字节记录，头部类型标记为 <c>0x11</c>。</para></summary>
    L1,

    /// <summary>L2 table: a 32-byte name field and 87-byte records, marked by header type <c>0x01</c>.
    /// <para>L2 表：32 字节名称字段与 87 字节记录，头部类型标记为 <c>0x01</c>。</para></summary>
    L2
}