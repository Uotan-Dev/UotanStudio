using UotanStudio.Areas.Device;
using UotanStudio.Areas.Log;
using UotanStudio.Areas.NodeEditor;
using UotanStudio.Areas.Settings;

namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // 设备面板是全局单例：首次访问时创建并在构造函数中注册到 DeviceViewModel.Instance，
    // 主窗口与工作区停靠面板共享同一实例，避免双实例双订阅设备变更。
    // The device panel is a global singleton: created on first access and registered in the
    // DeviceViewModel constructor, shared by the main window and the workspace dock so no
    // duplicate instance subscribes to device changes twice.
    public DeviceViewModel DeviceVM { get; } = DeviceViewModel.EnsureInstance();
    public LogViewModel LogVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();
}