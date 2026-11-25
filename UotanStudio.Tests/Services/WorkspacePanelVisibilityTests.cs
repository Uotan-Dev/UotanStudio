using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkspacePanelVisibilityTests
{
    private sealed class HeadlessWorkspaceDockFactory() : WorkspaceDockFactory(new AiSettingsViewModel())
    {
        protected override IHostWindow CreateHostWindow() => null!;
    }

    [Fact]
    public void HideDockable_KeepsThePanelAndItsSourcePane()
    {
        var (factory, root, toolDock, tool) = BuildLayout();

        factory.HideDockable(tool);

        Assert.DoesNotContain(tool, toolDock.VisibleDockables!);
        Assert.Contains(tool, root.HiddenDockables!);
        Assert.Same(toolDock, tool.OriginalOwner);
    }

    [Fact]
    public void RestoreDockable_ReturnsThePanelToThePaneItCameFrom()
    {
        var (factory, root, toolDock, tool) = BuildLayout();

        factory.HideDockable(tool);
        factory.RestoreDockable(tool);

        Assert.Empty(root.HiddenDockables!);
        Assert.Contains(tool, toolDock.VisibleDockables!);
        Assert.Same(toolDock, tool.Owner);
    }

    [Fact]
    public void HideAndRestore_RepeatedCycles_KeepTheLayoutStable()
    {
        var (factory, root, toolDock, tool) = BuildLayout();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            factory.HideDockable(tool);
            Assert.DoesNotContain(tool, toolDock.VisibleDockables!);

            factory.RestoreDockable(tool);
            Assert.Contains(tool, toolDock.VisibleDockables!);
        }

        Assert.Same(toolDock, tool.Owner);
        Assert.Single(toolDock.VisibleDockables!);
    }

    private static (WorkspaceDockFactory Factory, IRootDock Root, IToolDock ToolDock, IDockable Tool) BuildLayout()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var tool = new Tool { Id = "Panel" };
        var toolDock = new ToolDock
        {
            Id = "LeftPane",
            VisibleDockables = factory.CreateList<IDockable>(tool),
            ActiveDockable = tool
        };
        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = factory.CreateList<IDockable>(toolDock)
        };
        var root = factory.CreateRootDock();
        root.Id = "Root";
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        root.VisibleDockables = factory.CreateList<IDockable>(mainLayout);
        factory.InitLayout(root);
        return (factory, root, toolDock, tool);
    }
}