using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Ui.Workspace;
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