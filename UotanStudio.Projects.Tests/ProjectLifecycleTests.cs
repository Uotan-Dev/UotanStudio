using System.IO.Compression;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Projects.Tests;

public sealed class ProjectLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-lifecycle-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ProjectLifecycleTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task IndependentSessions_CreateSaveOpenExportAndCloseWithoutDesktop()
    {
        var first = new ProjectSession();
        var second = new ProjectSession();
        var firstPath = Path.Combine(_root, "first.uotan");
        var secondPath = Path.Combine(_root, "second.uotan");
        await Task.WhenAll(first.CreateNewSolutionAsync(firstPath, Token), second.CreateNewSolutionAsync(secondPath, Token));
        Assert.NotSame(first.Context, second.Context);
        Assert.NotSame(first.History, second.History);
        var data = Assert.IsType<ProjectDataWorkspace>(first.Workspace);
        data.Workflow.Nodes.Add(new() { InstanceId = "unknown-instance", TypeKey = "Vendor.Unknown", InputInitialValues = new() { ["Input"] = "opaque" } });
        data.Settings.ErofsCompLevel = 7;
        first.History.Record("record", new() { Description = "record" }, () => { }, () => { });
        Assert.True(await first.SaveAsync(cancellationToken: Token));
        Assert.True(await first.CloseAsync(cancellationToken: Token));
        Assert.False(first.Context.IsProjectOpen);
        Assert.Null(first.CurrentProjectPath);
        Assert.Empty(data.Workflow.Nodes);
        Assert.Equal(secondPath, second.Context.CurrentSolutionPath);
        Assert.True(await first.OpenProjectFromPathAsync(firstPath, Token));
        Assert.Equal("Vendor.Unknown", Assert.Single(data.Workflow.Nodes).TypeKey);
        Assert.Equal(7, data.Settings.ErofsCompLevel);
        Assert.Equal("record", Assert.Single(first.History.CaptureSnapshot()).Description);
        Assert.False(first.History.CanUndo); // A data-only reopen must not invent executable undo actions.
        var output = Path.Combine(first.Context.RootDirectory!, "export.zip");
        await first.ExportAsync(output, Token);
        using var archive = ZipFile.OpenRead(output);
        Assert.NotNull(archive.GetEntry("first/first.uotanfw"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("export.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switch_PersistsOldSettingsAndPreservesItsPageState()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Documents.Add(new() { Kind = "Hex", RelativePath = "first.bin", OpenMode = 2 });
        Assert.True(await session.SaveAsync(cancellationToken: Token));
        var firstFile = session.Context.FirmwareProjectPath!;
        await session.AddSubProjectAsync(Path.Combine(session.Context.RootDirectory!, "other", "other.uotanfw"), Token);
        data.Settings.ErofsCompLevel = 9;
        data.Documents.Clear();
        Assert.True(await session.SwitchSubProjectAsync("other", cancellationToken: Token));
        var first = await SolutionPersistenceService.LoadFirmwareProjectAsync(firstFile, Token);
        Assert.Equal(9, first!.Settings!.ErofsCompLevel);
        Assert.Equal("first.bin", Assert.Single(first.OpenDocuments!).RelativePath);
        Assert.Equal("other", session.Context.ActiveSubProject!.Name);
        Assert.True(await session.SwitchSubProjectAsync("sample", cancellationToken: Token));
        Assert.Equal(9, data.Settings.ErofsCompLevel);
    }

    [Fact]
    public async Task CancelledCloseAndSwitch_KeepStateAndDoNotNotify()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        var active = session.Context.ActiveSubProject;
        var notifications = 0;
        session.Changed += () => notifications++;
        session.BeforeProjectChangeAsync = () => Task.FromResult(false);
        Assert.False(await session.CloseAsync(cancellationToken: Token));
        Assert.False(await session.SwitchSubProjectAsync("anything", cancellationToken: Token));
        Assert.Same(active, session.Context.ActiveSubProject);
        Assert.Equal(0, notifications);
        session.BeforeProjectChangeAsync = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SaveAsAsync(Path.Combine(_root, "copy.uotan"), cancelled.Token));
        Assert.False(session.IsBusy);
        Assert.False(File.Exists(Path.Combine(_root, "copy.uotan")));
        Assert.True(await session.CloseAsync(cancellationToken: Token));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task WaitingOperationCancellation_DoesNotReleaseAnotherOperationsGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new WaitingWorkspace(entered, release);
        var session = new ProjectSession(workspace);
        var saving = session.AutoSaveAsync(persistProject: false, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var closing = session.CloseAsync(cancellationToken: cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
            Assert.True(session.IsBusy);
        }
        finally { release.TrySetResult(); }
        await saving;
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task LegacySolution_LoadsAndSavesAsVersionTwo()
    {
        var path = Path.Combine(_root, "legacy.uotan");
        var root = Path.ChangeExtension(path, null);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, """{"FormatVersion":1,"SolutionName":"legacy","FirmwareProjectPath":"legacy.uotanfw","NodeProjectPath":"legacy.uotannode"}""", Token);
        await File.WriteAllTextAsync(Path.Combine(root, "legacy.uotanfw"), """{"FormatVersion":1,"ProjectName":"legacy","Images":[],"Settings":{"ErofsCompLevel":6}}""", Token);
        var session = new ProjectSession();
        Assert.True(await session.OpenProjectFromPathAsync(path, Token));
        Assert.Equal(6, Assert.IsType<ProjectDataWorkspace>(session.Workspace).Settings.ErofsCompLevel);
        Assert.True(await session.SaveAsync(cancellationToken: Token));
        var saved = await SolutionPersistenceService.LoadSolutionAsync(path, Token);
        Assert.Equal(2, saved!.FormatVersion);
        Assert.Single(saved.SubProjects!);
    }

    [Fact]
    public async Task NoNodeEditor_SaveStillPersistsProvidedSnapshots()
    {
        var workspace = new WaitingWorkspace(new(), new());
        Assert.False(workspace.HasNodeEditor);
        var session = new ProjectSession(workspace);
        await session.CreateNewSolutionAsync(Path.Combine(_root, "headless.uotan"), Token);
        Assert.True(await session.SaveAsync(cancellationToken: Token));
        Assert.NotNull(await SolutionPersistenceService.LoadNodeProjectAsync(session.Context.NodeProjectPath!, Token));
    }

    [Fact]
    public async Task SaveAs_DataOnlyDocumentsKeepIdentityAcrossSubsequentSave()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "source.uotan"), Token);
        var originalRoot = session.Context.RootDirectory!;
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Documents.Add(new() { Kind = "Text", RelativePath = "external.txt", OpenMode = 1 });
        data.Images.Add(new() { RelativePath = "missing.img", Format = "unknown" });
        await session.SaveAsAsync(Path.Combine(_root, "copy.uotan"), Token);
        Assert.True(await session.SaveAsync(cancellationToken: Token));
        var saved = await SolutionPersistenceService.LoadFirmwareProjectAsync(session.Context.FirmwareProjectPath!, Token);
        var document = Assert.Single(saved!.OpenDocuments!);
        Assert.Equal(Path.Combine(originalRoot, "external.txt"), Path.GetFullPath(Path.Combine(session.Context.RootDirectory!, document.RelativePath)));
        Assert.True(await session.OpenProjectFromPathAsync(session.CurrentProjectPath!, Token));
        Assert.Equal("missing.img", Assert.Single(data.Images).RelativePath);
    }

    [Fact]
    public async Task FailedCreate_DoesNotReplaceLiveSessionOrClearItsData()
    {
        var session = new ProjectSession();
        var source = Path.Combine(_root, "source.uotan");
        await session.CreateNewSolutionAsync(source, Token);
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Workflow.Nodes.Add(new() { TypeKey = "keep" });
        var target = Path.Combine(_root, "blocked.uotan");
        Directory.CreateDirectory(Path.Combine(_root, "blocked", "blocked.uotannode"));
        await Assert.ThrowsAsync<IOException>(() => session.CreateNewSolutionAsync(target, Token));
        Assert.Equal(source, session.CurrentProjectPath);
        Assert.Equal(source, session.Context.CurrentSolutionPath);
        Assert.Equal("keep", Assert.Single(data.Workflow.Nodes).TypeKey);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task Open_ReconstructsHistoryOnlyAfterTargetContextIsActive()
    {
        var target = new ProjectSession();
        var targetPath = Path.Combine(_root, "target.uotan");
        await target.CreateNewSolutionAsync(targetPath, Token);
        target.History.Record("operation", new() { Description = "operation" }, () => { }, () => { });
        Assert.True(await target.SaveAsync(cancellationToken: Token));

        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "source.uotan"), Token);
        string? capturedPath = null;
        string? executedPath = null;
        session.History.ReconstructOperation = _ =>
        {
            capturedPath = session.Context.CurrentSolutionPath;
            var boundPath = capturedPath;
            return (() => executedPath = boundPath, () => { });
        };
        Assert.True(await session.OpenProjectFromPathAsync(targetPath, Token));
        Assert.Equal(targetPath, capturedPath);
        Assert.True(session.History.Undo());
        Assert.Equal(targetPath, executedPath);
    }

    private sealed class WaitingWorkspace(TaskCompletionSource entered, TaskCompletionSource release) : IProjectWorkspace
    {
        public bool IsImageOperationRunning => false;
        public bool HasNodeEditor => false;
        public void ClearNodes() { }
        public void ClearImages() { }
        public void ApplySettings(ProjectSettingsDto settings) { }
        public void RestoreImages(IEnumerable<ImageEntryDto> images) { }
        public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory) => Task.CompletedTask;
        public Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot) => Task.CompletedTask;
        public NodeProjectSnapshot CaptureWorkflow(string projectName) => new() { ProjectName = projectName };
        public List<ImageEntryDto> CaptureImages() => [];
        public ProjectSettingsDto CaptureSettings() => new();
        public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => [];
        public Task SaveActiveDocumentAsync() => Task.CompletedTask;
        public async Task AutoSaveDocumentsAsync() { entered.TrySetResult(); await release.Task; }
        public Task ImportFilesAsync(string[] paths) => Task.CompletedTask;
    }
}