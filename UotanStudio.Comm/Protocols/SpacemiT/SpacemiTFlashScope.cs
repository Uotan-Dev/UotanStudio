namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Which part of a SpacemiT package script a run executes: the boot-chain transfer that lifts a board out of the
/// BootROM download mode, the storage writes that follow it, or both.
/// <para>一次运行执行 SpacemiT 包脚本的哪一部分：把板卡带离 BootROM 下载模式的引导链传输、其后的存储写入，或两者。</para>
/// </summary>
public enum SpacemiTFlashScope
{
    /// <summary>
    /// The whole script, staging included — the vendor flasher's own flow.
    /// <para>整个脚本（含暂存），即厂商烧录器自身的流程。</para>
    /// </summary>
    Full = 0,

    /// <summary>
    /// Only the actions up to and including the last <c>continue</c>: FSBL and U-Boot are handed over to the board, and
    /// nothing is written to storage. A script without <c>continue</c> has no boot-chain section.
    /// <para>只执行到最后一个 <c>continue</c>（含）为止的动作：把 FSBL 与 U-Boot 交给板卡，不写入任何存储。脚本中没有
    /// <c>continue</c> 时没有引导链段落。</para>
    /// </summary>
    BootChain,

    /// <summary>
    /// Only the actions after the last <c>continue</c>: partition tables and images are written to a board that already
    /// runs the U-Boot gadget, so the staging actions are never attempted.
    /// <para>只执行最后一个 <c>continue</c> 之后的动作：向已运行 U-Boot gadget 的板卡写入分区表与镜像，因此绝不尝试
    /// 暂存动作。</para>
    /// </summary>
    Storage
}