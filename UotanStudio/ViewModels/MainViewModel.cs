namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public DeviceViewModel DeviceVM { get; } = new();
    public LogViewModel LogVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();
}