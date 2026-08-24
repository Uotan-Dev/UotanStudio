using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Areas.Analyzer;
using UotanStudio.Areas.Device;
using UotanStudio.Areas.Editor;
using UotanStudio.Areas.Output;
using UotanStudio.Areas.ProjectExplorer;
using UotanStudio.Areas.Properties;
using UotanStudio.Services;

namespace UotanStudio.Areas.DockControl;

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
        var document1 = new Document { Id = "Document1", Title = LanguageService.Instance.GetString("Dock_Editor"), CanClose = false };

        var projectExplorer = new Tool { Id = "ProjectExplorer", Title = LanguageService.Instance.GetString("Dock_ProjectExplorer"), CanClose = false };
        var properties = new Tool { Id = "Properties", Title = LanguageService.Instance.GetString("Dock_Properties"), CanClose = false };
        var analyzer = new Tool { Id = "Analyzer", Title = LanguageService.Instance.GetString("Dock_Analyzer"), CanClose = false };
        var output = new Tool { Id = "Output", Title = LanguageService.Instance.GetString("Dock_Output"), CanClose = false };
        var adbLog = new Tool { Id = "AdbLog", Title = LanguageService.Instance.GetString("Dock_AdbLog"), CanClose = true };

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = LanguageService.Instance.GetString("Dock_Documents"),
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = document1,
            VisibleDockables = CreateList<IDockable>(document1)
        };

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.3,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(analyzer, properties)
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

        // 面板视图模型：ProjectExplorer/Editor/Analyzer/Output 均为真实能力，经全局
        // ViewLocator 映射到对应视图；文档区共享同一 EditorViewModel 以承载打开的文档。
        // Panel view models: ProjectExplorer/Editor/Analyzer/Output map through the global
        // ViewLocator to their views; the document area shares one EditorViewModel to host
        // the opened documents.
        var explorer = new ProjectExplorerViewModel();
        var editor = new EditorViewModel();
        var analyzer = new AnalyzerViewModel();
        var output = new OutputViewModel();
        var properties = new PropertiesViewModel();

        // 文档上下文：启动时打开两个占位文档，引导用户使用编辑器。
        // Document contexts: open two placeholder documents at startup to guide usage.
        editor.OpenFileFromText("build.prop", "# build.prop\n# Auto-generated build properties\n\nro.build.id=UotanStudio\nro.build.version.release=14\nro.product.model=ExampleDevice\nro.build.type=userdebug\n");
        editor.OpenFileFromText("init.rc", "# init.rc - Android init script\n\nservice adbd /system/bin/adbd\n    class core\n    socket adbd stream 660 system system\n");

        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["ProjectExplorer"] = () => explorer,
            ["Analyzer"] = () => analyzer,
            ["Properties"] = () => properties,
            ["Output"] = () => output,
            ["AdbLog"] = () => output,
            ["Device"] = () => DeviceViewModel.Instance,
            ["Document1"] = () => editor
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        DefaultHostWindowLocator = () => new HostWindow();

        base.InitLayout(layout);
    }
}