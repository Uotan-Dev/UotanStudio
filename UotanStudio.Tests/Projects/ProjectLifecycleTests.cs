using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Projects;

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
        Assert.True(await first.CreateNodeProjectAsync("flow", Token));
        data.Workflows["flow"].Nodes.Add(new() { InstanceId = "unknown-instance", TypeKey = "Vendor.Unknown", InputInitialValues = new() { ["Input"] = "opaque" } });
        data.Settings.ErofsCompLevel = 7;
        first.History.Record("record", new() { Description = "record" }, () => { }, () => { });
        Assert.True(await first.SaveAsync(cancellationToken: Token));
        Assert.True(await first.CloseAsync(cancellationToken: Token));
        Assert.False(first.Context.IsProjectOpen);
        Assert.Null(first.CurrentProjectPath);
        Assert.Empty(data.Workflows);
        Assert.Equal(secondPath, second.Context.CurrentSolutionPath);
        Assert.True(await first.OpenProjectFromPathAsync(firstPath, Token));
        Assert.Equal("Vendor.Unknown", Assert.Single(data.Workflows["flow"].Nodes).TypeKey);
        Assert.Equal(7, data.Settings.ErofsCompLevel);
        Assert.Equal("record", Assert.Single(first.History.CaptureSnapshot()).Description);
        Assert.False(first.History.CanUndo);
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
    public async Task CreateNewSolution_RegistersNoNodeProject()
    {
        var workspace = new WaitingWorkspace(new(), new());
        Assert.False(workspace.HasNodeEditor);
        var session = new ProjectSession(workspace);
        var solution = Path.Combine(_root, "headless.uotan");
        await session.CreateNewSolutionAsync(solution, Token);

        Assert.Empty(session.Context.NodeProjects);
        Assert.Null(session.Context.ActiveNodeProject);
        Assert.False(Directory.Exists(session.Context.NodeProjectsDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "headless", "headless.uotannode")));
        Assert.True(await session.SaveAsync(cancellationToken: Token));
        Assert.Null((await SolutionPersistenceService.LoadSolutionAsync(solution, Token))!.NodeProjects);
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
        Assert.True(await session.CreateNodeProjectAsync("canvas", Token));
        data.Workflows["canvas"].Nodes.Add(new() { TypeKey = "keep" });
        var target = Path.Combine(_root, "blocked.uotan");
        Directory.CreateDirectory(Path.Combine(_root, "blocked", "blocked.uotanfw"));
        await Assert.ThrowsAsync<IOException>(() => session.CreateNewSolutionAsync(target, Token));
        Assert.Equal(source, session.CurrentProjectPath);
        Assert.Equal(source, session.Context.CurrentSolutionPath);
        Assert.Equal("keep", Assert.Single(data.Workflows["canvas"].Nodes).TypeKey);
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

    [Fact]
    public async Task TwoNodeProjects_SaveAndReopen_RestoresBothDocumentsWithOneActive()
    {
        var session = new ProjectSession();
        var solution = Path.Combine(_root, "canvas.uotan");
        await session.CreateNewSolutionAsync(solution, Token);
        Assert.True(await session.CreateNodeProjectAsync("alpha", Token));
        Assert.True(await session.CreateNodeProjectAsync("beta", Token));
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Workflows["alpha"].Nodes.Add(new() { InstanceId = "a1", TypeKey = "Alpha.Node" });
        data.Workflows["beta"].Nodes.Add(new() { InstanceId = "b1", TypeKey = "Beta.Node" });
        Assert.True(await session.SaveAsync(cancellationToken: Token));

        var reopened = new ProjectSession();
        Assert.True(await reopened.OpenProjectFromPathAsync(solution, Token));
        var reopenedData = Assert.IsType<ProjectDataWorkspace>(reopened.Workspace);

        Assert.Equal(["alpha", "beta"], reopened.Context.NodeProjects.Select(node => node.Name).ToArray());
        Assert.Equal("Alpha.Node", Assert.Single(reopenedData.Workflows["alpha"].Nodes).TypeKey);
        Assert.Equal("Beta.Node", Assert.Single(reopenedData.Workflows["beta"].Nodes).TypeKey);
        Assert.Equal(2, reopened.Context.NodeProjects.Count(node => node.IsOpen));
        Assert.Equal("beta", Assert.Single(reopened.Context.NodeProjects, node => node.IsActive).Name);
        Assert.Equal("beta", reopened.Context.ActiveNodeProject!.Name);
        Assert.Equal("beta", reopenedData.ActiveWorkflowName);
        var stored = (await SolutionPersistenceService.LoadSolutionAsync(solution, Token))!.NodeProjects!;
        Assert.Equal(1, stored.Count(node => node.IsActive));
        Assert.Equal(2, stored.Count(node => node.IsOpen));
    }

    [Fact]
    public async Task CloseNodeProject_KeepsRegistrationAndFile_AndSaveRewritesOnlyOpenDocuments()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "closed.uotan"), Token);
        Assert.True(await session.CreateNodeProjectAsync("flow", Token));
        Assert.True(await session.CloseNodeProjectAsync("flow", Token));
        var nodePath = session.Context.ResolveNodeProjectPath(session.Context.NodeProjects.Single())!;
        Assert.True(await SolutionPersistenceService.SaveNodeProjectAsync(nodePath, new NodeProjectSnapshot
        {
            ProjectName = "flow",
            Nodes = [new() { InstanceId = "sentinel", TypeKey = "Sentinel.Node" }]
        }, Token));
        var sentinelBytes = await File.ReadAllBytesAsync(nodePath, Token);
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);

        Assert.True(await session.SaveAsync(cancellationToken: Token));

        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(nodePath, Token));
        Assert.Equal("Sentinel.Node", Assert.Single((await SolutionPersistenceService.LoadNodeProjectAsync(nodePath, Token))!.Nodes).TypeKey);
        Assert.Empty(data.Workflows);
        var registration = Assert.Single(session.Context.NodeProjects);
        Assert.False(registration.IsOpen);
        Assert.False(registration.IsActive);
        var stored = Assert.Single((await SolutionPersistenceService.LoadSolutionAsync(session.CurrentProjectPath!, Token))!.NodeProjects!);
        Assert.False(stored.IsOpen);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task RemoveNodeProject_UnregistersAndLeavesTheFileOnDiskUnowned()
    {
        var session = new ProjectSession();
        var solution = Path.Combine(_root, "removed.uotan");
        await session.CreateNewSolutionAsync(solution, Token);
        Assert.True(await session.CreateNodeProjectAsync("flow", Token));
        var nodePath = session.Context.ResolveNodeProjectPath(session.Context.NodeProjects.Single())!;

        Assert.True(await session.RemoveNodeProjectAsync("flow", Token));

        Assert.Empty(session.Context.NodeProjects);
        Assert.True(File.Exists(nodePath));
        var reopened = new ProjectSession();
        Assert.True(await reopened.OpenProjectFromPathAsync(solution, Token));
        Assert.Empty(reopened.Context.NodeProjects);
        Assert.Empty(Assert.IsType<ProjectDataWorkspace>(reopened.Workspace).Workflows);
        Assert.True(File.Exists(nodePath));
    }

    [Fact]
    public async Task Open_CorruptRegisteredNodeProject_AbortsAndPreservesLiveSession()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "live.uotan"), Token);
        var legacySolution = Path.Combine(_root, "legacy", "legacy.uotan");
        var author = new ProjectSession();
        await author.CreateNewSolutionAsync(legacySolution, Token);
        Assert.True(await author.CreateNodeProjectAsync("flow", Token));
        var nodePath = author.Context.ResolveNodeProjectPath(author.Context.NodeProjects.Single())!;
        await File.WriteAllTextAsync(nodePath,
            "{\"FormatVersion\":2,\"ProjectName\":\"flow\",\"Nodes\":[],\"Connections\":[]}", Token);
        var livePath = session.CurrentProjectPath;
        var notifications = 0;
        session.Changed += () => notifications++;

        Assert.False(await session.OpenProjectFromPathAsync(legacySolution, Token));

        Assert.Equal(livePath, session.CurrentProjectPath);
        Assert.Equal(0, notifications);
        Assert.Empty(session.Context.NodeProjects);
        Assert.Empty(Assert.IsType<ProjectDataWorkspace>(session.Workspace).Workflows);
        Assert.Contains("\"FormatVersion\":2", await File.ReadAllTextAsync(nodePath, Token));
    }

    [Fact]
    public async Task Open_RegisteredNodeProjectFileMissing_WarnsAndOpensWithoutDocument()
    {
        var author = new ProjectSession();
        var solution = Path.Combine(_root, "partial", "partial.uotan");
        await author.CreateNewSolutionAsync(solution, Token);
        Assert.True(await author.CreateNodeProjectAsync("flow", Token));
        var nodePath = author.Context.ResolveNodeProjectPath(author.Context.NodeProjects.Single())!;
        File.Delete(nodePath);
        var warnings = new List<string>();
        void OnDiagnostic(ProjectLogLevel level, string source, string message)
        {
            if (level == ProjectLogLevel.Warn && source == "Menu" && message.Contains(nodePath, StringComparison.Ordinal))
                warnings.Add(message);
        }
        ProjectDiagnostics.Message += OnDiagnostic;
        try
        {
            var session = new ProjectSession();

            Assert.True(await session.OpenProjectFromPathAsync(solution, Token));

            Assert.Equal("flow", Assert.Single(session.Context.NodeProjects).Name);
            var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
            Assert.Empty(data.Workflows);
            Assert.Null(data.ActiveWorkflowName);
            Assert.Contains("Node project file is missing", Assert.Single(warnings));
        }
        finally
        {
            ProjectDiagnostics.Message -= OnDiagnostic;
        }
    }

    [Fact]
    public async Task SaveAs_TwoOpenNodeProjects_WritesBothIntoDestinationNodesAndReopensThem()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "source.uotan"), Token);
        Assert.True(await session.CreateNodeProjectAsync("alpha", Token));
        Assert.True(await session.CreateNodeProjectAsync("beta", Token));
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Workflows["alpha"].Nodes.Add(new() { TypeKey = "Alpha.Node" });
        data.Workflows["beta"].Nodes.Add(new() { TypeKey = "Beta.Node" });
        var destination = Path.Combine(_root, "destination");
        var target = Path.Combine(destination, "copy.uotan");

        await session.SaveAsAsync(target, Token);

        Assert.Equal(target, session.CurrentProjectPath);
        Assert.Equal(destination, session.Context.RootDirectory);
        Assert.True(File.Exists(Path.Combine(destination, "nodes", "alpha.uotannode")));
        Assert.True(File.Exists(Path.Combine(destination, "nodes", "beta.uotannode")));
        Assert.Equal(["alpha", "beta"], session.Context.NodeProjects.Select(node => node.Name).ToArray());
        Assert.All(session.Context.NodeProjects, node => Assert.True(node.IsOpen));
        Assert.Equal("beta", session.Context.ActiveNodeProject!.Name);
        var reopened = new ProjectSession();
        Assert.True(await reopened.OpenProjectFromPathAsync(target, Token));
        var reopenedData = Assert.IsType<ProjectDataWorkspace>(reopened.Workspace);
        Assert.Equal("Alpha.Node", Assert.Single(reopenedData.Workflows["alpha"].Nodes).TypeKey);
        Assert.Equal("Beta.Node", Assert.Single(reopenedData.Workflows["beta"].Nodes).TypeKey);
    }

    private sealed class WaitingWorkspace(TaskCompletionSource entered, TaskCompletionSource release) : IProjectWorkspace
    {
        private readonly NodeWorkflowRecorder _workflows = new();
        public bool IsImageOperationRunning => false;
        public bool HasNodeEditor => false;
        public void ClearNodes() => _workflows.Clear();
        public void ClearImages() { }
        public void ApplySettings(ProjectSettingsDto settings) { }
        public void RestoreImages(IEnumerable<ImageEntryDto> images) { }
        public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory) => Task.CompletedTask;
        public Task RestoreWorkflowAsync(string name, NodeProjectSnapshot snapshot)
        {
            _workflows.Restore(name, snapshot);
            return Task.CompletedTask;
        }
        public bool TryCaptureWorkflow(string name, [NotNullWhen(true)] out NodeProjectSnapshot? snapshot)
            => _workflows.TryCapture(name, out snapshot);
        public IReadOnlyList<string> OpenWorkflowNames => _workflows.OpenWorkflowNames;
        public string? ActiveWorkflowName => _workflows.ActiveWorkflowName;
        public void ActivateWorkflow(string name) => _workflows.ActivateWorkflow(name);
        public void CloseWorkflow(string name) => _workflows.CloseWorkflow(name);
        public List<ImageEntryDto> CaptureImages() => [];
        public ProjectSettingsDto CaptureSettings() => new();
        public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => [];
        public Task SaveActiveDocumentAsync() => Task.CompletedTask;
        public async Task AutoSaveDocumentsAsync() { entered.TrySetResult(); await release.Task; }
        public Task ImportFilesAsync(string[] paths) => Task.CompletedTask;
    }
}