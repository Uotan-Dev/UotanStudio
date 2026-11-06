namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Boot stage behind a SpacemiT fastboot gadget: the BootROM download mode that still needs FSBL and U-Boot
/// staged into RAM, or the U-Boot gadget that can already write partitions.
/// <para>进迭时空 fastboot gadget 背后的启动阶段：仍需把 FSBL 与 U-Boot 暂存进内存的 BootROM 下载模式，
/// 或已经可以直接写分区的 U-Boot gadget。</para>
/// </summary>
public enum SpacemiTBootStage
{
    /// <summary>
    /// Stage not established, or the device is not a SpacemiT fastboot gadget.
    /// <para>阶段未确定，或该设备不是进迭时空 fastboot gadget。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// BootROM download mode (DFU): answers <c>getvar:version-brom</c>, reports the <c>DFU-DEVICE</c> serial and
    /// needs <c>stage</c> plus <c>continue</c> before any partition write.
    /// <para>BootROM 下载模式（DFU）：应答 <c>getvar:version-brom</c>，上报 <c>DFU-DEVICE</c> 序列号，并且在任何
    /// 分区写入之前需要 <c>stage</c> 与 <c>continue</c>。</para>
    /// </summary>
    BromFdl,

    /// <summary>
    /// U-Boot fastboot gadget: no <c>version-brom</c> answer, reports the board serial, and accepts partition
    /// writes directly. Its <c>continue</c> keeps the same USB node instead of re-enumerating.
    /// <para>U-Boot fastboot gadget：不应答 <c>version-brom</c>，上报板卡序列号，并直接接受分区写入；它的
    /// <c>continue</c> 保持同一 USB 节点而不重新枚举。</para>
    /// </summary>
    Uboot
}