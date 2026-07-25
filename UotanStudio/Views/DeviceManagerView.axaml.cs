using Avalonia.Controls;

namespace UotanStudio.Views;

/// <summary>
/// <para>设备管理器视图，显示已连接设备及其能力描述，操作按钮随能力动态调整。</para>
/// Device manager view, showing connected devices and their capabilities; action buttons adjust dynamically based on capabilities.
/// </summary>
public partial class DeviceManagerView : UserControl
{
    /// <summary>
    /// <para>构建设备管理器视图。</para>
    /// Constructs the device manager view.
    /// </summary>
    public DeviceManagerView()
    {
        InitializeComponent();
    }
}
