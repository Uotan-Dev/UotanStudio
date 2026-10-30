using Avalonia.Controls;

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
    /// Records the enable intent of one control and writes it to the page, restricted by the selected device's mode.
    /// <para>记录单个控件的启用意图并写入页面，按所选设备的模式加以限制。</para>
    /// </summary>
    /// <param name="name">Name of the XAML control. <para>XAML 控件名称。</para></param>
    /// <param name="enabled">Whether the page state wants the control enabled.
    /// <para>页面状态是否希望启用该控件。</para></param>
    internal void SetControlEnabled(string name, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(name);
        _controlIntent[name] = enabled;
        if (_surface?.FindControl<Control>(name) is not { } control)
        {
            return;
        }

        control.IsEnabled = enabled && FlashDeviceUiPolicy.IsAvailable(name, DeviceMode);
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
            control.IsEnabled = intent && FlashDeviceUiPolicy.IsAvailable(name, mode);
        }
    }
}