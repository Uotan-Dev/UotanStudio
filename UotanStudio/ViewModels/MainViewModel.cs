namespace UotanStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public WorkspaceViewModel WorkspaceVM { get; } = new();
}
