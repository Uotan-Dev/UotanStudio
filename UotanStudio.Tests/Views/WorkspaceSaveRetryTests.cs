using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class WorkspaceSaveRetryTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task SaveSessionAsync_PublicationFails_RetriesUnchangedSession()
    {
        await fixture.DispatchAsync(async () =>
        {
            using var temp = new TempDirectory();
            var path = Path.Combine(temp.Path, "session.json");
            Directory.CreateDirectory(path);
            using var workspace = new WorkspaceViewModel(new EmptyFactory());
            using var settings = new SettingsViewModel();
            var projects = new ProjectOperationsService();
            using var service = new WorkspaceAutoSaveService(workspace, settings, projects, path);
            await service.InitializeAsync();

            await service.SaveSessionAsync();
            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
            Directory.Delete(path);
            await service.SaveSessionAsync();

            Assert.True(File.Exists(path), string.Join('\n', UotanStudio.Services.LogService.Instance.Entries.Select(e => e.Message)));
            var before = File.GetLastWriteTimeUtc(path);
            await service.SaveSessionAsync();
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }, TestContext.Current.CancellationToken);
    }

    private sealed class EmptyFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout() => new RootDock { VisibleDockables = CreateList<IDockable>() };
    }
}