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

public class WorkspaceDockFactory : Factory, IWorkspaceDockFactory, IDockSplitPolicy
{
    private readonly AiSettingsViewModel _aiSettings;
    private ProjectExplorerViewModel? _projectExplorer;
    internal ProjectExplorerViewModel? ProjectExplorer => _projectExplorer;

    object? IWorkspaceDockFactory.ProjectExplorer => _projectExplorer;
    private PropertiesViewModel? _properties;
    private TerminalDockService? _terminals;
    private readonly HashSet<IDockable> _closingDocuments = [];
    private bool _syncingDocumentDocks;
    private bool _syncDocumentDocksPending;

    public WorkspaceDockFactory() : this(new AiSettingsViewModel()) { }

    public WorkspaceDockFactory(AiSettingsViewModel aiSettings) => _aiSettings = aiSettings;

    /// <summary>
    /// Decides whether splitting a dockable into <paramref name="target"/> should create a
    /// <see cref="DocumentDock"/> rather than a <see cref="ToolDock"/>.
    /// <para>判定将停靠项分栏到 <paramref name="target"/> 时是否创建 <see cref="DocumentDock"/> 而非 <see cref="ToolDock"/>。</para>
    /// </summary>
    /// <param name="source">The dockable being split. <para>正在分栏的源停靠项。</para></param>
    /// <param name="target">The target dock receiving the split. <para>接收分栏的目标停靠区。</para></param>
    /// <param name="operation">The requested split direction. <para>请求的分栏方向。</para></param>
    /// <returns><c>true</c> when the source is a document or the target is a document dock; otherwise <c>false</c>. <para>当源项为文档或目标为文档停靠区时返回 <c>true</c>，否则返回 <c>false</c>。</para></returns>
    public bool UseDocumentDock(IDockable source, IDock target, DockOperation operation)
        => source is IDocument || target is IDocumentDock;

    /// <inheritdoc/>
    public override IDocumentDock CreateDocumentDock()
    {
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = true,
            EnableGlobalDocking = false,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            EmptyContent = CreateEmptyDocumentContent()
        };
        WorkspacePanelService.Instance.RegisterLocalizedTitle(documentDock, "Dock_Documents");
        return documentDock;
    }

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
            EnableGlobalDocking = false,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            EmptyContent = CreateEmptyDocumentContent()
        };

        var rightToolsTop = new ToolDock
        {
            Id = "RightPaneTop",
            Title = "RightPaneTop",
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = imagesView,
            VisibleDockables = CreateList<IDockable>(imagesView)
        };
        if (aiAssistant is not null)
        {
            rightToolsTop.VisibleDockables.Add(aiAssistant);
        }

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            IsCollapsable = false,
            Proportion = 0.35,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties)
        };

        var leftTools = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            IsCollapsable = false,
            Proportion = 0.15,
            ActiveDockable = projectExplorer,

            VisibleDockables = CreateList<IDockable>(projectExplorer, pluginTool)
        };

        var bottomTools = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            IsCollapsable = false,
            Proportion = 0.25,
            ActiveDockable = output,
            VisibleDockables = CreateList<IDockable>(output, adbLog)
        };

        var centerColumn = new ProportionalDock
        {
            Id = "WorkspaceCenterColumn",
            IsCollapsable = false,
            Orientation = Orientation.Vertical,
            Proportion = 0.7,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                bottomTools
            )
        };

        var rightColumn = new ProportionalDock
        {
            Id = "WorkspaceRightColumn",
            IsCollapsable = false,
            Orientation = Orientation.Vertical,
            Proportion = 0.15,
            VisibleDockables = CreateList<IDockable>(
                rightToolsTop,
                new ProportionalDockSplitter(),
                rightTools
            )
        };

        var mainLayout = new ProportionalDock
        {
            Id = "WorkspaceMainLayout",
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                centerColumn,
                new ProportionalDockSplitter(),
                rightColumn
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
        {
            panels.Register(WorkspacePanelService.AiAssistant, aiAssistant, rightToolsTop, visible: true);
        }

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
        await CloseDockableAsync(dockable);
    }

    public override void CloseAllDockables(IDockable dockable)
    {
        if (dockable.Owner is not IDock dock || dock.VisibleDockables is not { } children)
        {
            return;
        }

        _ = CloseDockablesAsync(children.ToArray(), excluded: null);
    }

    public override void CloseOtherDockables(IDockable dockable)
    {
        if (dockable.Owner is not IDock dock || dock.VisibleDockables is not { } children)
        {
            return;
        }

        _ = CloseDockablesAsync(children.ToArray(), dockable);
    }

    private async Task CloseDockableAsync(IDockable? dockable)
    {
        if (dockable == null)
        {
            return;
        }

        if (!dockable.CanClose)
        {
            return;
        }

        if (!_closingDocuments.Add(dockable))
        {
            return;
        }

        try
        {
            if (!await DocumentSaveService.ConfirmCloseAsync(dockable))
            {
                return;
            }

            SafeRemoveDockable(dockable);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", $"Close failed: {ex.Message}");
        }
        finally
        {
            _closingDocuments.Remove(dockable);
        }
    }

    private async Task CloseDockablesAsync(IEnumerable<IDockable> dockables, IDockable? excluded)
    {
        foreach (var dockable in dockables.Reverse())
        {
            if (!ReferenceEquals(dockable, excluded))
            {
                await CloseDockableAsync(dockable);
            }
        }
    }

    /// <inheritdoc/>
    public override void DockAsDocument(IDockable dockable)
    {
        if (dockable?.Owner is IDock sourceDock && sourceDock is not IDocumentDock)
        {
            dockable.OriginalOwner = sourceDock;
        }

        var previousOwner = dockable?.Owner as IDock;
        var root = (previousOwner is not null ? FindRoot(previousOwner, _ => true) : null)
            ?? (dockable is not null ? FindRoot(dockable, _ => true) : null);
        if (dockable is not null)
        {
            ReleaseDockablePresentations(dockable);
            base.DockAsDocument(dockable);
        }

        if (previousOwner is not null)
        {
            WorkspacePanelService.Instance.OnDockContentsChanged(previousOwner);
        }

        if (dockable?.Owner is IDock currentOwner && !ReferenceEquals(currentOwner, previousOwner))
        {
            WorkspacePanelService.Instance.OnDockContentsChanged(currentOwner);
        }

        SyncDocumentDocks(root);
    }

    /// <summary>
    /// Docks the specified dockable into a tool dock in the active layout, restoring its previous
    /// tool dock when available or placing it into a workspace tool pane.
    /// <para>将指定停靠项停靠到当前布局的工具停靠区；若存在先前的工具停靠区则优先恢复，否则停靠到工作区工具面板区。</para>
    /// </summary>
    /// <param name="dockable">The dockable to dock as a tool window. <para>要停靠为工具窗口的停靠项。</para></param>
    public void DockAsTool(IDockable? dockable)
    {
        if (dockable?.Owner is not IDock sourceDock || sourceDock is IToolDock)
        {
            return;
        }

        var root = FindRoot(dockable, _ => true);
        if (root is null)
        {
            return;
        }

        var targetDock = ResolveTargetToolDock(dockable, root);
        if (targetDock is null || ReferenceEquals(sourceDock, targetDock))
        {
            return;
        }

        dockable.OriginalOwner = sourceDock;
        MoveDockable(sourceDock, targetDock, dockable, targetDock.ActiveDockable);
        SetActiveDockable(dockable);
        SetFocusedDockable(root, dockable);
        WorkspacePanelService.Instance.OnDockContentsChanged(sourceDock);
        WorkspacePanelService.Instance.OnDockContentsChanged(targetDock);
        SyncDocumentDocks(root);
    }

    /// <summary>
    /// Docks the dockable supplied as a command parameter into a tool dock in the active layout.
    /// <para>将作为命令参数传入的停靠项停靠到当前布局的工具停靠区。</para>
    /// </summary>
    /// <param name="parameter">The command parameter carrying the dockable. <para>携带停靠项的命令参数。</para></param>
    public void DockAsTool(object? parameter)
    {
        if (parameter is IDockable dockable)
        {
            DockAsTool(dockable);
        }
    }

    /// <inheritdoc/>
    public override void MoveDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable? targetDockable)
    {
        var root = FindRoot(targetDock, _ => true) ?? FindRoot(sourceDock, _ => true);
        if (sourceDock is IDocumentDock && !ReferenceEquals(sourceDock, targetDock))
        {
            UpdateDocumentDockCollapsibility(root);
        }

        if (!ReferenceEquals(sourceDock, targetDock))
        {
            ReleaseDockablePresentations(sourceDockable);
        }

        base.MoveDockable(sourceDock, targetDock, sourceDockable, targetDockable);
        SyncDocumentDocks(root);
        WorkspacePanelService.Instance.OnDockContentsChanged();
    }

    /// <inheritdoc/>
    public override void SplitToDock(IDock dock, IDockable dockable, DockOperation operation)
    {
        var root = FindRoot(dock, _ => true);
        ReleaseDockablePresentations(dockable);
        base.SplitToDock(dock, dockable, operation);
        SyncDocumentDocks(root);
        WorkspacePanelService.Instance.OnDockContentsChanged();
    }

    /// <inheritdoc/>
    public override void SplitToWindow(IDock dock, IDockable dockable, double x, double y, double width, double height, DockWindowOptions? options)
    {
        var root = FindRoot(dock, _ => true);
        if (dock is IDocumentDock)
        {
            UpdateDocumentDockCollapsibility(root);
        }

        ReleaseDockablePresentations(dockable);
        base.SplitToWindow(dock, dockable, x, y, width, height, options);
        SyncDocumentDocks(root);
        WorkspacePanelService.Instance.OnDockContentsChanged();
    }

    /// <inheritdoc/>
    public override void RemoveDockable(IDockable dockable, bool collapse)
    {
        var root = dockable.Owner is IDock ownerDock ? FindRoot(ownerDock, _ => true) : FindRoot(dockable, _ => true);
        ReleaseDockablePresentations(dockable);
        if (collapse)
        {
            UpdateDocumentDockCollapsibility(root);
            if ((dockable as IDocumentDock ?? dockable.Owner as IDocumentDock) is { } collapsingDocumentDock)
            {
                PreserveDocumentSplitSpace(collapsingDocumentDock);
            }
        }

        base.RemoveDockable(dockable, collapse);
        if (collapse)
        {
            SyncDocumentDocks(root);
        }
        WorkspacePanelService.Instance.OnDockContentsChanged();
    }

    private IToolDock? ResolveTargetToolDock(IDockable dockable, IRootDock root)
    {
        if (dockable.OriginalOwner is IToolDock originalToolDock
            && ReferenceEquals(FindRoot(originalToolDock, _ => true), root))
        {
            return originalToolDock;
        }

        if (WorkspacePanelService.Instance.FindRegisteredToolHost(dockable) is { } registeredToolDock
            && ReferenceEquals(FindRoot(registeredToolDock, _ => true), root))
        {
            return registeredToolDock;
        }

        string[] preferredPaneIds = ["BottomPane", "RightPane", "RightPaneTop", "LeftPane"];
        foreach (var id in preferredPaneIds)
        {
            if (FindDockable(root, d => d is IToolDock && d.Id == id) is IToolDock pane)
            {
                return pane;
            }
        }

        return FindDockable(root, static d => d is IToolDock) as IToolDock;
    }

    private void SafeRemoveDockable(IDockable dockable)
    {
        try
        {
            if (dockable == null)
            {
                return;
            }

            var ownerDock = dockable.Owner as IDock;
            var root = ownerDock is not null ? FindRoot(ownerDock, _ => true) : FindRoot(dockable, _ => true);
            if (ownerDock is not null && ownerDock.VisibleDockables is not null)
            {
                UpdateDocumentDockCollapsibility(root);
                RemoveDockable(dockable, collapse: true);
            }
            else if (ownerDock?.VisibleDockables != null)
            {
                var list = ownerDock.VisibleDockables.ToList();
                if (list.Contains(dockable))
                {
                    list.Remove(dockable);
                    ownerDock.VisibleDockables = CreateList(list.ToArray());

                    if (ownerDock.ActiveDockable == dockable)
                    {
                        ownerDock.ActiveDockable = list.FirstOrDefault();
                    }
                }
            }

            dockable.Owner = null;

            if (ownerDock is not null)
            {
                WorkspacePanelService.Instance.OnDockContentsChanged(ownerDock);
            }

            SyncDocumentDocks(root);
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

    private void UpdateDocumentDockCollapsibility(IRootDock? root)
    {
        if (root is null)
        {
            return;
        }

        var documentDocks = CollectDocumentDocks(root);
        var collapsable = documentDocks.Count > 1;
        foreach (var dock in documentDocks)
        {
            dock.IsCollapsable = collapsable;
            if (dock is DocumentDock concreteDock)
            {
                concreteDock.EnableGlobalDocking = false;
                concreteDock.EmptyContent ??= CreateEmptyDocumentContent();
            }
        }
    }

    private void SyncDocumentDocks(IRootDock? root)
    {
        if (root is null)
        {
            return;
        }

        if (_syncingDocumentDocks)
        {
            _syncDocumentDocksPending = true;
            return;
        }

        _syncingDocumentDocks = true;
        try
        {
            do
            {
                _syncDocumentDocksPending = false;
                var documentDocks = CollectDocumentDocks(root);
                while (documentDocks.Count > 1)
                {
                    foreach (var dock in documentDocks)
                    {
                        dock.IsCollapsable = true;
                    }

                    var emptyDock = documentDocks.FirstOrDefault(static d => d.VisibleDockables is null or { Count: 0 });
                    if (emptyDock is null)
                    {
                        break;
                    }

                    PreserveDocumentSplitSpace(emptyDock);
                    CollapseDock(emptyDock);
                    var updatedDocks = CollectDocumentDocks(root);
                    if (updatedDocks.Count >= documentDocks.Count)
                    {
                        break;
                    }

                    documentDocks = updatedDocks;
                }

                foreach (var dock in documentDocks)
                {
                    dock.Id = "DocumentsPane";
                    dock.IsCollapsable = documentDocks.Count > 1;
                    if (dock is DocumentDock concreteDock)
                    {
                        concreteDock.EnableGlobalDocking = false;
                        concreteDock.EmptyContent ??= CreateEmptyDocumentContent();
                    }
                }
            } while (_syncDocumentDocksPending);
        }
        finally
        {
            _syncingDocumentDocks = false;
        }
    }

    private static void ReleaseDockablePresentations(IDockable dockable)
    {
        foreach (var item in WorkspaceSessionCapture.Enumerate(dockable))
        {
            ViewLocator.ReleaseDeferredPresentation(item);
        }
    }

    private static void PreserveDocumentSplitSpace(IDockable collapsingDockable)
    {
        if (collapsingDockable is not IDocumentDock documentDock
            || documentDock.VisibleDockables is not { Count: 1 }
            || documentDock.Owner is not IProportionalDock owner
            || owner.VisibleDockables is not { } siblings)
        {
            return;
        }

        var recipient = FindSpaceRecipient(siblings, documentDock);
        var released = ResolveProportion(owner, documentDock);
        var current = recipient is null ? double.NaN : ResolveProportion(owner, recipient);
        if (recipient is null || !double.IsFinite(released) || !double.IsFinite(current))
        {
            return;
        }

        recipient.Proportion = released + current;
        recipient.CollapsedProportion = recipient.Proportion;

        if (owner.Owner is IProportionalDock parent && !double.IsFinite(owner.Proportion))
        {
            var ownerProportion = ResolveProportion(parent, owner);
            if (double.IsFinite(ownerProportion))
            {
                owner.Proportion = ownerProportion;
                owner.CollapsedProportion = ownerProportion;
            }
        }
    }

    private static IDockable? FindSpaceRecipient(IList<IDockable> siblings, IDocumentDock collapsingDock)
    {
        var index = siblings.IndexOf(collapsingDock);
        for (var distance = 1; index >= 0 && distance < siblings.Count; distance++)
        {
            if (index - distance >= 0 && IsDocumentHost(siblings[index - distance]))
            {
                return siblings[index - distance];
            }

            if (index + distance < siblings.Count && IsDocumentHost(siblings[index + distance]))
            {
                return siblings[index + distance];
            }
        }

        return null;
    }

    private static bool IsDocumentHost(IDockable dockable)
        => dockable is IDocumentDock
            || dockable is IDock { VisibleDockables: { } children } && children.Any(IsDocumentHost);

    private static double ResolveProportion(IProportionalDock owner, IDockable target)
    {
        if (double.IsFinite(target.Proportion))
        {
            return target.Proportion;
        }

        if (owner.VisibleDockables is not { } children)
        {
            return double.NaN;
        }

        var finite = 0.0;
        var unresolved = 0;
        foreach (var child in children)
        {
            if (child is IProportionalDockSplitter)
            {
                continue;
            }

            if (double.IsFinite(child.Proportion))
            {
                finite += child.Proportion;
            }
            else
            {
                unresolved++;
            }
        }

        return unresolved == 0 ? double.NaN : Math.Max(0, 1 - finite) / unresolved;
    }

    private static List<IDocumentDock> CollectDocumentDocks(IRootDock root)
    {
        var result = new List<IDocumentDock>();
        CollectDocumentDocksRecursive(root, result);
        return result;
    }

    private static void CollectDocumentDocksRecursive(IDockable? current, List<IDocumentDock> result)
    {
        if (current is null)
        {
            return;
        }

        if (current is IDocumentDock documentDock)
        {
            result.Add(documentDock);
        }

        if (current is IDock { VisibleDockables: { } children })
        {
            for (var i = 0; i < children.Count; i++)
            {
                CollectDocumentDocksRecursive(children[i], result);
            }
        }
    }
}