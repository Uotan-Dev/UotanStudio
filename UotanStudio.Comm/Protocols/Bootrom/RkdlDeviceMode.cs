namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// The stage a Rockchip boot ROM reports through its USB device descriptor.
/// <para>瑞芯微 boot ROM 通过 USB 设备描述符报告的阶段。</para>
/// </summary>
public enum RkdlDeviceMode
{
    /// <summary>
    /// The stage is not known because the device reported no <c>bcdUSB</c> value and no product-id table entry can
    /// stand in for it.
    /// <para>阶段未知：设备未报告 <c>bcdUSB</c> 取值，且产品 ID 表无法代为判定。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Mask ROM stage: <c>bcdUSB</c> bit 0 is clear. Only this stage accepts the <c>0x0471</c>/<c>0x0472</c> loader
    /// download vendor requests.
    /// <para>Mask ROM 阶段：<c>bcdUSB</c> 的 bit 0 为 0。只有该阶段接受 <c>0x0471</c>/<c>0x0472</c> loader
    /// 下载 vendor 请求。</para>
    /// </summary>
    Maskrom = 1,

    /// <summary>
    /// Loader stage: <c>bcdUSB</c> bit 0 is set. Bulk CBW commands such as chip-info and storage reads are only
    /// valid here.
    /// <para>loader 阶段：<c>bcdUSB</c> 的 bit 0 为 1。芯片信息读取、存储读取等 CBW 批量命令只在该阶段有效。</para>
    /// </summary>
    Loader = 2
}