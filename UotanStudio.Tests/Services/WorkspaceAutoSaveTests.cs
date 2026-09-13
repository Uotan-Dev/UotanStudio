using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Dock.Model.Core;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace state")]
public sealed class WorkspaceAutoSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-autosave-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    public WorkspaceAutoSaveTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Coordinator_DisabledDoesNotSave_AndToggleTakesEffect()
    {
        var count = 0;
        var saver = new AutoSaveCoordinator(() => { count++; return Task.CompletedTask; });
        await saver.TickAsync();
        await saver.FlushAsync();
        Assert.Equal(0, count);
        saver.Enabled = true;
        await saver.TickAsync();
        Assert.Equal(1, count);
        saver.Enabled = false;
        await saver.FlushAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Coordinator_CoalescesTicks_AndFlushSavesLatestState()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<int>();
        var current = 1;
        var saver = new AutoSaveCoordinator(async () =>
        {
            values.Add(current);
            if (values.Count == 1) { started.SetResult(); await release.Task; }
        })
        { Enabled = true };
        var first = saver.TickAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        Assert.Same(first, saver.TickAsync());
        current = 2;
        var flushed = saver.FlushAsync();
        Assert.False(flushed.IsCompleted);
        release.SetResult();
        await flushed.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        Assert.Equal([1, 2], values);
    }

    [Fact]
    public async Task Coordinator_WaitForIdle_DoesNotSavePendingEdits()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<string>();
        var content = "before exit";
        var saver = new AutoSaveCoordinator(async () =>
        {
            saved.Add(content);
            await release.Task;
        })
        { Enabled = true };
        var saving = saver.TickAsync();
        content = "unsaved edit";
        var waiting = saver.WaitForIdleAsync();
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        await saving;
        await saver.WaitForIdleAsync();
        Assert.Equal(["before exit"], saved);
    }

    [Fact]
    public async Task Coordinator_RetriesAfterFailure()
    {
        var calls = 0;
        var saver = new AutoSaveCoordinator(() => ++calls == 1
            ? Task.FromException(new IOException("test")) : Task.CompletedTask)
        { Enabled = true };
        await Assert.ThrowsAsync<IOException>(saver.TickAsync);
        await saver.TickAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Session_RoundTripsPanelsDocumentsAndDraftParameters_WithoutRewritingUnchangedState()
    {
        var path = Path.Combine(_root, "state", "workspace-session.json");
        var snapshot = new WorkspaceSessionSnapshot
        {
            Panels = new() { [WorkspacePanelService.ProjectExplorer] = false, [WorkspacePanelService.ImagesView] = true },
            Documents = [new() { Kind = "Text", RelativePath = Path.Combine(_root, "outside.json"), OpenMode = (int)EditorOpenMode.Text, IsActive = true }],
            Docks = [new() { Id = "LeftPane", Proportion = 0.3, ActiveId = "ImagesView" }],
            DraftWorkflow = new() { Nodes = [new() { InstanceId = "test", TypeKey = "Data.ReadFile", InputInitialValues = new() { ["Path"] = "boot.img" } }] }
        };
        Assert.True(await AtomicJsonFile.WriteIfChangedAsync(path, snapshot, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot, Cancellation));
        var time = File.GetLastWriteTimeUtc(path);
        Assert.False(await AtomicJsonFile.WriteIfChangedAsync(path, snapshot, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot, Cancellation));
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        await using var input = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync(input, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot, Cancellation);
        Assert.NotNull(loaded);
        Assert.False(loaded.Panels[WorkspacePanelService.ProjectExplorer]);
        Assert.True(loaded.Panels[WorkspacePanelService.ImagesView]);
        Assert.True(Assert.Single(loaded.Documents).IsActive);
        Assert.Equal((int)EditorOpenMode.Text, loaded.Documents[0].OpenMode);
        Assert.Equal(0.3, Assert.Single(loaded.Docks).Proportion);
        Assert.Equal("boot.img", Assert.Single(loaded.DraftWorkflow!.Nodes).InputInitialValues!["Path"]);
    }

    [Fact]
    public async Task CancelledSnapshot_DoesNotReplaceExistingFile()
    {
        var path = Path.Combine(_root, "session.json");
        File.WriteAllText(path, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicJsonFile.WriteIfChangedAsync(
            path, new WorkspaceSessionSnapshot(), AppSettingsJsonContext.Default.WorkspaceSessionSnapshot, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void PanelVisibility_RestoresClosedAndOpenedPanels_AndTracksMovedOwner()
    {
        var panels = new WorkspacePanelService();
        var tool = new Tool { Id = WorkspacePanelService.ImagesView };
        var original = new ToolDock { VisibleDockables = new ObservableCollection<IDockable> { tool } };
        var moved = new ToolDock { VisibleDockables = new ObservableCollection<IDockable>() };
        tool.Owner = original;
        panels.Register(WorkspacePanelService.ImagesView, tool, original, true);
        var snapshot = panels.CaptureVisibility();
        panels.Hide(WorkspacePanelService.ImagesView);
        Assert.False(panels.IsPanelVisible(WorkspacePanelService.ImagesView));
        panels.RestoreVisibility(snapshot);
        Assert.True(panels.IsPanelVisible(WorkspacePanelService.ImagesView));
        original.VisibleDockables.Clear();
        moved.VisibleDockables.Add(tool);
        tool.Owner = moved;
        Assert.True(panels.CaptureVisibility()[WorkspacePanelService.ImagesView]);
        panels.Hide(WorkspacePanelService.ImagesView);
        Assert.Empty(moved.VisibleDockables);
        Assert.False(panels.CaptureVisibility()[WorkspacePanelService.ImagesView]);
    }

    [Fact]
    public void RestoreVisibility_ReaddsHiddenPanel_WithoutChangingActivePanel()
    {
        var panels = new WorkspacePanelService();
        var explorer = new Tool { Id = WorkspacePanelService.ProjectExplorer };
        var setting = new Tool { Id = WorkspacePanelService.ProjectSetting };
        var left = new ToolDock
        {
            VisibleDockables = new ObservableCollection<IDockable> { explorer },
            ActiveDockable = explorer
        };
        explorer.Owner = left;
        panels.Register(WorkspacePanelService.ProjectExplorer, explorer, left, true);
        panels.Register(WorkspacePanelService.ProjectSetting, setting, left, false);
        Assert.False(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.Same(explorer, left.ActiveDockable);
        panels.RestoreVisibility(new Dictionary<string, bool>
        {
            [WorkspacePanelService.ProjectSetting] = true,
            [WorkspacePanelService.ProjectExplorer] = true
        });
        Assert.True(panels.IsPanelVisible(WorkspacePanelService.ProjectSetting));
        Assert.Same(explorer, left.ActiveDockable);
        Assert.Equal([WorkspacePanelService.ProjectExplorer, WorkspacePanelService.ProjectSetting],
            left.VisibleDockables.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Capture_IncludesExternalDocumentsAndExplicitEditorMode_InTabOrder()
    {
        var text = new FileViewModel { FilePath = Path.Combine(_root, "external.json"), OpenMode = EditorOpenMode.Text };
        var hex = new HexEditorViewModel { FilePath = Path.Combine(_root, "image.img") };
        var dock = new DocumentDock { Id = "DocumentsPane", VisibleDockables = new ObservableCollection<IDockable> { text, hex }, ActiveDockable = text };
        text.Owner = dock;
        hex.Owner = dock;
        var docs = WorkspaceSessionCapture.Documents(dock, Path.Combine(_root, "project"));
        Assert.Equal(["Text", "Hex"], docs.Select(d => d.Kind));
        Assert.Equal(Path.GetFullPath(text.FilePath), Path.GetFullPath(docs[0].RelativePath, Path.Combine(_root, "project")));
        Assert.True(docs[0].IsActive);
        Assert.False(docs[1].IsActive);
        Assert.Equal((int)EditorOpenMode.Text, docs[0].OpenMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextSave_PreservesEncodingAndWritesModifiedContent(bool utf8Bom)
    {
        var path = Path.Combine(_root, "config.json");
        Encoding encoding = new UTF8Encoding(utf8Bom);
        await File.WriteAllTextAsync(path, "original", encoding, Cancellation);
        var document = new FileViewModel();
        await document.LoadContentAsync(path, EditorOpenMode.Text);
        Assert.False(document.IsBinary);
        document.Text = "edited";
        Assert.True(document.IsDirty);
        await document.SaveAsync();
        Assert.False(document.IsDirty);
        Assert.Equal("edited", await File.ReadAllTextAsync(path, Cancellation));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes("edited")), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task TextSave_ExternalChangesAreNotOverwritten()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "original");
        var document = new FileViewModel();
        await document.LoadContentAsync(path, EditorOpenMode.Text);
        document.Text = "mine";
        File.WriteAllText(path, "external");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        await Assert.ThrowsAsync<IOException>(document.SaveAsync);
        Assert.True(document.IsDirty);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [Fact]
    public async Task TextSave_EditsDuringWriteRemainDirty()
    {
        var path = Path.Combine(_root, "config.txt");
        File.WriteAllText(path, "original");
        var document = new FileViewModel();
        await document.LoadContentAsync(path, EditorOpenMode.Text);
        document.Text = new string('a', 1024 * 1024);
        var save = document.SaveAsync();
        document.Text = "newer";
        await save;
        Assert.True(document.IsDirty);
        await document.SaveAsync();
        Assert.False(document.IsDirty);
        Assert.Equal("newer", File.ReadAllText(path));
    }

    [Fact]
    public async Task RestoreDocuments_RetainsTabOrderModeAndActiveTab_SkipsMissingFiles()
    {
        var first = Path.Combine(_root, "first.img");
        var second = Path.Combine(_root, "second.json");
        File.WriteAllText(first, "editable text");
        File.WriteAllText(second, "{}");
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        await workspace.RestoreDocumentsAsync([
            new() { Kind = "Text", RelativePath = first, OpenMode = (int)EditorOpenMode.Text, IsActive = true },
            new() { Kind = "Text", RelativePath = Path.Combine(_root, "missing.txt") },
            new() { Kind = "Text", RelativePath = second, OpenMode = (int)EditorOpenMode.Text }
        ], _root);
        var dock = workspace.FindDocumentsPanePublic()!;
        var documents = dock.VisibleDockables!.OfType<FileViewModel>().ToList();
        Assert.Equal([first, second], documents.Select(d => d.FilePath));
        Assert.Equal(EditorOpenMode.Text, documents[0].OpenMode);
        Assert.Same(documents[0], dock.ActiveDockable);
        Assert.All(documents, d => Assert.False(d.IsDirty));
    }

    private sealed class TestWorkspaceFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }

    [Fact]
    public async Task ProjectAutoSave_SavesInactiveTextAndNodeInputs_PreservesOtherSubprojects()
    {
        var solutionPath = Path.Combine(_root, "test.uotan");
        var root = Path.Combine(_root, "test");
        var firstProject = Path.Combine(root, "first", "first.uotanfw");
        var secondProject = Path.Combine(root, "second", "second.uotanfw");
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(firstProject, new() { ProjectName = "first" }, Cancellation));
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(secondProject, new()
        {
            ProjectName = "second",
            Images = [new() { RelativePath = "second-only.img", Format = "EXT4", SizeBytes = 4096 }]
        }, Cancellation));
        var untouched = File.ReadAllBytes(secondProject);
        var solution = new SolutionSnapshot
        {
            SolutionName = "test",
            SubProjects = [new() { Name = "first", RelativePath = "first/first.uotanfw" }, new() { Name = "second", RelativePath = "second/second.uotanfw" }]
        };
        ProjectContextService.Instance.OpenSolution(solutionPath, solution);
        var images = ImageViewModel.EnsureInstance();
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        var nodes = new NodeEditorViewModel();
        try
        {
            images.Images.Clear();
            images.Images.Add(new() { RelativePath = "first/images/boot.img", Format = "Boot", SizeBytes = 100 });
            var node = DataNodeViewModel.CreateFileReadNode();
            node.Input[0].InitialValue = "boot.img";
            nodes.Nodes.Add(node);
            var textPath = Path.Combine(_root, "config.json");
            var otherPath = Path.Combine(_root, "other.txt");
            File.WriteAllText(textPath, "original");
            File.WriteAllText(otherPath, "other");
            await workspace.OpenDocumentAsync(textPath, EditorOpenMode.Text);
            var edited = Assert.IsType<FileViewModel>(workspace.FindDocumentsPanePublic()!.ActiveDockable);
            edited.Text = "edited";
            await workspace.OpenDocumentAsync(otherPath, EditorOpenMode.Text);
            var operations = new ProjectOperationsService { CurrentProjectPath = solutionPath, WorkspaceVM = workspace, NodeEditorVM = nodes };
            var originalProject = File.ReadAllBytes(firstProject);
            await operations.AutoSaveAsync(persistProject: false);
            Assert.False(edited.IsDirty);
            Assert.Equal(originalProject, File.ReadAllBytes(firstProject));
            Assert.False(File.Exists(solutionPath));
            await operations.AutoSaveAsync(persistPageState: false);
            Assert.Null((await SolutionPersistenceService.LoadFirmwareProjectAsync(firstProject, Cancellation))!.OpenDocuments);
            await operations.AutoSaveAsync();
            Assert.False(edited.IsDirty);
            Assert.Equal("edited", File.ReadAllText(textPath));
            Assert.Equal(untouched, File.ReadAllBytes(secondProject));
            var saved = await SolutionPersistenceService.LoadNodeProjectAsync(ProjectContextService.Instance.NodeProjectPath!, Cancellation);
            Assert.Equal("boot.img", Assert.Single(saved!.Nodes).InputInitialValues!["Path"]);
            var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(firstProject, Cancellation);
            Assert.Equal(2, firmware!.OpenDocuments!.Count);
            Assert.Equal("first/images/boot.img", Assert.Single(firmware.Images).RelativePath);
            Assert.True(firmware.OpenDocuments[1].IsActive);
            workspace.FindDocumentsPanePublic()!.VisibleDockables!.Clear();
            await operations.AutoSaveAsync(persistPageState: false);
            Assert.Equal(2, (await SolutionPersistenceService.LoadFirmwareProjectAsync(firstProject, Cancellation))!.OpenDocuments!.Count);
        }
        finally
        {
            images.Images.Clear();
            ProjectContextService.Instance.CloseProject();
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(nodes);
        }
    }
}

[CollectionDefinition("Workspace state", DisableParallelization = true)]
public sealed class WorkspaceStateCollection;