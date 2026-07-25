using Avalonia.Controls;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views;

/// <summary>
/// <para>设备管理器独立对话框窗口，承载 <see cref="DeviceManagerView"/>。</para>
/// Standalone dialog window hosting <see cref="DeviceManagerView"/>.
/// </summary>
public partial class DeviceManagerDialog : Window
{
    /// <summary>
    /// <para>构建设备管理器对话框。</para>
    /// Constructs the device manager dialog.
    /// </summary>
    public DeviceManagerDialog()
    {
        InitializeComponent();
        DataContext = new DeviceManagerViewModel();
    }
}
