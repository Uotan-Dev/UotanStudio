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
        Assert.False(leftPane.IsCollapsable);

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
    public void MoveDockable_FloatingWindowThatStillHasContent_IsKept()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var root = BuildLayout(factory);

        var toolDock = Assert.IsAssignableFrom<IDockable>(factory.FindDockable(root, d => d.Id == "LeftPane"));
        factory.FloatDockable(toolDock);

        Assert.Single(root.Windows!);
    }

    private static IRootDock BuildLayout(WorkspaceDockFactory factory)
    {
        var toolDock = new ToolDock
        {
            Id = "LeftPane",
            IsCollapsable = false,
            VisibleDockables = factory.CreateList<IDockable>(new Tool { Id = "ToolA" })
        };
        toolDock.ActiveDockable = toolDock.VisibleDockables![0];
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
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