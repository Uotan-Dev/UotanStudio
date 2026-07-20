namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public DeviceViewModel DeviceVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    // LogVM 必须先于 NodeEditorVM 构造，以便注入到 Output 类节点的执行逻辑
    public LogViewModel LogVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; }
    public SettingsViewModel SettingsVM { get; } = new();

    public MainViewModel()
    {
        NodeEditorVM = new NodeEditorViewModel(LogVM);
    }
}
