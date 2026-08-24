using System.Collections.Generic;
using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Areas.DockControl;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Workspace;

public class WorkspaceViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取当前工作区实例（由 <see cref="WorkspaceView"/> 创建时登记），供窗口管理器操作同一停靠布局；
    /// 未创建工作区视图时为空。</para>
    /// Gets the current workspace instance, registered by <see cref="WorkspaceView"/> when created,
    /// which the window manager uses to operate the same dock layout; null before the workspace
    /// view is constructed.
    /// </summary>
    public static WorkspaceViewModel? Instance { get; private set; }

    private readonly WorkspaceDockFactory _factory;

    public IRootDock? Layout
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets the toggleable tool windows mapped to their target dock panes.
    /// <para>获取可切换的工具窗口及其目标停靠面板的映射。</para>
    /// </summary>
    public IReadOnlyDictionary<string, DockWindowEntry> WindowCatalog => _factory.ToolWindows;

    public WorkspaceViewModel()
    {
        Instance = this;
        _factory = new WorkspaceDockFactory();
        Layout = _factory.CreateLayout();
        if (Layout != null)
        {
            _factory.InitLayout(Layout);
        }
    }

    /// <summary>
    /// Determines whether the specified window is currently shown in the workspace dock.
    /// <para>判断指定窗口当前是否显示在工作区停靠布局中。</para>
    /// </summary>
    /// <param name="id">The window id.</param>
    /// <returns><c>true</c> if the window is shown; otherwise, <c>false</c>.</returns>
    public bool IsWindowShown(string id)
        => WindowCatalog.TryGetValue(id, out var entry) && entry.Window.Owner is not null;

    /// <summary>
    /// Shows or hides the specified window in the workspace dock.
    /// <para>在工作区停靠布局中显示或隐藏指定窗口。</para>
    /// </summary>
    /// <param name="id">The window id.</param>
    /// <param name="visible"><c>true</c> to show, <c>false</c> to hide.</param>
    public void SetWindowVisible(string id, bool visible)
    {
        if (!WindowCatalog.TryGetValue(id, out var entry))
        {
            return;
        }

        var shown = entry.Window.Owner is not null;
        if (shown == visible)
        {
            return;
        }

        if (visible)
        {
            _factory.AddDockable(entry.TargetDock, entry.Window);
            entry.TargetDock.ActiveDockable = entry.Window;
        }
        else
        {
            _factory.RemoveDockable(entry.Window, collapse: true);
        }
    }
}