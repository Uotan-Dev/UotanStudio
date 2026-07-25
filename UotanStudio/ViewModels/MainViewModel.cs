using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Plugins;

namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MenuViewModel MenuVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public DeviceManagerViewModel DeviceManagerVM { get; } = new();
    public PluginManagerViewModel PluginManagerVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

    /// <summary>
    /// <para>构造主视图模型，并将节点编辑器引用绑定到菜单以便保存/打开工程文件。</para>
    /// Constructs the main view model, binding the node editor reference to the menu for save/open project operations.
    /// </summary>
    public MainViewModel()
    {
        MenuVM.NodeEditorVM = NodeEditorVM;
    }
}
