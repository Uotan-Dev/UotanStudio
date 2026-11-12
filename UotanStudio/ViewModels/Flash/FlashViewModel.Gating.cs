using System.Collections.Frozen;
using Avalonia.Controls;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Device awareness of the flash page: every control carries its own <see cref="FlashControlGate"/>, every enable
/// decision goes through <see cref="SetControlEnabled"/>, and a control stays unusable until a device satisfying that
/// control's own condition is selected. A control the selected device does not offer is disabled, never removed, so the
/// page keeps every action discoverable and only reports what the current device can do.
/// <para>烧录页面的设备感知：每个控件自带 <see cref="FlashControlGate"/>，所有启用决定都经
/// <see cref="SetControlEnabled"/> 作出，控件在选中满足其自身条件的设备之前保持不可用。所选设备不提供的控件只会被禁用而不会
/// 被移除，因此页面仍保留每个动作入口，只表明当前设备能做什么。</para>
/// </summary>
/// <remarks>
/// The page state stays authoritative for what it wants enabled; <see cref="RefreshDeviceUiGate"/> re-applies each
/// control's own condition on top of that intent whenever the selected device changes, so a device swap neither leaves
/// a disabled control enabled nor forgets what the page had enabled for the previous one.
/// <para>页面状态仍然是“想要启用什么”的依据；选中设备变化时，<see cref="RefreshDeviceUiGate"/> 在该意图之上重新施加
/// 每个控件自身的条件，因此换设备既不会留下被禁用的可用控件，也不会丢掉页面对上一台设备的启用状态。</para>
/// </remarks>
public sealed partial class FlashViewModel
{
    /// <summary>
    /// Enable intent the page last asked for, per control name; each control's own device condition is applied on top of it.
    /// <para>页面最近一次请求的启用意图（按控件名）；每个控件自身的设备条件叠加在其之上。</para>
    /// </summary>
    private readonly Dictionary<string, bool> _controlIntent = new(StringComparer.Ordinal);

    /// <summary>
    /// The control each gate belongs to; the page evaluates gates by walking these controls, never by looking a gate up
    /// from a page-wide name table.
    /// <para>每个门控所属的控件；页面通过遍历这些控件来求值，而不是到页面级名称表中反查门控。</para>
    /// </summary>
    private readonly Dictionary<Control, FlashControlGate> _controlGates = [];

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
    /// The boot stage the selected SpacemiT gadget reports: the BootROM download mode cannot write partitions, so the
    /// shared flash action stays closed until the boot chain has been sent.
    /// <para>所选进迭时空 gadget 上报的启动阶段：BootROM 下载模式无法写分区，因此在发送引导链之前通用烧录动作保持关闭。</para>
    /// </summary>
    internal SpacemiTBootStage SelectedFastbootStage => SelectedDevice?.FastbootStage ?? SpacemiTBootStage.Unknown;

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

    internal static readonly FrozenSet<string> AlwaysEnabledControls =
        AlwaysEnabledControlNames.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The device and page snapshot every control's own condition is evaluated against.
    /// <para>每个控件自身条件求值所用的设备与页面快照。</para>
    /// </summary>
    private FlashGateContext GateContext =>
        new(DeviceMode, DeviceKind, RkStage, IsSpacemiTSelected, HasConfirmedAdbSu, HasSelectedPartitions,
            SelectedFastbootStage);

    /// <summary>
    /// Registers every control's own gate; called once when the page is attached, before any enable decision reads it.
    /// <para>注册每个控件自身的门控；页面附加时调用一次，且先于任何读取门控的启用决定。</para>
    /// </summary>
    internal void RegisterControlGates()
    {
        RegisterPageControlGates();
        RegisterSharedControlGates();
        RegisterEdlControlGates();
        RegisterMtkControlGates();
        RegisterRockchipControlGates();
        RegisterSunxiControlGates();
        RegisterImageControlGates();
    }

    /// <summary>
    /// Package bar and partition toolbar: these actions work on the page state rather than on the device, so they carry
    /// only the busy condition; the delete action additionally needs selected rows.
    /// <para>刷机包栏与分区工具栏：这些动作作用于页面状态而不是设备，因此只持有忙碌条件；删除动作还要求已勾选的行。</para>
    /// </summary>
    private void RegisterPageControlGates()
    {
        RegisterGate("ChooseFileButton", FlashControlGate.BusyOnly("ChooseFileButton"));
        RegisterGate("ChooseFolderButton", FlashControlGate.BusyOnly("ChooseFolderButton"));
        RegisterGate("GetPackageButton", FlashControlGate.BusyOnly("GetPackageButton"));
        RegisterGate("AddPartitionButton", FlashControlGate.BusyOnly("AddPartitionButton"));
        RegisterGate("DeletePartitionsButton", FlashControlGate.SelectionRequired("DeletePartitionsButton"));
    }

    /// <summary>
    /// Attaches one gate to the control it belongs to; a gate whose control is missing is reported to the log instead of
    /// silently leaving that control ungated.
    /// <para>把门控挂到它所属的控件上；控件缺失时写入日志，而不是让该控件悄悄失去门控。</para>
    /// </summary>
    /// <param name="name">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="gate">The control's own condition. <para>该控件自身的条件。</para></param>
    private void RegisterGate(string name, FlashControlGate gate)
    {
        if (_surface?.FindControl<Control>(name) is not { } control)
        {
            _log.Write(LogLevel.Debug, "Flash", $"Gated control {name} is missing from the page.");
            return;
        }

        _controlGates[control] = gate;
    }

    /// <summary>
    /// Shared action pane: the common actions, the option check boxes and the two option panels.
    /// <para>通用操作区：通用动作、选项复选框与两个选项面板。</para>
    /// </summary>
    /// <remarks>
    /// The seven option check boxes carry no device condition: a tick is an intent the user states before choosing a
    /// device, each flash path reads the options it can act on, and the flash action itself reports a protocol mismatch
    /// when a chosen option has no path there. Only a running page operation closes them.
    /// <para>七个选项复选框不携带设备条件：勾选是用户在选设备之前表达的意图，每条烧录路径各自读取它能作用的选项，而所选选项在
    /// 该设备上没有路径时由烧录动作本身报告协议不匹配。只有页面操作运行期间才关闭它们。</para>
    /// </remarks>
    private void RegisterSharedControlGates()
    {
        RegisterGate("LoadBootButton", FlashControlGate.LoaderOnly("LoadBootButton", busyDisabled: true));
        RegisterGate("ReadInfoButton", FlashControlGate.InfoRead("ReadInfoButton", busyDisabled: true));
        RegisterGate("ReadbackSelectedButton",
            FlashControlGate.Readback("ReadbackSelectedButton", busyDisabled: true));
        RegisterGate("FlashSelectedButton",
            FlashControlGate.SharedFlash("FlashSelectedButton", busyDisabled: true));
        RegisterGate("EraseSelectedButton",
            FlashControlGate.SharedStorage("EraseSelectedButton", busyDisabled: true));
        RegisterGate("SwitchSlotButton", FlashControlGate.SlotSwitch("SwitchSlotButton", busyDisabled: true));
        RegisterGate("ReadPartitionTableButton",
            FlashControlGate.PartitionTableRead("ReadPartitionTableButton", busyDisabled: true));

        // The seven option check boxes state an intent, not a device capability: each flash path reads the options it can
        // act on and the flash action reports the mismatch when a chosen option has no path there, so no device
        // condition can close them. Only a running page operation does.
        // 七个选项复选框表达的是意图而不是设备能力：由各自的烧录路径决定是否读取，没有路径时由烧录动作报告不匹配，
        // 因此没有任何设备条件能关闭它们，只有页面操作运行期间才关闭。
        RegisterGate("DisableVbmetaBox", FlashControlGate.BusyOnly("DisableVbmetaBox"));
        RegisterGate("AddRootBox", FlashControlGate.BusyOnly("AddRootBox"));
        RegisterGate("EraseDataBox", FlashControlGate.BusyOnly("EraseDataBox"));
        RegisterGate("RebootAfterBox", FlashControlGate.BusyOnly("RebootAfterBox"));
        RegisterGate("ShowAllPartitionsBox", FlashControlGate.BusyOnly("ShowAllPartitionsBox"));
        RegisterGate("DeleteCowBox", FlashControlGate.BusyOnly("DeleteCowBox"));
        RegisterGate("SpacemiTVerifyBox", FlashControlGate.BusyOnly("SpacemiTVerifyBox"));
        // The two option groups are shown by their own check box and restricted by nothing else: only a running page
        // operation closes them, which keeps their inner selectors following the busy state as before.
        // 两个选项组由各自的复选框决定显隐，除此之外不受任何限制：只有页面操作运行期间关闭它们，组内选择框因此仍随忙碌态变化。
        RegisterGate("AvbOptionsPanel", FlashControlGate.BusyOnly("AvbOptionsPanel"));
        RegisterGate("RootOptionsPanel", FlashControlGate.BusyOnly("RootOptionsPanel"));
        RegisterGate("ChooseRootPackageButton",
            FlashControlGate.FlashOptionAnyDevice("ChooseRootPackageButton", busyDisabled: true));
    }

    /// <summary>
    /// Records the enable intent of one control and writes it to the page, restricted by the control's own gate and by
    /// the busy state of the side action pane. The intent is recorded even while the control is forced off, so leaving
    /// the busy state restores it; the log area can never be turned off.
    /// <para>记录单个控件的启用意图并写入页面，按该控件自身的门控与侧边操作区的忙碌状态施加限制。即使控件被强制关闭，
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

        var gate = _controlGates.GetValueOrDefault(control);
        ApplyGate(control, gate, enabled, GateContext);
    }

    /// <summary>
    /// Snapshots the enable state the controls start with, before any device restriction is applied to them.
    /// <para>在施加设备限制之前，记录控件初始的启用状态。</para>
    /// </summary>
    private void CaptureControlIntent()
    {
        foreach (var (control, gate) in _controlGates)
        {
            if (_controlIntent.ContainsKey(gate.Name))
            {
                continue;
            }

            _controlIntent[gate.Name] = control.IsEnabled;
        }
    }

    /// <summary>
    /// Re-applies every control's own condition; called whenever the selected device can have changed, so a mode
    /// without a section leaves that section unusable.
    /// <para>重新施加每个控件自身的条件；所选设备可能变化时调用，使没有专属分类的模式关闭对应分类。</para>
    /// </summary>
    internal void RefreshDeviceUiGate()
    {
        if (_surface is null)
        {
            return;
        }

        var context = GateContext;
        foreach (var (control, gate) in _controlGates)
        {
            var intent = _controlIntent.TryGetValue(gate.Name, out var stored) ? stored : control.IsEnabled;
            _controlIntent[gate.Name] = intent;
            ApplyGate(control, gate, intent, context);
        }
    }

    /// <summary>
    /// Writes one control's enable state; a control the selected device does not offer stays visible and only reports
    /// itself unusable, so no device condition ever removes an entry from the page.
    /// <para>写入一个控件的启用状态；所选设备不提供的控件保持可见并仅表明自身不可用，因此任何设备条件都不会把入口从页面上移除。</para>
    /// </summary>
    /// <param name="control">The control the gate belongs to. <para>门控所属的控件。</para></param>
    /// <param name="gate">The control's own gate, or <see langword="null"/> for an ungated control.
    /// <para>该控件自身的门控；未受门控的控件为 <see langword="null"/>。</para></param>
    /// <param name="intent">Whether the page state wants the control enabled. <para>页面状态是否希望启用该控件。</para></param>
    /// <param name="context">The device and page snapshot. <para>设备与页面快照。</para></param>
    private void ApplyGate(Control control, FlashControlGate? gate, bool intent, FlashGateContext context)
    {
        if (gate is null)
        {
            control.IsEnabled = intent;
            return;
        }

        control.IsEnabled = !(_busy && gate.BusyDisabled) && intent && gate.IsOffered(context);
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