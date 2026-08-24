using Avalonia.Controls;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views;

/// <summary>
/// <para>设备管理器独立窗口，绑定全局设备面板单例以展示与管理 USB/ADB/Fastboot 设备。</para>
/// Standalone device manager window, bound to the global device panel singleton to display and manage USB/ADB/Fastboot devices.
/// </summary>
public partial class DeviceWindow : Window
{
    /// <summary>
    /// <para>初始化设备管理器窗口，并将数据上下文指向 <see cref="DeviceViewModel.Instance"/> 单例。</para>
    /// Initializes the device manager window, binding the data context to the <see cref="DeviceViewModel.Instance"/> singleton.
    /// </summary>
    public DeviceWindow()
    {
        InitializeComponent();
        // 使用 EnsureInstance 确保首次打开时创建设备面板单例（Instance 本身不会自创建）
        DataContext = DeviceViewModel.EnsureInstance();
    }
}