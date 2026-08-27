using Avalonia.Controls;
using Dock.Model.Mvvm.Controls;

namespace UotanStudio.Views.Devices;

/// <summary>
/// <para>工作区设备面板的轻量 Tool 包装器，承载完整 <see cref="DeviceView"/>，
/// 与设备管理器窗口共享同一 <see cref="ViewModels.Devices.DeviceViewModel"/> 单例，
/// 支持烧写、读取等操作。</para>
/// A lightweight Tool wrapper for the workspace device panel, hosting the full
/// <see cref="DeviceView"/> and sharing the same
/// <see cref="ViewModels.Devices.DeviceViewModel"/> singleton with the device manager window,
/// supporting flash, read and other operations.
/// </summary>
internal sealed class WorkspaceDeviceTool : Tool
{
    /// <summary>
    /// <para>获取面板内容视图。</para>
    /// Gets the content view of the panel.
    /// </summary>
    public Control Content => new DeviceView();
}