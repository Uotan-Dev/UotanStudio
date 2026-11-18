using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// NVIDIA Tegra section: the reboot target matrix, the APX/RCM recognition readout and the RCM download chain.
/// <para>NVIDIA Tegra 分类：重启目标矩阵、APX/RCM 识别读数与 RCM 下载链。</para>
/// </summary>
/// <remarks>
/// Tegra owns a section of its own because none of its actions fit a shared button. A Tegra boot ROM answers only
/// RCM, so the shared storage and erase actions cannot run against it; the reboot targets are a fixed three-value
/// matrix the kernel implements by programming PMC_SCRATCH0, and one of those three targets is the software entry into
/// RCM that the shared flash action cannot express.
/// <para>Tegra 拥有独立分类，因为它的动作没有一个能塞进通用按钮：Tegra boot ROM 只应答 RCM，因此通用存储与擦除动作
/// 无法作用于它；重启目标是内核通过写 PMC_SCRATCH0 实现的固定三值矩阵，而其中"强制恢复"正是通用烧录动作无法表达的
/// RCM 软件入口。</para>
/// </remarks>
public sealed partial class FlashViewModel
{
    /// <summary>Reboot target that returns the device to its bootloader (fastboot).
    /// <para>把设备带回 bootloader（fastboot）的重启目标。</para></summary>
    internal const string TegraBootloaderTarget = "bootloader";

    /// <summary>Reboot target that boots the recovery image.
    /// <para>启动 recovery 镜像的重启目标。</para></summary>
    internal const string TegraRecoveryTarget = "recovery";

    /// <summary>Reboot target that forces recovery even when a system update is pending.
    /// <para>即使有待处理的系统更新也强制进入 recovery 的重启目标。</para></summary>
    internal const string TegraForcedRecoveryTarget = "forced-recovery";

    /// <summary>The three reboot targets the Tegra kernel implements, in the order the section presents them.
    /// <para>Tegra 内核实现的重启目标，按分类呈现的顺序排列。</para></summary>
    internal static IReadOnlyList<string> TegraRebootTargets { get; } =
        [TegraBootloaderTarget, TegraRecoveryTarget, TegraForcedRecoveryTarget];

    /// <summary>
    /// The only reboot target that stops the device in RCM: the boot ROM reads the forced-recovery bit as a request
    /// not to boot an image, so this is the software entry into the download mode.
    /// <para>唯一让设备停在 RCM 的重启目标：boot ROM 把强制恢复位读作"不启动镜像"的请求，因此这就是下载模式的软件入口。</para>
    /// </summary>
    internal const string TegraRcmEntryTarget = TegraForcedRecoveryTarget;

    /// <summary>Status line of the NVIDIA section. <para>NVIDIA 分类的状态行。</para></summary>
    [ObservableProperty]
    private string _tegraStatusText = string.Empty;

    /// <summary>
    /// Explains what the reboot targets do and which one reaches RCM, so the page does not present three targets as
    /// interchangeable when one of them is the download-mode entry.
    /// <para>说明各重启目标的作用以及哪一个能到达 RCM，使页面不会把三个目标呈现成可以互换 —— 其中一个正是下载模式入口。</para>
    /// </summary>
    [ObservableProperty]
    private string _tegraRcmHint = string.Empty;

    /// <summary>Whether the selected endpoint answered as a Tegra boot ROM (APX/RCM).
    /// <para>所选端点是否以 Tegra boot ROM（APX/RCM）应答。</para></summary>
    [ObservableProperty]
    private bool _isTegraRcmEndpoint;

    /// <summary>Whether the selected endpoint answered as the Tegra fastboot stage.
    /// <para>所选端点是否以 Tegra fastboot 阶段应答。</para></summary>
    [ObservableProperty]
    private bool _isTegraFastbootStage;

    /// <summary>
    /// Whether the selected endpoint can answer a reboot request, which needs a live ADB shell rather than a
    /// download port.
    /// <para>所选端点能否应答重启请求；这需要一个活动的 ADB shell，而不是下载端口。</para>
    /// </summary>
    [ObservableProperty]
    private bool _canRebootTegraTarget;

    /// <summary>
    /// NVIDIA section: the pane answers only on a Tegra endpoint, and each reboot target is an independent action
    /// that needs a live shell. The header stays present for every endpoint because a disabled entry is how the page
    /// says a device does not offer it.
    /// <para>NVIDIA 分类：面板只在 Tegra 端点上应答，每个重启目标都是需要活动 shell 的独立动作。表头对任何端点都保持呈现，
    /// 因为页面正是用置灰来表达设备不提供该动作。</para>
    /// </summary>
    private void RegisterTegraControlGates()
    {
        RegisterGate("NvidiaExpander", FlashControlGate.SectionHeader("NvidiaExpander"));
        RegisterGate("NvidiaPane", FlashControlGate.Section("NvidiaPane", FlashDeviceMode.Tegra, busyDisabled: true));
        RegisterGate("NvidiaRebootBootloaderButton", FlashControlGate.Tegra("NvidiaRebootBootloaderButton"));
        RegisterGate("NvidiaRebootRecoveryButton", FlashControlGate.Tegra("NvidiaRebootRecoveryButton"));
        RegisterGate("NvidiaRebootForcedRecoveryButton", FlashControlGate.Tegra("NvidiaRebootForcedRecoveryButton"));
        RegisterGate("NvidiaEnterRcmButton", FlashControlGate.Tegra("NvidiaEnterRcmButton"));
    }

    /// <summary>
    /// Publishes the NVIDIA readout for the selected endpoint: which Tegra stage the endpoint answered as, what that
    /// stage allows, and how the forced-recovery target turns an Android endpoint into an RCM port.
    /// <para>为所选端点发布 NVIDIA 读数：端点以哪个 Tegra 阶段应答、该阶段允许什么，以及"软件侧没有进入 RCM 的路径"
    /// 这一固定事实。</para>
    /// </summary>
    internal void RefreshTegraDeviceStatus()
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Tegra } device)
        {
            IsTegraRcmEndpoint = false;
            IsTegraFastbootStage = false;
            CanRebootTegraTarget = false;
            TegraStatusText = string.Empty;
            TegraRcmHint = string.Empty;
            RefreshDeviceUiGate();
            return;
        }

        IsTegraRcmEndpoint = true;
        IsTegraFastbootStage = false;
        CanRebootTegraTarget = false;
        TegraStatusText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            _localizer.GetString("Flash_TegraRcmDetected"),
            device.Serial);
        TegraRcmHint = _localizer.GetString("Flash_TegraRcmHint");
        RefreshDeviceUiGate();
    }

    /// <summary>
    /// Reports whether a reboot target is one the Tegra kernel implements; the section never sends a target the
    /// kernel would silently fold into a plain reboot.
    /// <para>报告某个重启目标是否为 Tegra 内核实现的目标；本分类绝不发送内核会静默折叠成普通重启的目标。</para>
    /// </summary>
    /// <param name="target">The reboot target to test. <para>待检测的重启目标。</para></param>
    /// <returns><see langword="true"/> for the three implemented targets.
    /// <para>三个已实现目标之一时为 <see langword="true"/>。</para></returns>
    internal static bool IsTegraRebootTarget(string? target) =>
        target is not null && TegraRebootTargets.Contains(target, StringComparer.Ordinal);

    /// <summary>Reboots the selected Tegra device to its bootloader. <para>把所选 Tegra 设备重启到其 bootloader。</para></summary>
    internal void OnNvidiaRebootBootloaderClick(object? sender, RoutedEventArgs e) =>
        RunTegraReboot(TegraBootloaderTarget);

    /// <summary>Reboots the selected Tegra device to recovery. <para>把所选 Tegra 设备重启到 recovery。</para></summary>
    internal void OnNvidiaRebootRecoveryClick(object? sender, RoutedEventArgs e) =>
        RunTegraReboot(TegraRecoveryTarget);

    /// <summary>Reboots the selected Tegra device with the forced-recovery target.
    /// <para>用 forced-recovery 目标重启所选 Tegra 设备。</para></summary>
    internal void OnNvidiaRebootForcedRecoveryClick(object? sender, RoutedEventArgs e) =>
        RunTegraReboot(TegraForcedRecoveryTarget);

    /// <summary>
    /// Steers a running Android endpoint into RCM, which is what makes the download chain reachable at all: the
    /// device leaves adb about six seconds later and enumerates as an NVIDIA APX port.
    /// <para>把运行中的 Android 端点引导进 RCM，这正是让下载链可达的前提：设备约六秒后离开 adb，并枚举为 NVIDIA APX 端口。</para>
    /// </summary>
    internal void OnNvidiaEnterRcmClick(object? sender, RoutedEventArgs e) =>
        RunTegraReboot(TegraRcmEntryTarget);

    /// <summary>
    /// Sends one reboot target and reports the outcome; the device drops the ADB link as soon as the kernel takes the
    /// request, so a transport failure after the command was accepted is not reported as an error.
    /// <para>发送一个重启目标并报告结果；内核接受请求后设备会立刻断开 ADB 链路，因此命令被接受后的传输失败不会当作错误上报。</para>
    /// </summary>
    /// <param name="target">The reboot target. <para>重启目标。</para></param>
    private void RunTegraReboot(string target)
    {
        if (_busy)
        {
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Tegra } and not { Protocol: DeviceProtocolKind.Adb })
        {
            AppendLog(_localizer.GetString("Flash_TegraRebootNeedsAdb"));
            return;
        }

        _ = RunTegraRebootAsync(target);
    }

    private async Task RunTegraRebootAsync(string target)
    {
        var language = _localizer;
        try
        {
            if (SelectedDevice is not { } device)
            {
                return;
            }

            AppendLog(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                language.GetString("Flash_TegraRebootSent"),
                target));
            await App.DeviceMonitor.RebootTegraTargetAsync(device, target).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException
            or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            AppendLog(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                language.GetString("Flash_TegraRebootFailed"),
                target,
                error.Message));
        }
    }
}