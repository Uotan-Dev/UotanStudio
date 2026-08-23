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
/// <para>节点编辑器的 Dock 布局工厂，创建包含工具箱、画布、属性面板和执行日志的布局。</para>
/// Dock layout factory for the node editor, creating a layout with toolbox, canvas, properties panel, and execution log.
/// <para>备注：当前 NodeEditorView 使用固定的 DockPanel 布局；此类作为可停靠浮动布局的备用实现保留，
/// 供未来需要支持面板拖拽分离/重停靠的场景启用。</para>
/// Note: NodeEditorView currently uses a fixed DockPanel layout; this factory is retained as a standby
/// implementation of a dockable floating layout, to be enabled when panel tear-off/re-docking is needed.
/// </summary>
public class NodeDockFactory : Factory
{
    /// <summary>
    /// <para>创建节点编辑器的 Dock 布局。</para>
    /// Creates the dock layout for the node editor.
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
            Title = "NodeLeftPane",
            Proportion = 0.2,
            ActiveDockable = nodeToolbox,
            VisibleDockables = CreateList<IDockable>(nodeToolbox)
        };

        var rightTools = new ToolDock
        {
            Id = "NodeRightPane",
            Title = "NodeRightPane",
            Proportion = 0.2,
            ActiveDockable = nodeProperties,
            VisibleDockables = CreateList<IDockable>(nodeProperties)
        };

        var bottomTools = new ToolDock
        {
            Id = "NodeBottomPane",
            Title = "NodeBottomPane",
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

        // 显式启用托管托管模式，避免 ManagedWindowLayer 存在却无 Dock 而触发 #PART_Windows 绑定错误。
        // Explicitly enable managed hosting so the ManagedWindowLayer is bound to a dock and the
        // theme's #PART_Windows ItemsSource binding does not log "Value is null".
        rootDock.FloatingWindowHostMode = DockFloatingWindowHostMode.Managed;

        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    /// <summary>
    /// <para>初始化 Dock 布局，注册上下文定位器和宿主窗口定位器。</para>
    /// Initializes the dock layout, registering context locators and host window locators.
    /// </summary>
    /// <param name="layout">
    /// <para>要初始化的布局。</para>
    /// The layout to initialize.
    /// </param>
    public override void InitLayout(IDockable layout)
    {
        // 主题模板会直接绑定 DockCapabilityOverrides/DockCapabilityPolicy，预置非空对象避免 "Value is null" 绑定错误
        DockCapabilityDefaults.Ensure(layout);

        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["NodeToolbox"] = () => LanguageService.Instance.GetString("Node_Toolbox"),
            ["NodeProperties"] = () => LanguageService.Instance.GetString("Node_Properties"),
            ["ExecutionLog"] = () => LanguageService.Instance.GetString("Node_ExecLog"),
            ["NodeCanvas"] = () => LanguageService.Instance.GetString("Node_Canvas")
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