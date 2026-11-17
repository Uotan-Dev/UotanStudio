namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Stage a Tegra endpoint is in, which decides what the host may do with it: RCM download, fastboot, or
/// Android with adb.
/// <para>Tegra 端点所处阶段，决定主机可以对它做什么：RCM 下载、fastboot 或带 adb 的 Android。</para>
/// </summary>
/// <remarks>
/// RCM (APX) is a hardware strapping state with no software entry point on Tegra: the kernel's reboot handler
/// only honours <c>bootloader</c>, <c>recovery</c> and <c>forced-recovery</c> through <c>PMC_SCRATCH0</c>, so a
/// host can never reboot an Android device into RCM. Treat this value as an observation of the USB identity,
/// never as a command outcome.
/// <para>RCM（APX）是硬件 strap 状态，Tegra 上没有软件入口：内核重启处理只通过 <c>PMC_SCRATCH0</c> 响应
/// <c>bootloader</c>、<c>recovery</c> 与 <c>forced-recovery</c>，因此主机无法把 Android 设备重启进 RCM。本值
/// 只表示对 USB 身份的观测结果，绝不表示命令执行结果。</para>
/// </remarks>
public enum TegraDeviceMode
{
    /// <summary>
    /// The device is in RCM (also called APX) and accepts RCM messages: NVIDIA vendor ID with an RCM device ID
    /// in the product ID's low byte.
    /// <para>设备处于 RCM（亦称 APX）并接受 RCM 消息：NVIDIA 厂商 ID，且产品 ID 低字节为 RCM 设备 ID。</para>
    /// </summary>
    ApxRcm = 0,

    /// <summary>
    /// The device is in its bootloader and answers fastboot.
    /// <para>设备处于 bootloader 并应答 fastboot。</para>
    /// </summary>
    Fastboot = 1,

    /// <summary>
    /// The device has booted Android and answers adb.
    /// <para>设备已启动到 Android 并应答 adb。</para>
    /// </summary>
    Android = 2,
}