using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Settings;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace panel state")]
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

    [Fact]
    public void Show_InitiallyHiddenProjectSetting_AddsAndActivatesInLeftPane()
    {
        var (factory, root, panels, leftPane, projectSetting, _, _) = BuildPanelServiceLayout();

        Assert.False(panels.ProjectSettingVisible);
        Assert.False(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.DoesNotContain(projectSetting, leftPane.VisibleDockables!);

        panels.Toggle(WorkspacePanelService.ProjectSetting);

        Assert.True(panels.ProjectSettingVisible);
        Assert.True(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.Contains(projectSetting, leftPane.VisibleDockables!);
        Assert.Same(projectSetting, leftPane.ActiveDockable);
        Assert.NotNull(factory.FindDockable(root, d => ReferenceEquals(d, projectSetting)));

        panels.Toggle(WorkspacePanelService.ProjectSetting);

        Assert.False(panels.ProjectSettingVisible);
        Assert.False(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.DoesNotContain(projectSetting, leftPane.VisibleDockables!);

        panels.Show(WorkspacePanelService.ProjectSetting);

        Assert.True(panels.ProjectSettingVisible);
        Assert.True(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.Contains(projectSetting, leftPane.VisibleDockables!);
        Assert.Same(projectSetting, leftPane.ActiveDockable);
    }

    [Fact]
    public void Show_ProjectSettingAfterLeftPaneCollapsed_ReattachesLeftPane()
    {
        var (factory, root, panels, leftPane, projectSetting, projectExplorer, pluginTool) = BuildPanelServiceLayout();

        factory.RemoveDockable(projectExplorer, collapse: true);
        factory.RemoveDockable(pluginTool, collapse: true);

        Assert.Null(factory.FindDockable(root, d => ReferenceEquals(d, leftPane)));

        panels.Show(WorkspacePanelService.ProjectSetting);

        Assert.True(panels.ProjectSettingVisible);
        Assert.True(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.NotNull(factory.FindDockable(root, d => ReferenceEquals(d, leftPane)));
        Assert.NotNull(factory.FindDockable(root, d => ReferenceEquals(d, projectSetting)));
        Assert.Same(projectSetting, leftPane.ActiveDockable);
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

    private static (
        WorkspaceDockFactory Factory,
        IRootDock Root,
        WorkspacePanelService Panels,
        IToolDock LeftPane,
        IDockable ProjectSetting,
        IDockable ProjectExplorer,
        IDockable PluginTool) BuildPanelServiceLayout()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var projectExplorer = new Tool { Id = "ProjectExplorer", CanClose = true };
        var pluginTool = new Tool { Id = WorkspacePanelService.PluginTool, CanClose = true };
        var projectSetting = new Tool { Id = "ProjectSetting", CanClose = true };
        var leftPane = new ToolDock
        {
            Id = "LeftPane",
            Proportion = 0.15,
            ActiveDockable = projectExplorer,
            VisibleDockables = factory.CreateList<IDockable>(projectExplorer, pluginTool)
        };
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            IsCollapsable = false,
            VisibleDockables = factory.CreateList<IDockable>()
        };
        var mainLayout = new ProportionalDock
        {
            Id = "WorkspaceMainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = factory.CreateList<IDockable>(
                leftPane,
                new ProportionalDockSplitter(),
                documentDock)
        };
        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        root.VisibleDockables = factory.CreateList<IDockable>(mainLayout);

        var panels = new WorkspacePanelService();
        panels.Register(WorkspacePanelService.ProjectExplorer, projectExplorer, leftPane, visible: true);
        panels.Register(WorkspacePanelService.ProjectSetting, projectSetting, leftPane, visible: false);
        panels.Register(WorkspacePanelService.PluginTool, pluginTool, leftPane, visible: true);
        factory.InitLayout(root);
        return (factory, root, panels, leftPane, projectSetting, projectExplorer, pluginTool);
    }
}