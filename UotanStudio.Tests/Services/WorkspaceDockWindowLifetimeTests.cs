using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkspaceDockWindowLifetimeTests
{
    private sealed class HeadlessWorkspaceDockFactory() : WorkspaceDockFactory(new AiSettingsViewModel())
    {
        protected override IHostWindow CreateHostWindow() => null!;
    }

    [Fact]
    public void MoveDockable_DockingAWholePaneBack_RemovesTheEmptiedFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var leftPane = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "LeftPane"));
        Assert.True(leftPane.IsCollapsable);

        factory.FloatDockable(leftPane);
        var floatingRoot = Assert.Single(root.Windows!).Layout;
        Assert.NotNull(floatingRoot);

        var container = Assert.IsAssignableFrom<IDockable>(((IDock)floatingRoot!).ActiveDockable);
        var mainLayout = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "MainLayout"));

        factory.MoveDockable(Assert.IsAssignableFrom<IDock>(container.Owner), Assert.IsAssignableFrom<IDock>(mainLayout),
            container, null);

        Assert.Empty(root.Windows!);
        Assert.Contains(WorkspaceSessionCapture.Enumerate(root), d => d.Id == "LeftPane");
    }

    [Fact]
    public void DockToMainWindow_ReturnsFloatingDocumentAndClosesEmptiedWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var doc1 = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "Doc1"));
        Assert.False(factory.CanDockToMainWindow(doc1));

        factory.FloatDockable(doc1);
        Assert.Single(root.Windows!);
        Assert.True(factory.CanDockToMainWindow(doc1));

        factory.DockToMainWindow(doc1);

        Assert.Empty(root.Windows!);
        Assert.False(factory.CanDockToMainWindow(doc1));
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        Assert.Contains(doc1, documentsPane.VisibleDockables!);
    }

    [Fact]
    public void DockToMainWindow_ReturnsFloatingToolAndClosesEmptiedWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var toolA = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "ToolA"));
        Assert.False(factory.CanDockToMainWindow(toolA));

        factory.FloatDockable(toolA);
        Assert.Single(root.Windows!);
        Assert.True(factory.CanDockToMainWindow(toolA));

        factory.DockToMainWindow(toolA);

        Assert.Empty(root.Windows!);
        Assert.False(factory.CanDockToMainWindow(toolA));
        Assert.Contains(WorkspaceSessionCapture.Enumerate(root), d => ReferenceEquals(d, toolA));
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        Assert.DoesNotContain(toolA, documentsPane.VisibleDockables!);
    }

    private sealed class DualDocumentTool : Document, ITool
    {
    }

    [Fact]
    public void DockToMainWindow_KeepsToolAttributeForDualDocumentFloatedFromToolDock()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var leftPane = Assert.IsAssignableFrom<IToolDock>(factory.FindDockable(root, d => d.Id == "LeftPane"));
        var dual = new DualDocumentTool { Id = "DualTool" };
        factory.AddDockable(leftPane, dual);

        factory.FloatDockable(dual);
        Assert.Single(root.Windows!);

        factory.DockToMainWindow(dual);

        Assert.Empty(root.Windows!);
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        Assert.DoesNotContain(dual, documentsPane.VisibleDockables!);
        Assert.IsAssignableFrom<IToolDock>(dual.Owner);
    }

    [Fact]
    public void FloatDockable_DualDocumentFromDocumentDock_StaysDocumentDockAndReturnsToDocumentsPane()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var dual = new DualDocumentTool { Id = "DualDoc" };
        factory.AddDockable(documentsPane, dual);

        factory.FloatDockable(dual);
        Assert.Single(root.Windows!);
        Assert.IsAssignableFrom<IDocumentDock>(dual.Owner);

        factory.DockToMainWindow(dual);

        Assert.Empty(root.Windows!);
        Assert.Contains(dual, documentsPane.VisibleDockables!);
        Assert.Same(documentsPane, dual.Owner);
    }

    [Fact]
    public void RightColumn_FloatBothAndDockBack_RemountsRightColumnAndPanels()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);

        factory.FloatDockable(images);
        factory.FloatDockable(properties);
        Assert.Equal(2, root.Windows!.Count);

        factory.DockToMainWindow(images);
        // Simulate ProportionalStackPanel assigning CollapsedProportion=1.0 while alone in rightCol.
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);
        rightTop.Proportion = 1.0;
        rightTop.CollapsedProportion = 1.0;

        factory.DockToMainWindow(properties);

        Assert.Empty(root.Windows!);
        AssertRightColumnSplitIntact(root, images, properties);
    }

    [Fact]
    public void RightColumn_FloatWholePanesAndDockBack_RemountsBothWithoutOverwriting()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);
        var rightBottom = Assert.IsAssignableFrom<IToolDock>(properties.Owner);

        factory.FloatDockable(rightTop);
        factory.FloatDockable(rightBottom);
        Assert.Equal(2, root.Windows!.Count);

        factory.DockToMainWindow(images);
        rightTop.Proportion = 1.0;
        rightTop.CollapsedProportion = 1.0;

        factory.DockToMainWindow(properties);

        Assert.Empty(root.Windows!);
        AssertRightColumnSplitIntact(root, images, properties);
    }

    [Fact]
    public void RightColumn_FloatBothAndClose_ShowAndToggleFromMenuRemountsBoth()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);
        var rightBottom = Assert.IsAssignableFrom<IToolDock>(properties.Owner);

        factory.FloatDockable(rightTop);
        factory.FloatDockable(rightBottom);
        Assert.Equal(2, root.Windows!.Count);

        foreach (var window in root.Windows!.ToList())
        {
            factory.CloseWindow(window);
            if (root.Windows!.Contains(window))
            {
                factory.RemoveWindow(window);
            }
        }

        Assert.Empty(root.Windows!);
        Assert.False(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
        Assert.False(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));

        WorkspacePanelService.Instance.Show(WorkspacePanelService.ImagesView);
        rightTop.Proportion = 1.0;
        rightTop.CollapsedProportion = 1.0;

        WorkspacePanelService.Instance.Show(WorkspacePanelService.Properties);
        AssertRightColumnSplitIntact(root, images, properties);

        WorkspacePanelService.Instance.Hide(WorkspacePanelService.Properties);
        WorkspacePanelService.Instance.Show(WorkspacePanelService.Properties);
        AssertRightColumnSplitIntact(root, images, properties);
    }

    [Fact]
    public void RightColumn_FloatOneAndDockBack_ReplacesPromotedSiblingWithRightColumn()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);

        factory.FloatDockable(rightTop);
        Assert.Single(root.Windows!);

        factory.DockToMainWindow(images);

        Assert.Empty(root.Windows!);
        AssertRightColumnSplitIntact(root, images, properties);
    }

    [Fact]
    public void HideFromMenu_ClosesEmptiedFloatingWindowAndShowRemountsInMainWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);

        factory.FloatDockable(rightTop);
        Assert.Single(root.Windows!);

        WorkspacePanelService.Instance.Hide(WorkspacePanelService.ImagesView);

        Assert.Empty(root.Windows!);
        Assert.False(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));

        WorkspacePanelService.Instance.Show(WorkspacePanelService.ImagesView);

        Assert.Empty(root.Windows!);
        AssertRightColumnSplitIntact(root, images, properties);
    }

    private static void AssertRightColumnSplitIntact(IRootDock root, Tool images, Tool properties)
    {
        var mainLayout = Assert.IsAssignableFrom<IProportionalDock>(
            WorkspaceSessionCapture.Enumerate(root).Single(d => d.Id == "WorkspaceMainLayout"));
        var rightCol = Assert.IsAssignableFrom<IProportionalDock>(
            WorkspaceSessionCapture.Enumerate(root).Single(d => d.Id == "WorkspaceRightColumn"));

        Assert.Equal(Orientation.Vertical, rightCol.Orientation);
        Assert.False(rightCol.IsEmpty);
        Assert.Contains(rightCol, mainLayout.VisibleDockables!);
        Assert.DoesNotContain(images.Owner!, mainLayout.VisibleDockables!);
        Assert.DoesNotContain(properties.Owner!, mainLayout.VisibleDockables!);

        Assert.Equal(3, rightCol.VisibleDockables!.Count);
        var topPane = Assert.IsAssignableFrom<IToolDock>(rightCol.VisibleDockables[0]);
        Assert.IsAssignableFrom<IProportionalDockSplitter>(rightCol.VisibleDockables[1]);
        var bottomPane = Assert.IsAssignableFrom<IToolDock>(rightCol.VisibleDockables[2]);

        Assert.False(topPane.IsEmpty);
        Assert.False(bottomPane.IsEmpty);
        Assert.True(double.IsNaN(topPane.CollapsedProportion));
        Assert.Equal(0.35, bottomPane.CollapsedProportion);
        Assert.Contains(images, topPane.VisibleDockables!);
        Assert.Contains(properties, bottomPane.VisibleDockables!);
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));
    }

    private static IRootDock BuildRightColumnLayout(WorkspaceDockFactory factory, out Tool images, out Tool properties)
    {
        images = new Tool { Id = "ImagesView", CanClose = true };
        properties = new Tool { Id = "Properties", CanClose = true };

        var rightTop = new ToolDock
        {
            Id = "RightPaneTop",
            Proportion = double.NaN,
            ActiveDockable = images,
            VisibleDockables = factory.CreateList<IDockable>(images)
        };
        var rightBottom = new ToolDock
        {
            Id = "RightPane",
            Proportion = 0.35,
            ActiveDockable = properties,
            VisibleDockables = factory.CreateList<IDockable>(properties)
        };
        var rightCol = new ProportionalDock
        {
            Id = "WorkspaceRightColumn",
            Orientation = Orientation.Vertical,
            Proportion = 0.15,
            VisibleDockables = factory.CreateList<IDockable>(rightTop, new ProportionalDockSplitter(), rightBottom)
        };
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
            Proportion = double.NaN,
            VisibleDockables = factory.CreateList<IDockable>()
        };
        var centerCol = new ProportionalDock
        {
            Id = "WorkspaceCenterColumn",
            Orientation = Orientation.Vertical,
            Proportion = 0.7,
            VisibleDockables = factory.CreateList<IDockable>(documentDock)
        };
        var mainLayout = new ProportionalDock
        {
            Id = "WorkspaceMainLayout",
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = factory.CreateList<IDockable>(centerCol, new ProportionalDockSplitter(), rightCol)
        };
        var root = factory.CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        root.VisibleDockables = factory.CreateList<IDockable>(mainLayout);
        factory.InitLayout(root);

        WorkspacePanelService.Instance.RegisterMainRoot(root);
        WorkspacePanelService.Instance.Register(WorkspacePanelService.ImagesView, images, rightTop, visible: true);
        WorkspacePanelService.Instance.Register(WorkspacePanelService.Properties, properties, rightBottom, visible: true);
        return root;
    }

    private static IRootDock BuildLayout(WorkspaceDockFactory factory)
    {
        var toolDock = new ToolDock
        {
            Id = "LeftPane",
            VisibleDockables = factory.CreateList<IDockable>(new Tool { Id = "ToolA" })
        };
        toolDock.ActiveDockable = toolDock.VisibleDockables![0];
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            VisibleDockables = factory.CreateList<IDockable>(new Document { Id = "Doc1" })
        };
        documentDock.ActiveDockable = documentDock.VisibleDockables![0];
        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = factory.CreateList<IDockable>(toolDock, new ProportionalDockSplitter(), documentDock)
        };
        var root = factory.CreateRootDock();
        root.Id = "Root";
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        root.VisibleDockables = factory.CreateList<IDockable>(mainLayout);
        factory.InitLayout(root);
        return root;
    }
}