using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Areas.DockControl;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Workspace;

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