namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MenuViewModel MenuVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();
}
