using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Pipeline.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Settings;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace panel state")]
public sealed class WorkspaceRestoreFocusTests : IDisposable
{
    public WorkspaceRestoreFocusTests() => WorkspacePanelService.Instance.ResetForTests();

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
    public async Task RestoreOpenDocument_DeferredOpen_KeepsTheCurrentActiveDocument()
    {
        var root = CreateSessionDirectory();
        try
        {
            var foreground = WriteDocument(root, "foreground.bin", [0x03, 0x04, 0x05]);
            var background = WriteDocument(root, "background.bin", [0x00, 0x01, 0x02]);
            var (workspace, documentDock) = CreateWorkspace();

            await workspace.RestoreOpenDocumentAsync(
                new OpenDocumentDto { Kind = "Hex", RelativePath = foreground, IsActive = true }, root);
            var active = Assert.IsAssignableFrom<IDockable>(documentDock.ActiveDockable);
            Assert.Equal(foreground, Assert.IsType<HexEditorViewModel>(active).FilePath);

            await workspace.RestoreOpenDocumentAsync(
                new OpenDocumentDto { Kind = "Hex", RelativePath = background }, root, activate: false);

            Assert.Equal(2, documentDock.VisibleDockables!.Count);
            Assert.Contains(documentDock.VisibleDockables!, dockable =>
                dockable is HexEditorViewModel document && document.FilePath == background);
            Assert.Same(active, documentDock.ActiveDockable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreOpenDocument_ActivatedOpen_MakesTheDocumentActive()
    {
        var root = CreateSessionDirectory();
        try
        {
            var foreground = WriteDocument(root, "foreground.bin", [0x03, 0x04, 0x05]);
            var background = WriteDocument(root, "background.bin", [0x00, 0x01, 0x02]);
            var (workspace, documentDock) = CreateWorkspace();

            await workspace.RestoreOpenDocumentAsync(
                new OpenDocumentDto { Kind = "Hex", RelativePath = foreground }, root);
            await workspace.RestoreOpenDocumentAsync(
                new OpenDocumentDto { Kind = "Hex", RelativePath = background }, root, activate: true);

            var active = Assert.IsAssignableFrom<IDockable>(documentDock.ActiveDockable);
            Assert.Equal(background, Assert.IsType<HexEditorViewModel>(active).FilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSessionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-restore-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteDocument(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (WorkspaceViewModel Workspace, DocumentDock DocumentDock) CreateWorkspace()
    {
        var factory = new HeadlessWorkspaceDockFactory();
        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            VisibleDockables = factory.CreateList<IDockable>()
        };
        var mainLayout = new ProportionalDock
        {
            Id = "WorkspaceMainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = factory.CreateList<IDockable>(documentDock)
        };
        var root = factory.CreateRootDock();
        root.Id = "Root";
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        root.VisibleDockables = factory.CreateList<IDockable>(mainLayout);
        factory.InitLayout(root);

        var workspace = new WorkspaceViewModel(factory);
        workspace.Layout = root;
        return (workspace, documentDock);
    }
}