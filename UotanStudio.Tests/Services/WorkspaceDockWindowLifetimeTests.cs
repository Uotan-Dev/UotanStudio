using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace panel state")]
public sealed class WorkspaceDockWindowLifetimeTests : IDisposable
{
    public WorkspaceDockWindowLifetimeTests() => WorkspacePanelService.Instance.ResetForTests();

    public void Dispose()
    {
        WorkspacePanelService.Instance.ResetForTests();
        GC.SuppressFinalize(this);
    }

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
    public void FloatDockable_Document_UsesUnownedWindowMode()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var doc1 = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "Doc1"));

        factory.FloatDockable(doc1);

        var floatingWindow = Assert.Single(root.Windows!);
        Assert.Equal(DockWindowOwnerMode.None, floatingWindow.OwnerMode);
    }

    [Fact]
    public void FloatDockable_Tool_KeepsDefaultOwnerMode()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var toolA = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "ToolA"));

        factory.FloatDockable(toolA);

        var floatingWindow = Assert.Single(root.Windows!);
        Assert.Equal(DockWindowOwnerMode.Default, floatingWindow.OwnerMode);
    }

    [Fact]
    public void MoveDockable_LastDocumentDraggedBackToMainWindow_ClosesEmptiedFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var mainDocumentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var doc1 = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "Doc1"));

        factory.FloatDockable(doc1);
        Assert.Single(root.Windows!);
        var floatingDocumentDock = Assert.IsAssignableFrom<IDocumentDock>(doc1.Owner);

        factory.MoveDockable(floatingDocumentDock, mainDocumentsPane, doc1, mainDocumentsPane.ActiveDockable);

        Assert.Empty(root.Windows!);
        Assert.Contains(doc1, mainDocumentsPane.VisibleDockables!);
        Assert.Same(mainDocumentsPane, doc1.Owner);
    }

    [Fact]
    public void SplitToDock_LastDocumentSplitBackToMainWindow_ClosesEmptiedFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var mainDocumentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var doc1 = Assert.IsAssignableFrom<IDocument>(factory.FindDockable(root, d => d.Id == "Doc1"));
        var doc2 = new Document { Id = "Doc2" };
        factory.AddDockable(mainDocumentsPane, doc2);

        factory.FloatDockable(doc1);
        Assert.Single(root.Windows!);

        var dockManager = new DockManager(new DockService());
        Assert.True(dockManager.ValidateDocument(doc1, mainDocumentsPane, DragAction.Move, DockOperation.Right, bExecute: true));

        Assert.Empty(root.Windows!);
        Assert.Contains(WorkspaceSessionCapture.Enumerate(root), d => ReferenceEquals(d, doc1));
    }

    [Fact]
    public void RemoveDockable_LastDocumentClosedInFloatingWindow_ClosesEmptiedFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var doc1 = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "Doc1"));

        factory.FloatDockable(doc1);
        Assert.Single(root.Windows!);

        factory.RemoveDockable(doc1, collapse: true);

        Assert.Empty(root.Windows!);
    }

    [Fact]
    public void FloatDockable_LastDocumentWithMainRootWindowAttached_PreservesMainDocumentsPaneAndClosesFloatingWindowOnReturn()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var mainWindow = factory.CreateDockWindow();
        mainWindow.Layout = root;
        factory.InitDockWindow(mainWindow, root, null);
        root.Window = mainWindow;

        var mainDocumentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var doc1 = Assert.IsAssignableFrom<IDocument>(factory.FindDockable(root, d => d.Id == "Doc1"));

        factory.FloatDockable(doc1);

        var floatingWindow = Assert.Single(root.Windows!);
        Assert.Equal(DockWindowOwnerMode.None, floatingWindow.OwnerMode);
        Assert.Same(mainDocumentsPane, factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        Assert.False(mainDocumentsPane.IsCollapsable);
        Assert.Empty(mainDocumentsPane.VisibleDockables!);

        var floatingDocumentDock = Assert.IsAssignableFrom<IDocumentDock>(doc1.Owner);
        Assert.True(floatingDocumentDock.IsCollapsable);

        factory.MoveDockable(floatingDocumentDock, mainDocumentsPane, doc1, null);

        Assert.Empty(root.Windows!);
        Assert.Contains(doc1, mainDocumentsPane.VisibleDockables!);
        Assert.Same(mainDocumentsPane, doc1.Owner);
        Assert.False(mainDocumentsPane.IsCollapsable);
    }

    [Fact]
    public void PinDockable_RightToolPanes_PinToRightEdgePreviewAndRestoreToOriginalSlots()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);

        factory.PinDockable(images);
        factory.PinDockable(properties);

        Assert.True(root.LeftPinnedDockables is null or { Count: 0 });
        Assert.NotNull(root.RightPinnedDockables);
        Assert.Contains(images, root.RightPinnedDockables!);
        Assert.Contains(properties, root.RightPinnedDockables!);

        factory.PreviewPinnedDockable(images);
        Assert.NotNull(root.PinnedDock);
        Assert.Equal(Alignment.Right, root.PinnedDock!.Alignment);
        Assert.Same(images, root.PinnedDock.ActiveDockable);

        factory.UnpinDockable(images);
        factory.UnpinDockable(properties);

        Assert.Empty(root.RightPinnedDockables!);
        Assert.Null(root.PinnedDock);
        var rightTop = Assert.IsAssignableFrom<IToolDock>(images.Owner);
        var rightBottom = Assert.IsAssignableFrom<IToolDock>(properties.Owner);
        Assert.Equal("RightPaneTop", rightTop.Id);
        Assert.Equal("RightPane", rightBottom.Id);
        Assert.Equal(Alignment.Right, rightTop.Alignment);
        Assert.Equal(Alignment.Right, rightBottom.Alignment);
        Assert.Equal(0.35, rightBottom.Proportion);
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));
    }

    [Fact]
    public void PinDockable_FloatingTool_DoesNotPinIntoFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var toolA = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "ToolA"));

        factory.FloatDockable(toolA);
        var floatingRoot = Assert.Single(root.Windows!).Layout!;

        factory.PinDockable(toolA);

        Assert.False(factory.IsDockablePinned(toolA));
        Assert.True(floatingRoot.LeftPinnedDockables is null or { Count: 0 });
        Assert.Contains(WorkspaceSessionCapture.Enumerate(floatingRoot), d => ReferenceEquals(d, toolA));
    }

    [Fact]
    public void Show_PinnedPanel_UnpinsAndRestoresWithoutDuplicating()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);

        factory.PinDockable(images);
        Assert.True(factory.IsDockablePinned(images));

        WorkspacePanelService.Instance.Show(WorkspacePanelService.ImagesView);

        Assert.False(factory.IsDockablePinned(images));
        Assert.True(root.RightPinnedDockables is null or { Count: 0 });
        Assert.Equal("RightPaneTop", images.Owner?.Id);
        Assert.Equal("RightPane", properties.Owner?.Id);
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));
    }

    [Fact]
    public void DockAsDocument_FloatingTool_MovesToMainDocumentsPaneAndClosesFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var toolA = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "ToolA"));
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));

        factory.FloatDockable(toolA);
        Assert.Single(root.Windows!);

        factory.DockAsDocument(toolA);

        Assert.Empty(root.Windows!);
        Assert.Contains(toolA, documentsPane.VisibleDockables!);
        Assert.Same(documentsPane, toolA.Owner);
    }

    [Fact]
    public void DockAsTool_FloatingDocument_MovesToMainToolDockAndClosesFloatingWindow()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var doc1 = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "Doc1"));

        factory.FloatDockable(doc1);
        Assert.Single(root.Windows!);

        factory.DockAsTool(doc1);

        Assert.Empty(root.Windows!);
        var toolDock = Assert.IsAssignableFrom<IToolDock>(doc1.Owner);
        Assert.Contains(doc1, toolDock.VisibleDockables!);
    }

    [Fact]
    public void TogglePreviewPinnedDockable_ClickedTwice_ExpandsThenCollapses()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out _);

        factory.PinDockable(images);
        Assert.Null(root.PinnedDock);

        factory.TogglePreviewPinnedDockable(images);
        Assert.NotNull(root.PinnedDock);
        Assert.Same(images, root.PinnedDock!.ActiveDockable);

        factory.TogglePreviewPinnedDockable(images);
        Assert.Null(root.PinnedDock);
    }

    [Fact]
    public void ResetToDefaultLayout_ClosesFloatingWindowsUnpinsRestoresProportionsAndKeepsDocuments()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out var properties);
        var mainDocumentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var doc1 = new Document { Id = "Doc1" };
        factory.AddDockable(mainDocumentsPane, doc1);

        factory.FloatDockable(doc1);
        factory.PinDockable(images);
        WorkspacePanelService.Instance.Hide(WorkspacePanelService.Properties);
        Assert.Single(root.Windows!);
        Assert.True(factory.IsDockablePinned(images));
        Assert.False(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));

        var resetRoot = factory.ResetToDefaultLayout(root);
        try
        {
            Assert.Empty(root.Windows!);
            Assert.True(resetRoot.Windows is null or { Count: 0 });
            Assert.True(resetRoot.LeftPinnedDockables is null or { Count: 0 });
            Assert.True(resetRoot.RightPinnedDockables is null or { Count: 0 });
            Assert.True(resetRoot.BottomPinnedDockables is null or { Count: 0 });

            var resetDocumentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(resetRoot, d => d.Id == "DocumentsPane"));
            Assert.Contains(doc1, resetDocumentsPane.VisibleDockables!);
            Assert.Same(resetDocumentsPane, doc1.Owner);

            var leftPane = Assert.IsAssignableFrom<IToolDock>(factory.FindDockable(resetRoot, d => d.Id == "LeftPane"));
            var rightTop = Assert.IsAssignableFrom<IToolDock>(factory.FindDockable(resetRoot, d => d.Id == "RightPaneTop"));
            var rightBottom = Assert.IsAssignableFrom<IToolDock>(factory.FindDockable(resetRoot, d => d.Id == "RightPane"));
            var bottomPane = Assert.IsAssignableFrom<IToolDock>(factory.FindDockable(resetRoot, d => d.Id == "BottomPane"));
            Assert.Equal(Alignment.Left, leftPane.Alignment);
            Assert.Equal(0.15, leftPane.Proportion);
            Assert.Equal(Alignment.Right, rightTop.Alignment);
            Assert.Equal(Alignment.Right, rightBottom.Alignment);
            Assert.Equal(0.35, rightBottom.Proportion);
            Assert.Equal(Alignment.Bottom, bottomPane.Alignment);
            Assert.Equal(0.25, bottomPane.Proportion);

            Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ProjectExplorer));
            Assert.False(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ProjectSetting));
            Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
            Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.Properties));
            Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.WorkspaceLog));
            Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.AdbLog));
        }
        finally
        {
            factory.DisposeTerminal();
            factory.ProjectExplorer?.Dispose();
        }
    }

    [Fact]
    public void PinDockable_ToolAboveDocumentArea_PinsToLeftInsteadOfTop()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var topTool = new Tool { Id = "TopSplitTool", CanPin = true };
        var topToolDock = new ToolDock
        {
            Id = "TopSplitPane",
            Alignment = Alignment.Top,
            ActiveDockable = topTool,
            VisibleDockables = factory.CreateList<IDockable>(topTool)
        };
        factory.SplitToDock(documentsPane, topToolDock, DockOperation.Top);

        factory.PinDockable(topTool);

        Assert.True(root.TopPinnedDockables is null or { Count: 0 });
        Assert.NotNull(root.LeftPinnedDockables);
        Assert.Contains(topTool, root.LeftPinnedDockables!);
    }

    [Fact]
    public void PinDockable_ToolMovedToRightOfDocumentArea_PinsToRightEdge()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));
        var movedTool = new Tool { Id = "MovedRightTool", CanPin = true };
        var rightSplitDock = new ToolDock
        {
            Id = "MovedRightPane",
            Alignment = Alignment.Bottom,
            ActiveDockable = movedTool,
            VisibleDockables = factory.CreateList<IDockable>(movedTool)
        };
        factory.SplitToDock(documentsPane, rightSplitDock, DockOperation.Right);

        factory.PinDockable(movedTool);

        Assert.True(root.BottomPinnedDockables is null or { Count: 0 });
        Assert.True(root.TopPinnedDockables is null or { Count: 0 });
        Assert.NotNull(root.RightPinnedDockables);
        Assert.Contains(movedTool, root.RightPinnedDockables!);
    }

    [Fact]
    public void UnpinDockable_RightToolMovedToCenterThenPinned_RestoresVisiblyInLayoutTree()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildRightColumnLayout(factory, out var images, out _);
        var documentsPane = Assert.IsAssignableFrom<IDocumentDock>(factory.FindDockable(root, d => d.Id == "DocumentsPane"));

        var dockManager = new DockManager(new DockService());
        Assert.True(dockManager.ValidateTool(images, documentsPane, DragAction.Move, DockOperation.Top, bExecute: true));

        var centerToolDock = Assert.IsAssignableFrom<IToolDock>(images.Owner);
        Assert.Contains(WorkspaceSessionCapture.Enumerate(root), d => ReferenceEquals(d, centerToolDock));

        factory.PinDockable(images);
        Assert.True(factory.IsDockablePinned(images));
        Assert.Contains(images, root.LeftPinnedDockables!);

        factory.UnpinDockable(images);

        Assert.False(factory.IsDockablePinned(images));
        Assert.Same(centerToolDock, images.Owner);
        Assert.Contains(images, centerToolDock.VisibleDockables!);
        Assert.Contains(WorkspaceSessionCapture.Enumerate(root), d => ReferenceEquals(d, images));
        Assert.True(WorkspacePanelService.Instance.IsPanelVisible(WorkspacePanelService.ImagesView));
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
            Alignment = Alignment.Right,
            Proportion = double.NaN,
            ActiveDockable = images,
            VisibleDockables = factory.CreateList<IDockable>(images)
        };
        var rightBottom = new ToolDock
        {
            Id = "RightPane",
            Alignment = Alignment.Right,
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