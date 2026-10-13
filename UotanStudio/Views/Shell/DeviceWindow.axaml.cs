using Avalonia.Controls;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Standalone device manager window, bound to the global device panel singleton to display and manage USB/ADB/Fastboot devices.
/// <para>设备管理器独立窗口，绑定全局设备面板单例以展示与管理 USB/ADB/Fastboot 设备。</para>
/// </summary>
public partial class DeviceWindow : Window
{
    /// <summary>
    /// Initializes the device manager window, binding the data context to the <see cref="DeviceViewModel.Instance"/> singleton.
    /// <para>初始化设备管理器窗口，并将数据上下文指向 <see cref="DeviceViewModel.Instance"/> 单例。</para>
    /// </summary>
    public DeviceWindow()
    {
        InitializeComponent();

        DataContext = DeviceViewModel.EnsureInstance();
    }
}