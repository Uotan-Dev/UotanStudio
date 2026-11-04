using Avalonia.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Device awareness of the flash page: every enable decision goes through <see cref="SetControlEnabled"/> and the
/// gated controls stay unusable until a device is selected and offers them.
/// <para>烧录页面的设备感知：所有启用决定都经 <see cref="SetControlEnabled"/> 作出，受控控件在选中设备并提供该
/// 功能之前保持不可用。</para>
/// </summary>
/// <remarks>
/// The page state stays authoritative for what it wants enabled; <see cref="RefreshDeviceUiGate"/> re-applies the
/// device restriction on top of that intent whenever the selected device changes, so a device swap neither leaves a
/// hidden control enabled nor forgets what the page had enabled for the previous one.
/// <para>页面状态仍然是“想要启用什么”的依据；选中设备变化时，<see cref="RefreshDeviceUiGate"/> 在该意图之上重新
/// 施加设备限制，因此换设备既不会留下被隐藏的可用控件，也不会丢掉页面对上一台设备的启用状态。</para>
/// </remarks>
public sealed partial class FlashViewModel
{
    /// <summary>
    /// Enable intent the page last asked for, per control name; the device restriction is applied on top of it.
    /// <para>页面最近一次请求的启用意图（按控件名）；设备限制叠加在其之上。</para>
    /// </summary>
    private readonly Dictionary<string, bool> _controlIntent = new(StringComparer.Ordinal);

    /// <summary>
    /// The mode whose controls the page offers, derived from the selected device's protocol.
    /// <para>页面据此提供控件的模式，取自所选设备的协议。</para>
    /// </summary>
    internal FlashDeviceMode DeviceMode => FlashDeviceUiPolicy.ModeOf(SelectedDevice?.Protocol);

    /// <summary>
    /// The restricted protocol kind of the selected device; ADB and fastboot narrow the page to the shared
    /// flash action, and HDC offers no device operation at all.
    /// <para>所选设备的受限协议种类；ADB 与 fastboot 把页面收窄为通用烧录动作，HDC 则不提供任何设备操作。</para>
    /// </summary>
    internal FlashDeviceKind DeviceKind => FlashDeviceUiPolicy.KindOf(SelectedDevice?.Protocol);

    /// <summary>
    /// Whether the selected fastboot endpoint is the SpacemiT K1/K3 gadget; only then does the page offer the
    /// SpacemiT section.
    /// <para>所选 fastboot 端点是否为进迭时空 K1/K3 gadget；只有此时页面才提供进迭时空分类。</para>
    /// </summary>
    internal bool IsSpacemiTSelected => _deviceViewModel?.SelectedDevice?.IsSpacemiTFastboot == true;

    /// <summary>
    /// The last ADB root-shell verdict for the selected device key; <see langword="null"/> until a probe for
    /// that key completed, so an unprobed device never looks authorized.
    /// <para>所选设备键最近一次的 ADB root shell 结论；对该键的探测完成前为 <see langword="null"/>，
    /// 因此未探测设备绝不会被当作已授权。</para>
    /// </summary>
    private (string Key, bool HasSu)? _adbSuVerdict;

    /// <summary>
    /// Serializes ADB root-shell publications so a stale probe never overwrites a newer verdict.
    /// <para>串行化 ADB root shell 结论的发布，使过期探测不会覆盖更新的结论。</para>
    /// </summary>
    private int _adbSuProbeEpoch;

    /// <summary>
    /// Cooperative bound for the ADB root-shell probe, so a consent prompt on the device cannot hold the page.
    /// <para>ADB root shell 探测的协作式时限，使设备上的授权弹窗不会占住页面。</para>
    /// </summary>
    private const int AdbSuProbeTimeoutMs = 5000;

    /// <summary>
    /// The selected device's confirmed noninteractive root shell; a device that was never probed, a probe still
    /// running and a probe that failed all report <see langword="false"/>.
    /// <para>所选设备已确认的非交互 root shell；从未探测、探测仍在进行以及探测失败都报告
    /// <see langword="false"/>。</para>
    /// </summary>
    private bool HasConfirmedAdbSu => SelectedDevice is { Protocol: DeviceProtocolKind.Adb } device
        && _adbSuVerdict is { } verdict
        && string.Equals(verdict.Key, device.Key, StringComparison.Ordinal)
        && verdict.HasSu;

    /// <summary>
    /// The controls that are never disabled: the log area, whose output has to stay readable and copyable at every
    /// moment of a run. No enable decision can turn them off.
    /// <para>任何情况下都不被禁用的控件：日志区，运行中的任何时刻其输出都必须可阅读与复制。
    /// 任何启用决定都无法把它们关闭。</para>
    /// </summary>
    private static readonly string[] AlwaysEnabledControlNames =
    [
        "LogBox",
        "ClearLogButton"
    ];

    internal static readonly HashSet<string> AlwaysEnabledControls =
        new(AlwaysEnabledControlNames, StringComparer.Ordinal);

    /// <summary>
    /// Records the enable intent of one control and writes it to the page, restricted by the selected device's mode
    /// and by the busy state of the side action pane. The intent is recorded even while the control is forced off, so
    /// leaving the busy state restores it; the log area can never be turned off.
    /// <para>记录单个控件的启用意图并写入页面，按所选设备的模式与侧边操作区的忙碌状态施加限制。即使控件被强制关闭，
    /// 意图仍被记录，退出忙碌后即可恢复；日志区永远无法被关闭。</para>
    /// </summary>
    /// <param name="name">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="enabled">Whether the page state wants the control enabled.
    /// <para>页面状态是否希望启用该控件。</para></param>
    internal void SetControlEnabled(string name, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (AlwaysEnabledControls.Contains(name))
        {
            enabled = true;
        }

        _controlIntent[name] = enabled;
        if (_surface?.FindControl<Control>(name) is not { } control)
        {
            return;
        }

        control.IsEnabled = !(_busy && FlashDeviceUiPolicy.IsBusyDisabled(name)) &&
            enabled && IsOfferedByDevice(name);
    }

    /// <summary>
    /// Snapshots the enable state the controls start with, before any device restriction is applied to them.
    /// <para>在施加设备限制之前，记录控件初始的启用状态。</para>
    /// </summary>
    private void CaptureControlIntent()
    {
        if (_surface is null)
        {
            return;
        }

        foreach (var name in FlashDeviceUiPolicy.GatedControls)
        {
            if (_controlIntent.ContainsKey(name) || _surface.FindControl<Control>(name) is not { } control)
            {
                continue;
            }

            _controlIntent[name] = control.IsEnabled;
        }
    }

    /// <summary>
    /// The single availability verdict for one control: the device mode and Rockchip stage from the policy table,
    /// plus the ADB root-shell verdict for the controls that need it.
    /// <para>单个控件的唯一可用性结论：来自策略表的设备模式与瑞芯微阶段，以及对需要它的控件追加的
    /// ADB root shell 结论。</para>
    /// </summary>
    /// <param name="name">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <returns><see langword="true"/> when the page may enable the control.
    /// <para>页面可启用该控件时为 <see langword="true"/>。</para></returns>
    private bool IsOfferedByDevice(string name)
    {
        if (!FlashDeviceUiPolicy.IsAvailable(name, DeviceMode, RkStage, DeviceKind, IsSpacemiTSelected))
        {
            return false;
        }

        return DeviceKind != FlashDeviceKind.Adb ||
            !FlashDeviceUiPolicy.RequiresAdbSu(name) ||
            HasConfirmedAdbSu;
    }

    /// <summary>
    /// Re-applies the device restriction to every gated control; called whenever the selected device can have
    /// changed, so a mode without a section leaves that section unusable.
    /// <para>把设备限制重新施加到全部受控控件；所选设备可能变化时调用，使没有专属分类的模式关闭对应分类。</para>
    /// </summary>
    internal void RefreshDeviceUiGate()
    {
        if (_surface is null)
        {
            return;
        }

        var mode = DeviceMode;
        foreach (var name in FlashDeviceUiPolicy.GatedControls)
        {
            if (_surface.FindControl<Control>(name) is not { } control)
            {
                continue;
            }

            var intent = _controlIntent.TryGetValue(name, out var stored) ? stored : control.IsEnabled;
            _controlIntent[name] = intent;
            control.IsEnabled = !(_busy && FlashDeviceUiPolicy.IsBusyDisabled(name)) && intent &&
                IsOfferedByDevice(name);
        }
    }

    /// <summary>
    /// Probes the selected ADB device for a noninteractive root shell once per device key and republishes the gate,
    /// so the shared flash action becomes usable only on a device that actually answers a root command.
    /// <para>对所选 ADB 设备按设备键各探测一次非交互 root shell 并重新发布门控，使通用烧录动作只在确实能应答
    /// root 命令的设备上可用。</para>
    /// </summary>
    /// <remarks>
    /// The probe runs off the UI thread with the page's own bound, and every failure is recorded as an unavailable
    /// verdict instead of an exception: a consent prompt, a missing su and a dropped link all leave the flash action
    /// disabled. A verdict is cached by device key, so re-selecting the same device does not probe again, and a
    /// cancelled or superseded probe never overwrites a newer verdict.
    /// <para>探测经页面的协作时限在 UI 线程之外运行，任何失败都记为不可用结论而不是异常：授权弹窗、缺少 su 与链路
    /// 断开都让烧录动作保持禁用。结论按设备键缓存，重新选中同一设备不会再探测；被取消或被取代的探测不会覆盖更新的
    /// 结论。</para>
    /// </remarks>
    private async void RefreshAdbSuGate()
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Adb } device)
        {
            _adbSuVerdict = null;
            return;
        }

        if (_adbSuVerdict is { } cached && string.Equals(cached.Key, device.Key, StringComparison.Ordinal))
        {
            return;
        }

        var epoch = ++_adbSuProbeEpoch;
        bool hasSu;
        try
        {
            using var timeout = new CancellationTokenSource(AdbSuProbeTimeoutMs);
            hasSu = await Task.Run(
                () => FlashSuService.HasSuAsync(device, timeout.Token).GetAwaiter().GetResult(),
                timeout.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Write(LogLevel.Debug, "Flash",
                $"ADB root probe for {device.Key} is unavailable: {exception.Message}");
            hasSu = false;
        }

        if (epoch != _adbSuProbeEpoch || SelectedDevice?.Key != device.Key)
        {
            return;
        }

        _adbSuVerdict = (device.Key, hasSu);
        RefreshDeviceUiGate();
    }
}