using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.UiAbstractions;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Nodes;

/// <summary>
/// Dock layout factory for the node editor, creating a layout with toolbox, canvas, properties panel, and execution log.
/// <para>节点编辑器的 Dock 布局工厂，创建包含工具箱、画布、属性面板和执行日志的布局。</para>
/// </summary>
public class NodeDockFactory : Factory, IDockReturnFactory
{
    private IRootDock? _mainRoot;
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
            Proportion = double.NaN,
            ActiveDockable = nodeCanvas,
            VisibleDockables = CreateList<IDockable>(nodeCanvas)
        };

        var leftTools = new ToolDock
        {
            Id = "NodeLeftPane",
            Title = LanguageService.Instance.GetString("Dock_NodeLeftPane"),
            Alignment = Alignment.Left,
            Proportion = 0.2,
            ActiveDockable = nodeToolbox,
            VisibleDockables = CreateList<IDockable>(nodeToolbox)
        };

        var rightTools = new ToolDock
        {
            Id = "NodeRightPane",
            Title = LanguageService.Instance.GetString("Dock_NodeRightPane"),
            Alignment = Alignment.Right,
            Proportion = 0.2,
            ActiveDockable = nodeProperties,
            VisibleDockables = CreateList<IDockable>(nodeProperties)
        };

        var bottomTools = new ToolDock
        {
            Id = "NodeBottomPane",
            Title = LanguageService.Instance.GetString("Dock_NodeBottomPane"),
            Alignment = Alignment.Bottom,
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
        _mainRoot = rootDock;
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
            [nameof(IDockWindow)] = CreateHostWindow,
            ["ManagedHostWindow"] = CreateManagedHostWindow
        };

        DefaultHostWindowLocator = () => CreateHostWindow();

        base.InitLayout(layout);
    }

    /// <summary>
    /// Creates the native window that hosts a floating dock window.
    /// <para>创建承载浮动停靠窗口的原生窗口。</para>
    /// </summary>
    /// <returns>The host window for a floating layout. <para>浮动布局的宿主窗口。</para></returns>
    protected virtual IHostWindow CreateHostWindow()
    {
        var options = new DockManagerOptions { IsDockingEnabled = true };
        return new DockHostWindow(options);
    }

    /// <inheritdoc/>
    public override void DockAsDocument(IDockable dockable)
    {
        if (dockable is null)
        {
            return;
        }

        if (IsDockablePinned(dockable))
        {
            UnpinDockable(dockable);
        }

        if (dockable.Owner is not IDock sourceDock)
        {
            return;
        }

        var sourceRoot = FindRoot(sourceDock, _ => true);
        var targetRoot = sourceRoot;
        var targetDocumentDock = sourceRoot is not null ? FindInDockTree<IDocumentDock>(sourceRoot) : null;
        if (targetDocumentDock is null && sourceRoot?.Window is { } floatingWindow)
        {
            var mainRoot = ResolveMainRoot(floatingWindow);
            if (mainRoot is not null)
            {
                targetRoot = mainRoot;
                targetDocumentDock = FindInDockTree<IDocumentDock>(mainRoot);
            }
        }

        if (targetDocumentDock is null || ReferenceEquals(sourceDock, targetDocumentDock))
        {
            return;
        }

        MoveDockable(sourceDock, targetDocumentDock, dockable, targetDocumentDock.VisibleDockables?.LastOrDefault());
        SetActiveDockable(dockable);
        if (targetRoot is not null)
        {
            SetFocusedDockable(targetRoot, dockable);
        }

        if (IsFloatingRoot(sourceRoot)
            && sourceRoot?.Window is { } emptiedWindow
            && !HasVisibleLeafDockables(emptiedWindow.Layout))
        {
            var mainRoot = ResolveMainRoot(emptiedWindow);
            CloseWindow(emptiedWindow);
            if (mainRoot?.Windows?.Contains(emptiedWindow) == true)
            {
                RemoveWindow(emptiedWindow);
            }
        }
    }

    /// <inheritdoc/>
    protected override void PinDockableCore(IDockable dockable, IRootDock rootDock, IToolDock? unpinDock)
    {
        var isPinned = IsDockablePinned(dockable, rootDock);
        if (!isPinned && IsFloatingRoot(rootDock))
        {
            return;
        }

        if (!isPinned && dockable.Owner is IToolDock sourceToolDock)
        {
            sourceToolDock.Alignment = ResolvePinAlignment(sourceToolDock, rootDock);
            if (dockable.OriginalOwner is not IToolDock originalTool || originalTool.Alignment != sourceToolDock.Alignment)
            {
                dockable.OriginalOwner = null;
            }
        }

        base.PinDockableCore(dockable, rootDock, unpinDock);
    }

    private static Alignment ResolvePinAlignment(IToolDock toolDock, IRootDock rootDock)
    {
        var current = (IDockable)toolDock;
        Alignment? verticalFallback = null;

        while (current.Owner is IDock parent)
        {
            if (parent is IProportionalDock { VisibleDockables: { Count: > 1 } children } proportional)
            {
                var currentIndex = children.IndexOf(current);
                var documentIndex = FindDocumentBranchIndex(children);
                if (currentIndex >= 0 && documentIndex >= 0 && currentIndex != documentIndex)
                {
                    if (proportional.Orientation == Orientation.Horizontal)
                    {
                        return currentIndex < documentIndex ? Alignment.Left : Alignment.Right;
                    }

                    verticalFallback ??= currentIndex > documentIndex ? Alignment.Bottom : Alignment.Left;
                }
            }

            if (ReferenceEquals(parent, rootDock))
            {
                break;
            }

            current = parent;
        }

        if (verticalFallback is { } vertical)
        {
            return vertical;
        }

        if (toolDock.Alignment is Alignment.Right or Alignment.Bottom)
        {
            return toolDock.Alignment;
        }

        return toolDock.Id switch
        {
            "NodeRightPane" => Alignment.Right,
            "NodeBottomPane" => Alignment.Bottom,
            _ => Alignment.Left
        };
    }

    private static int FindDocumentBranchIndex(IList<IDockable> children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (FindInDockTree<IDocumentDock>(children[i]) is not null)
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsFloatingRoot(IRootDock? root)
    {
        if (root?.Window is not { } window || ReferenceEquals(root, _mainRoot))
        {
            return false;
        }

        var mainRoot = ResolveMainRoot(window);
        return mainRoot is not null && !ReferenceEquals(mainRoot, root);
    }

    /// <inheritdoc/>
    public bool CanDockToMainWindow(IDockable? dockable)
    {
        if (dockable is null)
        {
            return false;
        }

        var sourceRoot = FindRoot(dockable, static _ => true);
        if (sourceRoot?.Window is not { } floatingWindow)
        {
            return false;
        }

        var mainRoot = ResolveMainRoot(floatingWindow);
        return mainRoot is not null && !ReferenceEquals(mainRoot, sourceRoot);
    }

    /// <inheritdoc/>
    public void DockToMainWindow(IDockable? dockable)
    {
        if (dockable?.Owner is not IDock sourceDock)
        {
            return;
        }

        var sourceRoot = FindRoot(dockable, static _ => true);
        if (sourceRoot?.Window is not { } floatingWindow)
        {
            return;
        }

        var mainRoot = ResolveMainRoot(floatingWindow);
        if (mainRoot is null || ReferenceEquals(mainRoot, sourceRoot))
        {
            return;
        }

        var keepAsTool = dockable is ITool && (dockable is not IDocument || sourceDock is IToolDock);
        IDock? targetDock = keepAsTool
            ? (dockable.OriginalOwner is IToolDock original && ContainsInDockTree(mainRoot, original)
                ? original
                : FindInDockTree<IToolDock>(mainRoot))
            : FindInDockTree<IDocumentDock>(mainRoot);

        if (targetDock is null || ReferenceEquals(sourceDock, targetDock))
        {
            return;
        }

        MoveDockable(sourceDock, targetDock, dockable, targetDock.ActiveDockable);
        SetActiveDockable(dockable);
        SetFocusedDockable(mainRoot, dockable);

        if (!HasVisibleLeafDockables(floatingWindow.Layout))
        {
            CloseWindow(floatingWindow);
            if (mainRoot.Windows?.Contains(floatingWindow) == true)
            {
                RemoveWindow(floatingWindow);
            }
        }
    }

    private IRootDock? ResolveMainRoot(IDockWindow floatingWindow)
    {
        var candidate = floatingWindow.Owner as IRootDock ?? _mainRoot;
        while (candidate?.Window?.Owner is IRootDock parentRoot && !ReferenceEquals(parentRoot, candidate))
        {
            candidate = parentRoot;
        }

        return candidate;
    }

    private static bool ContainsInDockTree(IDock root, IDockable target)
        => FindInDockTree<IDockable>(root, d => ReferenceEquals(d, target)) is not null;

    private static T? FindInDockTree<T>(IDockable? current, Func<T, bool>? predicate = null)
        where T : class, IDockable
    {
        if (current is null)
        {
            return null;
        }

        if (current is T matched && (predicate is null || predicate(matched)))
        {
            return matched;
        }

        if (current is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (FindInDockTree(child, predicate) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool HasVisibleLeafDockables(IDockable? root)
    {
        if (root is null)
        {
            return false;
        }

        if (root is ITool or IDocument)
        {
            return true;
        }

        if (root is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (HasVisibleLeafDockables(child))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ManagedHostWindow CreateManagedHostWindow()
    {
        var options = new DockManagerOptions { IsDockingEnabled = true };
        return new ManagedHostWindow(options);
    }

}