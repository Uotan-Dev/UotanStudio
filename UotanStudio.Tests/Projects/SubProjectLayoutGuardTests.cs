using System.Diagnostics.CodeAnalysis;
using UotanStudio.Projects;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>
/// Verifies the sub-project layout guard: a sub-project root must be a nested directory below the
/// solution root, never the solution root itself; each sub-project owns its state directory.
/// <para>验证子项目布局守卫：子项目根必须是解决方案根下的嵌套目录，不得为解决方案根本身，
/// 每个子项目独立持有状态目录。
/// </para>
/// </summary>
public sealed class SubProjectLayoutGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-layout-guard-" + Guid.NewGuid().ToString("N"));

    public SubProjectLayoutGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SnapshotReference_RootLevelFirmware_IsSkipped()
    {
        var solution = Path.Combine(_root, "sol.uotan");
        File.WriteAllText(Path.Combine(_root, "flat.uotanfw"), "{}");

        var context = new ProjectContext();
        context.OpenSolution(solution, new SolutionSnapshot
        {
            FormatVersion = 1,
            SolutionName = "sol",
            SubProjects = [new() { Name = "flat", RelativePath = "flat.uotanfw" }]
        });

        Assert.Empty(context.SubProjects);
    }

    [Fact]
    public void SnapshotReference_NestedFirmware_IsKept()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var solution = Path.Combine(_root, "sol.uotan");
        File.WriteAllText(Path.Combine(_root, "nested", "nested.uotanfw"), "{}");

        var context = new ProjectContext();
        context.OpenSolution(solution, new SolutionSnapshot
        {
            FormatVersion = 1,
            SolutionName = "sol",
            SubProjects = [new() { Name = "nested", RelativePath = "nested/nested.uotanfw" }]
        });

        Assert.Equal("nested", Assert.Single(context.SubProjects).Name);
    }

    [Fact]
    public void UnsupportedSolutionVersion_IsRejected()
    {
        var solution = Path.Combine(_root, "sol.uotan");
        File.WriteAllText(Path.Combine(_root, "legacy.uotanfw"), "{}");

        var context = new ProjectContext();
        Assert.Throws<InvalidDataException>(() => context.OpenSolution(solution, new SolutionSnapshot
        {
            FormatVersion = 2,
            SolutionName = "sol"
        }));
    }

    [Fact]
    public void FallbackScan_FindsNested_SkipsRootLevelAndReservedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "device", "images"));
        Directory.CreateDirectory(Path.Combine(_root, ".uotan"));
        var solution = Path.Combine(_root, "sol.uotan");
        File.WriteAllText(Path.Combine(_root, "top.uotanfw"), "{}");
        File.WriteAllText(Path.Combine(_root, "device", "device.uotanfw"), "{}");
        File.WriteAllText(Path.Combine(_root, ".uotan", "ghost.uotanfw"), "{}");

        var context = new ProjectContext();
        context.OpenSolution(solution);

        var sub = Assert.Single(context.SubProjects);
        Assert.Equal("device", sub.Name);
        Assert.Equal(Path.Combine(_root, "device"), sub.RootPath);
    }

    [Fact]
    public async Task AddSubProject_IntoSolutionRoot_IsAborted()
    {
        var session = CreateSession(out var workspace);
        var solution = Path.Combine(_root, "sol.uotan");
        await session.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        var fwPath = Path.Combine(session.Context.RootDirectory!, "bad.uotanfw");
        workspace.Events.Clear();

        await session.AddSubProjectAsync(fwPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(session.Context.SubProjects);
        Assert.False(File.Exists(fwPath));
        Assert.Empty(workspace.Events);
    }

    [Fact]
    public async Task ImportSubProject_SolutionRootDirectory_IsAborted()
    {
        var session = CreateSession(out _);
        var solution = Path.Combine(_root, "sol.uotan");
        await session.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);

        await session.ImportSubProjectAsync(session.Context.RootDirectory!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(session.Context.SubProjects);
    }

    [Fact]
    public async Task AddSubProject_NestedDirectory_StillSucceeds()
    {
        var session = CreateSession(out _);
        var solution = Path.Combine(_root, "sol.uotan");
        await session.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);

        await session.AddSubProjectAsync(
            Path.Combine(session.Context.RootDirectory!, "second", "second.uotanfw"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, session.Context.SubProjects.Count);
        Assert.Equal("second", session.Context.SubProjects[1].Name);
    }

    private static SolutionSnapshot NodeSolution(params NodeProjectDto[] nodes) => new()
    {
        FormatVersion = 1,
        SolutionName = "sol",
        NodeProjects = [.. nodes]
    };

    [Fact]
    public void ValidateSolution_RegisteredOpenAndClosedNodeProjects_IsAccepted()
    {
        var snapshot = NodeSolution(
            new NodeProjectDto { Name = "flow", RelativePath = "nodes/flow.uotannode", IsOpen = true, IsActive = true },
            new NodeProjectDto { Name = "idle", RelativePath = "nodes/idle.uotannode" });

        ProjectValidation.ValidateSolution(snapshot);
    }

    [Fact]
    public void ValidateSolution_DuplicateNodeProjectName_IsRejected()
    {
        var snapshot = NodeSolution(
            new NodeProjectDto { Name = "flow", RelativePath = "nodes/flow.uotannode", IsOpen = true, IsActive = true },
            new NodeProjectDto { Name = "FLOW", RelativePath = "nodes/other.uotannode" });

        Assert.Equal("Duplicate node project name.",
            Assert.Throws<InvalidDataException>(() => ProjectValidation.ValidateSolution(snapshot)).Message);
    }

    [Fact]
    public void ValidateSolution_BlankNodeProjectPath_IsRejected()
    {
        var snapshot = NodeSolution(new NodeProjectDto { Name = "flow", RelativePath = " ", IsOpen = true, IsActive = true });

        Assert.Equal("Empty node project path.",
            Assert.Throws<InvalidDataException>(() => ProjectValidation.ValidateSolution(snapshot)).Message);
    }

    [Fact]
    public void ValidateSolution_NodeProjectPathEscapingRoot_IsRejected()
    {
        var snapshot = NodeSolution(new NodeProjectDto { Name = "flow", RelativePath = "../escape.uotannode", IsOpen = true, IsActive = true });

        Assert.Equal("Node project path escapes the solution root.",
            Assert.Throws<InvalidDataException>(() => ProjectValidation.ValidateSolution(snapshot)).Message);
    }

    [Fact]
    public void ValidateSolution_ActiveNodeProjectWithoutOpen_IsRejected()
    {
        var snapshot = NodeSolution(new NodeProjectDto { Name = "flow", RelativePath = "nodes/flow.uotannode", IsActive = true });

        Assert.Equal("An active node project must be open.",
            Assert.Throws<InvalidDataException>(() => ProjectValidation.ValidateSolution(snapshot)).Message);
    }

    [Fact]
    public void ValidateSolution_MoreThanOneActiveNodeProject_IsRejected()
    {
        var snapshot = NodeSolution(
            new NodeProjectDto { Name = "alpha", RelativePath = "nodes/alpha.uotannode", IsOpen = true, IsActive = true },
            new NodeProjectDto { Name = "beta", RelativePath = "nodes/beta.uotannode", IsOpen = true, IsActive = true });

        Assert.Equal("More than one active node project.",
            Assert.Throws<InvalidDataException>(() => ProjectValidation.ValidateSolution(snapshot)).Message);
    }

    [Fact]
    public void OpenSolution_SnapshotWithRejectedNodeList_KeepsPreviousContextPublished()
    {
        var context = new ProjectContext();
        context.OpenSolution(Path.Combine(_root, "first.uotan"));
        var snapshot = NodeSolution(new NodeProjectDto { Name = "flow", RelativePath = "nodes/flow.uotannode", IsActive = true });

        Assert.Throws<InvalidDataException>(() => context.OpenSolution(Path.Combine(_root, "second.uotan"), snapshot));

        Assert.Equal("first", context.SolutionName);
        Assert.Empty(context.NodeProjects);
    }

    private ProjectSession CreateSession(out TestWorkspace workspace)
    {
        workspace = new TestWorkspace();
        return new(workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class TestWorkspace : IProjectWorkspace
    {
        private readonly NodeWorkflowRecorder _workflows = new();
        public List<string> Events { get; } = [];
        public List<ImageEntryDto> Images { get; } = [];
        public List<OpenDocumentDto> Documents { get; } = [];
        public Func<Task> SaveDocuments { get; set; } = () => Task.CompletedTask;
        public bool IsImageOperationRunning { get; set; }
        public bool HasNodeEditor => true;
        public void ClearNodes() { Events.Add("clear-nodes"); _workflows.Clear(); }
        public void ClearImages() { Events.Add("clear-images"); Images.Clear(); }
        public void ApplySettings(ProjectSettingsDto settings) => Events.Add("settings");
        public void RestoreImages(IEnumerable<ImageEntryDto> images) { Events.Add("images"); Images.AddRange(images); }
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
        public List<ImageEntryDto> CaptureImages() => [.. Images];
        public ProjectSettingsDto CaptureSettings() => new();
        public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) => [];
        public Task SaveActiveDocumentAsync() => Task.CompletedTask;
        public Task AutoSaveDocumentsAsync() => Task.CompletedTask;
        public Task ImportFilesAsync(string[] paths) => Task.CompletedTask;
    }
}