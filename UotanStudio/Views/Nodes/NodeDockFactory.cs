using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;

namespace UotanStudio.Views.Nodes;

/// <summary>
/// Dock layout factory for the node editor, creating a layout with toolbox, canvas, properties panel, and execution log.
/// <para>节点编辑器的 Dock 布局工厂，创建包含工具箱、画布、属性面板和执行日志的布局。</para>
/// </summary>
public class NodeDockFactory : Factory
{
    /// <summary>
    /// Creates the dock layout for the node editor.
    /// <para>创建节点编辑器的 Dock 布局。</para>
    /// </summary>
    /// <returns>根 Dock 布局。</returns>
    public override IRootDock CreateLayout()
    {
        var nodeToolbox = new Tool
        {
            Id = "NodeToolbox",
            Title = LanguageService.Instance.GetString("Node_Toolbox"),
            CanClose = false
        };

        var nodeProperties = new Tool
        {
            Id = "NodeProperties",
            Title = LanguageService.Instance.GetString("Node_Properties"),
            CanClose = false
        };

        var executionLog = new Tool
        {
            Id = "ExecutionLog",
            Title = LanguageService.Instance.GetString("Node_ExecLog"),
            CanClose = false
        };

        var nodeCanvas = new Document
        {
            Id = "NodeCanvas",
            Title = LanguageService.Instance.GetString("Node_Canvas"),
            CanClose = false
        };

        var documentDock = new DocumentDock
        {
            Id = "NodeCanvasPane",
            Title = LanguageService.Instance.GetString("Node_Canvas"),
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = nodeCanvas,
            VisibleDockables = CreateList<IDockable>(nodeCanvas)
        };

        var leftTools = new ToolDock
        {
            Id = "NodeLeftPane",
            Title = LanguageService.Instance.GetString("Dock_NodeLeftPane"),
            Proportion = 0.2,
            ActiveDockable = nodeToolbox,
            VisibleDockables = CreateList<IDockable>(nodeToolbox)
        };

        var rightTools = new ToolDock
        {
            Id = "NodeRightPane",
            Title = LanguageService.Instance.GetString("Dock_NodeRightPane"),
            Proportion = 0.2,
            ActiveDockable = nodeProperties,
            VisibleDockables = CreateList<IDockable>(nodeProperties)
        };

        var bottomTools = new ToolDock
        {
            Id = "NodeBottomPane",
            Title = LanguageService.Instance.GetString("Dock_NodeBottomPane"),
            Proportion = 0.25,
            ActiveDockable = executionLog,
            VisibleDockables = CreateList<IDockable>(executionLog)
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Orientation = Orientation.Vertical,
                    Proportion = 0.6,
                    VisibleDockables = CreateList<IDockable>(
                        documentDock,
                        new ProportionalDockSplitter(),
                        bottomTools
                    )
                },
                new ProportionalDockSplitter(),
                rightTools
            )
        };

        var rootDock = CreateRootDock();
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    /// <summary>
    /// Initializes the dock layout, registering context locators and host window locators.
    /// <para>初始化 Dock 布局，注册上下文定位器和宿主窗口定位器。</para>
    /// </summary>
    /// <param name="layout">
    /// The layout to initialize.
    /// <para>要初始化的布局。</para>
    /// </param>
    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["NodeToolbox"] = () => LanguageService.Instance.GetString("Node_Toolbox"),
            ["NodeProperties"] = () => LanguageService.Instance.GetString("Node_Properties"),
            ["ExecutionLog"] = () => LanguageService.Instance.GetString("Node_ExecLog"),
            ["NodeCanvas"] = () => LanguageService.Instance.GetString("Node_Canvas")
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => CreateHostWindow(),
            ["ManagedHostWindow"] = () => CreateManagedHostWindow()
        };

        DefaultHostWindowLocator = () => CreateHostWindow();

        base.InitLayout(layout);
    }

    private static HostWindow CreateHostWindow()
    {
        var options = new Dock.Model.Core.DockManagerOptions { IsDockingEnabled = true };
        return new HostWindow(options);
    }

    private static ManagedHostWindow CreateManagedHostWindow()
    {
        var options = new Dock.Model.Core.DockManagerOptions { IsDockingEnabled = true };
        return new ManagedHostWindow(options);
    }

    /// <summary>
    /// Closes a dockable panel with safe removal.
    /// <para>关闭可停靠面板，安全移除。</para>
    /// </summary>
    /// <param name="dockable">
    /// The dockable to close.
    /// <para>要关闭的可停靠面板。</para>
    /// </param>
    public override void CloseDockable(IDockable? dockable)
    {
        if (dockable == null || !dockable.CanClose)
        {
            return;
        }

        try
        {
            SafeRemoveDockable(dockable);
        }
        catch
        {

        }
    }

    private void SafeRemoveDockable(IDockable dockable)
    {
        try
        {
            if (dockable?.Owner is IDock dock && dock.VisibleDockables != null)
            {
                var list = new List<IDockable>(dock.VisibleDockables);
                if (list.Contains(dockable))
                {
                    list.Remove(dockable);
                    dock.VisibleDockables = CreateList(list.ToArray());

                    if (dock.ActiveDockable == dockable)
                    {
                        dock.ActiveDockable = list.Count > 0 ? list[0] : null;
                    }
                }
            }

            dockable?.Owner = null;
        }
        catch
        {

        }
    }
}