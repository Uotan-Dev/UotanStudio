using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

public sealed class WorkspacePluginToolTests
{
    [Fact]
    public async Task PluginTool_ManagerRefresh_ShowsAvailableWorkflowTemplates()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var manager = new VisualPluginManager(temp.Path);
        using var tool = new PluginToolViewModel(manager);
        var package = VisualPluginPackageBuilder.Create("Patch plan", "tests.workspace-patch-plan",
            VisualPluginKind.WorkflowTemplate, [NodeTypeRegistry.Create("File.Binary.Read")!], []);

        await VisualPluginFile.SaveAsync(Path.Combine(temp.Path, "patch-plan.uotanplugin.json"), package,
            TestContext.Current.CancellationToken);
        manager.Refresh();

        var displayed = Assert.Single(tool.Plugins);
        Assert.Equal(package.Manifest.Id, displayed.Id);
        Assert.True(displayed.IsTemplate);
        Assert.True(displayed.IsAvailable);
        Assert.Same(displayed, tool.SelectedPlugin);
    }
}