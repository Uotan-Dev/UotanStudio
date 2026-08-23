using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;

namespace UotanStudio.Pages.DockControl;

/// <summary>
/// Factory for creating the workspace dock layout.
/// <para>用于创建工作区停靠布局的工厂。</para>
/// </summary>
public class WorkspaceDockFactory : Factory
{
    /// <summary>
    /// Creates the root dock layout.
    /// <para>创建根停靠布局。</para>
    /// </summary>
    /// <returns>The root dock layout.</returns>
    public override IRootDock CreateLayout()
    {
        var document1 = new Document { Id = "Document1", Title = "build.prop", CanClose = true };
        var document2 = new Document { Id = "Document2", Title = "init.rc", CanClose = true };

        var projectExplorer = new Tool { Id = "ProjectExplorer", Title = LanguageService.Instance.GetString("Dock_ProjectExplorer"), CanClose = true };
        var properties = new Tool { Id = "Properties", Title = LanguageService.Instance.GetString("Dock_Properties"), CanClose = true };
        var output = new Tool { Id = "Output", Title = LanguageService.Instance.GetString("Dock_Output"), CanClose = true };
        var adbLog = new Tool { Id = "AdbLog", Title = LanguageService.Instance.GetString("Dock_AdbLog"), CanClose = true };

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = LanguageService.Instance.GetString("Dock_Documents"),
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = document1,
            VisibleDockables = CreateList<IDockable>(document1, document2)
        };

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.25,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties)
        };

        var leftTools = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            Proportion = 0.25,
            ActiveDockable = projectExplorer,
            VisibleDockables = CreateList<IDockable>(projectExplorer)
        };

        var bottomTools = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            Proportion = 0.25,
            ActiveDockable = output,
            VisibleDockables = CreateList<IDockable>(output, adbLog)
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
                    Proportion = 0.5,
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

        // 显式启用托管窗口托管模式：DockControl.EnableManagedWindowLayer 默认为 true，但
        // DockSettings 默认不启用托管托管。若不设置，ManagedWindowLayer 虽被创建却从未绑定 Dock，
        // 其主题模板中的 #PART_Windows(ItemsSource=$parent[ManagedWindowLayer].Dock.VisibleDockables)
        // 会因 Dock 为 null 而输出 "Value is null" 绑定错误。
        // Explicitly enable managed window hosting: DockControl.EnableManagedWindowLayer defaults to
        // true, but DockSettings does not enable managed hosting by default. Without this, the
        // ManagedWindowLayer is created but never given a Dock, so its theme's #PART_Windows
        // (ItemsSource=$parent[ManagedWindowLayer].Dock.VisibleDockables) logs "Value is null"
        // binding errors because Dock is null.
        rootDock.FloatingWindowHostMode = DockFloatingWindowHostMode.Managed;

        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    /// <summary>
    /// Initializes the dock layout.
    /// <para>初始化停靠布局。</para>
    /// </summary>
    /// <param name="layout">The dockable layout.</param>
    public override void InitLayout(IDockable layout)
    {
        // 主题模板会直接绑定 DockCapabilityOverrides/DockCapabilityPolicy，预置非空对象避免 "Value is null" 绑定错误
        DockCapabilityDefaults.Ensure(layout);

        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Document1"] = () => "# build.prop\n# Auto-generated build properties\n\nro.build.id=UotanStudio\nro.build.version.release=14\nro.product.model=ExampleDevice\nro.build.type=userdebug\n",
            ["Document2"] = () => "# init.rc - Android init script\n\nservice adbd /system/bin/adbd\n    class core\n    socket adbd stream 660 system system\n",
            ["ProjectExplorer"] = () => "ProjectExplorer",
            ["Properties"] = () => "Properties",
            ["Output"] = () => "Output",
            ["AdbLog"] = () => "AdbLog"
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
            ["ManagedHostWindow"] = () => new ManagedHostWindow()
        };

        DefaultHostWindowLocator = () => new HostWindow();

        base.InitLayout(layout);
    }
}