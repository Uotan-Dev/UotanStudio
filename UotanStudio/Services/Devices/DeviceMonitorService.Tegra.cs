using UotanStudio.Comm.Protocols.Tegra;

namespace UotanStudio.Services.Devices;

/// <summary>
/// NVIDIA Tegra device operations the flash page cannot express through a shared action.
/// <para>烧录页面无法用通用动作表达的 NVIDIA Tegra 设备操作。</para>
/// </summary>
/// <remarks>
/// A Tegra reboot target is not a storage write, so it needs no confirmation phrase; it does need a live ADB shell,
/// because every target is reached by programming <c>PMC_SCRATCH0</c> from a running Android. The three targets that
/// exist do genuinely different things: <c>bootloader</c> stops in fastboot, <c>recovery</c> boots the recovery image
/// and <c>forced-recovery</c> stops in RCM, which the boot ROM honours as a request not to boot an image at all.
/// <para>Tegra 重启目标不是存储写入，因此不需要确认短语；但它需要一个活动的 ADB shell，因为所有目标都由运行中的
/// Android 写 <c>PMC_SCRATCH0</c> 达成。三个目标做的事情确实不同：<c>bootloader</c> 停在 fastboot，<c>recovery</c>
/// 启动 recovery 镜像，而 <c>forced-recovery</c> 停在 RCM —— boot ROM 把它当作"不启动镜像"的请求。</para>
/// </remarks>
public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Reboots a Tegra device into one of the three targets its kernel implements.
    /// <para>把 Tegra 设备重启到其内核实现的三个目标之一。</para>
    /// </summary>
    /// <param name="device">The ADB target to reboot; it must be a live Android, not a download port.
    /// <para>要重启的 ADB 目标；它必须是运行中的 Android，而不是下载端口。</para></param>
    /// <param name="target">The reboot target, for example <c>bootloader</c> or <c>forced-recovery</c>.
    /// <para>重启目标，例如 <c>bootloader</c> 或 <c>forced-recovery</c>。</para></param>
    /// <param name="cancellationToken">Cancels the request. <para>取消本次请求。</para></param>
    /// <returns>The output the device shell reported; a reboot normally returns before the device drops the link.
    /// <para>设备 shell 上报的输出；设备断开链路之前重启通常就已返回。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is empty or not one of the three targets.
    /// <para><paramref name="target"/> 为空，或不是三个目标之一。</para></exception>
    /// <exception cref="InvalidOperationException">The target is not an ADB endpoint.
    /// <para>目标不是 ADB 端点。</para></exception>
    internal async Task<string> RebootTegraTargetAsync(DetectedDevice device, string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!IsTegraRebootTarget(target))
        {
            throw new ArgumentException(
                $"'{target}' is not a reboot target the Tegra kernel implements.",
                nameof(target));
        }

        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            throw new InvalidOperationException(
                $"A Tegra reboot target needs a live Android shell, but the selected endpoint speaks {device.Protocol}.");
        }

        using var scope = AcquireSharedAdbSession(device, cancellationToken);
        var shell = scope.Session.CreateShellClient($"reboot {target}");
        var result = await shell.ExecuteAsync(TegraRebootTimeoutMs, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(result.Stdout).TrimEnd('\n');
    }

    /// <summary>
    /// Reports whether a target is one the Tegra kernel implements; the three targets are the only ones that program
    /// <c>PMC_SCRATCH0</c>, and every other string the kernel folds back into a plain reboot.
    /// <para>报告某个目标是否为 Tegra 内核实现的目标；只有这三个目标会写 <c>PMC_SCRATCH0</c>，其它任何字符串都会被内核折叠成
    /// 普通重启。</para>
    /// </summary>
    /// <param name="target">The reboot target to test. <para>待检测的重启目标。</para></param>
    /// <returns><see langword="true"/> for <c>bootloader</c>, <c>recovery</c> and <c>forced-recovery</c>.
    /// <para><c>bootloader</c>、<c>recovery</c> 与 <c>forced-recovery</c> 为 <see langword="true"/>。</para></returns>
    internal static bool IsTegraRebootTarget(string? target) => target is
        "bootloader" or "recovery" or "forced-recovery";

    /// <summary>
    /// Maps a reboot target to the Tegra stage the device lands in, so the page can name the outcome before the
    /// reboot happens instead of describing every target the same way.
    /// <para>把重启目标映射为设备最终所处的 Tegra 阶段，使页面能在重启发生前就说明结果，而不是把所有目标描述成同一回事。</para>
    /// </summary>
    /// <param name="target">The reboot target; an unknown target maps to <see cref="TegraDeviceMode.Android"/>.
    /// <para>重启目标；未知目标映射为 <see cref="TegraDeviceMode.Android"/>。</para></param>
    /// <returns>The stage the boot ROM selects for that target. <para>boot ROM 为该目标选择的阶段。</para></returns>
    internal static TegraDeviceMode TegraStageForTarget(string? target) => target switch
    {
        "bootloader" => TegraDeviceMode.Fastboot,
        "forced-recovery" => TegraDeviceMode.ApxRcm,
        _ => TegraDeviceMode.Android,
    };

    /// <summary>
    /// Reports whether a target stops the device in RCM, which is the only target that turns an Android endpoint into
    /// a download port a host can speak RCM to.
    /// <para>报告某个目标是否让设备停在 RCM —— 只有该目标能把 Android 端点变成主机可以用 RCM 对话的下载端口。</para>
    /// </summary>
    /// <param name="target">The reboot target. <para>重启目标。</para></param>
    /// <returns><see langword="true"/> for <c>forced-recovery</c> only.
    /// <para>仅 <c>forced-recovery</c> 为 <see langword="true"/>。</para></returns>
    internal static bool TargetEntersRcm(string? target) =>
        string.Equals(target, "forced-recovery", StringComparison.Ordinal);

    private const int TegraRebootTimeoutMs = 15_000;
}