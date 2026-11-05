using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Flash;

/// <summary>
/// A target mode an EDL device can be rebooted into.
/// <para>EDL 设备可重启进入的目标模式。</para>
/// </summary>
public enum EdlRebootTarget
{
    /// <summary>Reboot into the installed Android system.
    /// <para>重启进入已安装的 Android 系统。</summary>
    System,

    /// <summary>Reboot into recovery.
    /// <para>重启进入 recovery。</summary>
    Recovery,

    /// <summary>Reboot into the userspace fastboot daemon.
    /// <para>重启进入用户态 fastboot 守护进程。</summary>
    Fastbootd,

    /// <summary>Reset back into EDL emergency-download mode.
    /// <para>复位回 EDL 紧急下载模式。</summary>
    Edl,

    /// <summary>Power the device off.
    /// <para>关闭设备电源。</summary>
    PowerOff,
}

/// <summary>
/// The complete sequence one reboot target needs: which power value to send, and whether a bootloader
/// control block must be written to the misc partition first so the bootloader picks a non-system mode.
/// <para>一个重启目标所需的完整序列：发送哪个 power 值，以及是否必须先把引导控制块写入 misc 分区，
/// 好让 bootloader 选择非系统模式。</para>
/// </summary>
/// <param name="Target">The requested target mode. <para>请求的目标模式。</para></param>
/// <param name="PowerValue">The Firehose power value to send.
/// <para>要发送的 Firehose power 值。</para></param>
/// <param name="BootloaderControlImage">The misc image that selects this mode, or <c>null</c> when a plain reset suffices.
/// <para>用于选择该模式的 misc 镜像；普通复位足以达成时为空。</para></param>
public sealed record EdlModeSwitchPlan(
    EdlRebootTarget Target,
    string PowerValue,
    string? BootloaderControlImage);

/// <summary>
/// Decides what each reboot target requires, matching the Qualcomm lineage: system, power-off and EDL are
/// reachable with a bare power command, while recovery and fastbootd are only reachable by writing a
/// bootloader control block the bootloader reads on the next reset.
/// <para>决定每个重启目标需要什么，与高通谱系一致：系统、关机与 EDL 只需一条 power 命令即可达成，
/// 而 recovery 与 fastbootd 只能通过写入一个引导控制块、由 bootloader 在下次复位时读取来达成。</para>
/// </summary>
public static class EdlModeSwitchPlanner
{
    /// <summary>The misc image name that asks the bootloader to boot recovery.
    /// <para>请求 bootloader 启动 recovery 的 misc 镜像名。</para></summary>
    public const string RecoveryBootImage = "misc_torecovery.img";

    /// <summary>The misc image name that asks the bootloader to boot the fastboot daemon.
    /// <para>请求 bootloader 启动 fastboot 守护进程的 misc 镜像名。</para></summary>
    public const string FastbootdBootImage = "misc_tofastbootd.img";

    /// <summary>
    /// Builds the plan for one target mode. Storage type does not change the sequence, because the bootloader
    /// control block write is addressed by partition name rather than by a hard-coded logical unit.
    /// <para>为一个目标模式构建计划。存储类型不影响该序列，因为引导控制块写入按分区名定位，
    /// 而不是按硬编码的逻辑单元。</para>
    /// </summary>
    /// <param name="target">The requested target mode. <para>请求的目标模式。</para></param>
    /// <returns>The plan describing the power value and any boot image needed.
    /// <para>描述 power 值以及所需引导镜像的计划。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not a defined target.
    /// <para><paramref name="target"/> 不是已定义的目标。</para></exception>
    public static EdlModeSwitchPlan Plan(EdlRebootTarget target) => target switch
    {
        EdlRebootTarget.System => new EdlModeSwitchPlan(target, "reset", null),
        EdlRebootTarget.PowerOff => new EdlModeSwitchPlan(target, "off", null),
        EdlRebootTarget.Edl => new EdlModeSwitchPlan(target, "reset_to_edl", null),
        EdlRebootTarget.Recovery => new EdlModeSwitchPlan(target, "reset", RecoveryBootImage),
        EdlRebootTarget.Fastbootd => new EdlModeSwitchPlan(target, "reset", FastbootdBootImage),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unexpected EDL reboot target."),
    };

    /// <summary>
    /// Maps a plan onto the protocol's own reboot option, so the existing power implementation keeps owning
    /// the boot-drive preparation and the session retirement.
    /// <para>把计划映射到协议自身的重启选项，使已有的 power 实现继续负责启动驱动器准备与会话退役。</para>
    /// </summary>
    /// <param name="target">The requested target mode. <para>请求的目标模式。</para></param>
    /// <returns>The matching reboot option. <para>对应的重启选项。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The target cannot be expressed as a plain reboot option; recovery and fastbootd need the misc write.
    /// <para>该目标无法表达为普通重启选项；recovery 与 fastbootd 需要 misc 写入。</para>
    /// </exception>
    public static RebootOption ToRebootOption(EdlRebootTarget target) => target switch
    {
        EdlRebootTarget.System => RebootOption.Reset,
        EdlRebootTarget.PowerOff => RebootOption.PowerOff,
        EdlRebootTarget.Edl => RebootOption.ResetToEdl,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target,
            "This target needs a bootloader control block, not a plain power command."),
    };
}