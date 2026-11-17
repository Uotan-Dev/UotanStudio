namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Stage a Tegra endpoint is in, which decides what the host may do with it: RCM download, fastboot, or
/// Android with adb.
/// <para>Tegra 端点所处阶段，决定主机可以对它做什么：RCM 下载、fastboot 或带 adb 的 Android。</para>
/// </summary>
/// <remarks>
/// The kernel's reboot handler writes one of three <c>PMC_SCRATCH0</c> bits: <c>bootloader</c> selects
/// <c>BOOTLOADER_MODE</c>, <c>recovery</c> selects <c>RECOVERY_MODE</c> and <c>forced-recovery</c> selects
/// <c>FORCED_RECOVERY_MODE</c>. The boot ROM, not the kernel, decides what those bits mean after the reset, and it
/// treats the forced-recovery bit as a request to stop in RCM instead of booting an image. A running Android can
/// therefore be steered into RCM with a plain <c>reboot forced-recovery</c>; measured on a Xiaomi 3 (pisces,
/// Tegra 4), the APX endpoint appears about six seconds later as <c>0955:7835</c>.
/// <para>内核重启处理会写 <c>PMC_SCRATCH0</c> 的三个位之一：<c>bootloader</c> 置 <c>BOOTLOADER_MODE</c>，
/// <c>recovery</c> 置 <c>RECOVERY_MODE</c>，<c>forced-recovery</c> 置 <c>FORCED_RECOVERY_MODE</c>。这些位在复位后
/// 由 boot ROM 而不是内核解释，而 boot ROM 把强制恢复位当作"停在 RCM、不启动镜像"的请求。因此运行中的 Android
/// 可以用一条 <c>reboot forced-recovery</c> 进入 RCM；在小米 3（pisces，Tegra 4）上实测，约六秒后以
/// <c>0955:7835</c> 出现 APX 端点。</para>
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