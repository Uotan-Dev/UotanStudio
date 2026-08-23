using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Pages.DockControl;

namespace UotanStudio.ViewModels;

public class WorkspaceViewModel : ViewModelBase
{
    private readonly IFactory _factory;

    public IRootDock? Layout
    {
        get;
        set => SetProperty(ref field, value);
    }

    public WorkspaceViewModel()
    {
        _factory = new WorkspaceDockFactory();
        Layout = _factory.CreateLayout();
        if (Layout != null)
        {
            _factory.InitLayout(Layout);
        }
    }
}