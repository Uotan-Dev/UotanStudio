using Avalonia.Controls;

namespace UotanStudio.Pages;

/// <summary>
/// Device page view, providing ADB/Fastboot device list and connection management interface.
/// <para>设备页面视图，提供 ADB/Fastboot 设备列表与连接管理界面。</para>
/// </summary>
public partial class DeviceView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceView"/> class.
    /// <para>初始化 <see cref="DeviceView"/> 类的新实例。</para>
    /// </summary>
    public DeviceView()
    {
        InitializeComponent();
    }
}