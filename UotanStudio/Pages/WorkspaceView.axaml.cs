using Avalonia.Controls;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 在 DockControl 加载后将主 DockControl 注入工厂，使宿主窗口构造时共享
    /// DockManagerOptions，并注册为外部停靠表面参与浮动→重停靠的命中测试与指示器渲染。
    /// </summary>
    private void OnLoaded(object? sender, System.EventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (vm.Layout?.Factory is not DockControl.WorkspaceDockFactory factory) return;

        var dockControl = DockControl;
        if (dockControl is null) return;

        // 注入主 DockControl 引用，工厂的 HostWindowLocator 会据此共享 DockManagerOptions。
        factory.DockControl = dockControl;

        // 注册主 DockControl 为外部停靠表面，使浮动窗体拖拽时能命中测试并显示停靠指示器。
        // DockControl 自身已实现 IExternalDockSurface，通过 RegisterExternalDockSurface 注册即可。
        if (dockControl is Dock.Avalonia.Contract.IExternalDockSurface surface)
        {
            dockControl.RegisterExternalDockSurface(surface);
        }

        Loaded -= OnLoaded;
    }
}