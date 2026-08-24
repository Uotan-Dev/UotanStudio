using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MenuViewModel MenuVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public LogViewModel LogVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

    // 设备面板是全局单例：首次访问时创建并注册到 DeviceViewModel.Instance，
    // 主窗口与工作区停靠面板共享同一实例，避免双实例双订阅设备变更。
    // The device panel is a global singleton: created on first access and registered in the
    // DeviceViewModel constructor, shared by the main window and the workspace dock so no
    // duplicate instance subscribes to device changes twice.
    public DeviceViewModel DeviceVM { get; } = DeviceViewModel.EnsureInstance();

    /// <summary>
    /// <para>构造主视图模型，并将节点编辑器引用绑定到菜单以便保存/打开工程文件。</para>
    /// Constructs the main view model, binding the node editor reference to the menu for save/open project operations.
    /// </summary>
    public MainViewModel()
    {
        MenuVM.NodeEditorVM = NodeEditorVM;
    }
}