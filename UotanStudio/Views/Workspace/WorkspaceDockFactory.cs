using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.Services.Workspace;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

public class WorkspaceDockFactory : Factory, IWorkspaceDockFactory
{
    private readonly AiSettingsViewModel _aiSettings;
    private ProjectExplorerViewModel? _projectExplorer;
    internal ProjectExplorerViewModel? ProjectExplorer => _projectExplorer;

    object? IWorkspaceDockFactory.ProjectExplorer => _projectExplorer;
    private PropertiesViewModel? _properties;
    private TerminalDockService? _terminals;
    private readonly HashSet<IDockable> _closingDocuments = [];

    public WorkspaceDockFactory() : this(new AiSettingsViewModel()) { }

    public WorkspaceDockFactory(AiSettingsViewModel aiSettings) => _aiSettings = aiSettings;

    public override IRootDock CreateLayout()
    {
        var projectSetting = ProjectSettingViewModel.EnsureInstance();
        projectSetting.Id = "ProjectSetting";
        projectSetting.CanClose = true;

        var pluginTool = new PluginToolViewModel
        {
            Id = WorkspacePanelService.PluginTool,
            CanClose = true
        };

        _projectExplorer?.Dispose();
        var projectExplorer = _projectExplorer = new ProjectExplorerViewModel
        {
            Id = "ProjectExplorer",
            CanClose = true
        };

        var imagesView = ImageViewModel.EnsureInstance();
        imagesView.Id = "ImagesView";
        imagesView.CanClose = true;

        var aiAssistant = AppFeatures.AiAssistantEnabled ? new AiAssistantViewModel(_aiSettings) : null;

        var properties = _properties = new PropertiesViewModel
        {
            Id = "Properties",
            CanClose = true
        };
        var output = new WorkspaceLogViewModel
        {
            Id = "Output",
            CanClose = true
        };
        var adbLog = new AdbLogViewModel
        {
            Id = "AdbLog",
            CanClose = true
        };
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            EmptyContent = CreateEmptyDocumentContent()
        };

        var rightToolsTop = new ToolDock
        {
            Id = "RightPaneTop",
            Title = "RightPaneTop",
            Proportion = double.NaN,
            ActiveDockable = imagesView,
            VisibleDockables = CreateList<IDockable>(imagesView)
        };
        if (aiAssistant is not null)
            rightToolsTop.VisibleDockables.Add(aiAssistant);

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.35,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties)
        };

        var leftTools = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            Proportion = 0.15,
            ActiveDockable = projectExplorer,

            VisibleDockables = CreateList<IDockable>(projectExplorer, pluginTool)
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
            Id = "WorkspaceMainLayout",
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Id = "WorkspaceCenterColumn",
                    Orientation = Orientation.Vertical,
                    Proportion = 0.7,
                    VisibleDockables = CreateList<IDockable>(
                        documentDock,
                        new ProportionalDockSplitter(),
                        bottomTools
                    )
                },
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Id = "WorkspaceRightColumn",
                    Orientation = Orientation.Vertical,
                    Proportion = 0.15,
                    VisibleDockables = CreateList<IDockable>(
                        rightToolsTop,
                        new ProportionalDockSplitter(),
                        rightTools
                    )
                }
            )
        };

        var rootDock = CreateRootDock();
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        var panels = WorkspacePanelService.Instance;
        panels.Register(WorkspacePanelService.ProjectExplorer, projectExplorer, leftTools, visible: true);
        panels.Register(WorkspacePanelService.ProjectSetting, projectSetting, leftTools, visible: false);
        panels.Register(WorkspacePanelService.PluginTool, pluginTool, leftTools, visible: true);
        panels.Register(WorkspacePanelService.ImagesView, imagesView, rightToolsTop, visible: true);
        if (aiAssistant is not null)
            panels.Register(WorkspacePanelService.AiAssistant, aiAssistant, rightToolsTop, visible: true);
        panels.Register(WorkspacePanelService.Properties, properties, rightTools, visible: true);
        panels.Register(WorkspacePanelService.WorkspaceLog, output, bottomTools, visible: true);
        panels.Register(WorkspacePanelService.AdbLog, adbLog, bottomTools, visible: true);
        panels.RegisterLocalizedTitle(documentDock, "Dock_Documents");
        panels.RegisterTerminals(_terminals = new TerminalDockService(this, bottomTools));
        _terminals.CreateTerminal(activate: false);
        adbLog.Initialize();

        return rootDock;
    }

    /// <summary>
    /// Builds the document pane placeholder shown while no document is open; the text lines bind
    /// their captions through <c>DynamicResource</c> so an application language switch refreshes
    /// them in place instead of keeping the text captured when the layout was created.
    /// <para>构建未打开文档时显示的文档区占位内容；文本行通过 <c>DynamicResource</c> 绑定标题，
    /// 使应用语言切换时就地刷新，而不是保留布局创建时捕获的文本。</para>
    /// </summary>
    private static Control CreateEmptyDocumentContent()
    {
        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 8
        };
        content.Children.Add(new AppIcon { Kind = AppIconKind.File, Width = 48, Height = 48, Opacity = 0.4 });
        content.Children.Add(CreateLocalizedText("Dock_NoDocumentOpen", 16, 0.6));
        content.Children.Add(CreateLocalizedText("Dock_NoDocumentOpenHint", 12, 0.4));
        return content;
    }

    private static TextBlock CreateLocalizedText(string localeKey, double fontSize, double opacity)
    {
        var text = new TextBlock
        {
            FontSize = fontSize,
            Opacity = opacity,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        text.Bind(TextBlock.TextProperty, new DynamicResourceExtension(localeKey));
        return text;
    }

    /// <summary>
    /// Stops all terminal processes owned by this workspace.
    /// <para>停止工作区拥有的全部终端进程。</para>
    /// </summary>
    public void DisposeTerminal() => _terminals?.Dispose();

    /// <summary>
    /// Closes every document dockable in the workspace, clearing documents before a project switch.
    /// <para>关闭工作区全部文档型面板，用于工程切换前清理文档。</para>
    /// </summary>
    /// <param name="layout">The layout root (workspace <c>Layout</c>). <para>布局根（工作区 <c>Layout</c>）。</para></param>
    public void CloseAllDocuments(IDockable? layout)
    {
        var documents = WorkspaceSessionCapture.Enumerate(layout)
            .Where(WorkspaceSessionCapture.IsDocument)
            .Distinct()
            .ToList();
        foreach (var document in documents)
        {
            _closingDocuments.Remove(document);
            SafeRemoveDockable(document);
        }
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["ProjectExplorer"] = () => _projectExplorer ??= new ProjectExplorerViewModel { Id = "ProjectExplorer" },
            ["Properties"] = () => _properties ??= new PropertiesViewModel { Id = "Properties" },
            ["Device"] = DeviceViewModel.EnsureInstance
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = CreateHostWindow,
            ["ManagedHostWindow"] = CreateManagedHostWindow
        };

        DefaultHostWindowLocator = () => CreateHostWindow();

        base.InitLayout(layout);
    }

    private static HostWindow CreateHostWindow()
    {
        var options = new DockManagerOptions { IsDockingEnabled = true };
        var hwnd = new HostWindow(options);

        hwnd.ShowInTaskbar = true;
        hwnd.CanResize = true;
        return hwnd;
    }

    private static ManagedHostWindow CreateManagedHostWindow()
    {
        var options = new DockManagerOptions { IsDockingEnabled = true };
        return new ManagedHostWindow(options);
    }

    public override async void CloseDockable(IDockable? dockable)
    {
        if (dockable == null)
        {
            return;
        }

        if (!dockable.CanClose)
        {
            return;
        }

        if (!_closingDocuments.Add(dockable)) return;
        try
        {
            if (!await DocumentSaveService.ConfirmCloseAsync(dockable)) return;
            SafeRemoveDockable(dockable);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", $"Close failed: {ex.Message}");
        }
        finally { _closingDocuments.Remove(dockable); }
    }

    private void SafeRemoveDockable(IDockable dockable)
    {
        try
        {
            if (dockable == null)
            {
                return;
            }

            IDock? ownerDock = null;
            if (dockable.Owner is IDock dock)
            {
                ownerDock = dock;
                if (dock.VisibleDockables != null)
                {
                    var list = dock.VisibleDockables.ToList();
                    if (list.Contains(dockable))
                    {
                        list.Remove(dockable);
                        dock.VisibleDockables = CreateList(list.ToArray());

                        if (dock.ActiveDockable == dockable)
                        {
                            dock.ActiveDockable = list.FirstOrDefault();
                        }
                    }
                }
            }

            dockable.Owner = null;

            if (ownerDock is not null)
            {
                WorkspacePanelService.Instance.OnDockContentsChanged(ownerDock);
            }
        }
        catch
        {

        }

        if (dockable is TerminalViewModel terminal)
        {
            _terminals?.Remove(terminal);
        }

        DocumentOpenRegistry
            .ByInstance(dockable)?.Cleanup?.Invoke(dockable);
    }
}