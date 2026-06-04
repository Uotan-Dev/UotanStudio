using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Pages.DockControl;

namespace UotanStudio.ViewModels
{
    public class WorkspaceViewModel : ViewModelBase
    {
        private readonly IFactory _factory;
        private IRootDock? _layout;

        public IRootDock? Layout
        {
            get => _layout;
            set => SetProperty(ref _layout, value);
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
}
