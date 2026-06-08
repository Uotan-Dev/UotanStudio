namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();
}
