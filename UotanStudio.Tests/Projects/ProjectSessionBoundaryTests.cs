using System.Diagnostics.CodeAnalysis;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>Exercises project operations through data only, without constructing a desktop workspace.</summary>
public sealed class ProjectSessionBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-project-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly TestWorkspace _workspace = new();
    private readonly ProjectSession _projects;

    public ProjectSessionBoundaryTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new(_workspace);
    }

    [Fact]
    public async Task OpenProject_OldNodeSchema_PreservesCurrentContextAndWorkspace()
    {
        var token = TestContext.Current.CancellationToken;
        var original = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(original, cancellationToken: token);
        var oldProject = Path.Combine(_root, "legacy.uotan");
        var other = new ProjectSession(new ProjectDataWorkspace());
        await other.CreateNewSolutionAsync(oldProject, cancellationToken: token);
        Assert.True(await other.CreateNodeProjectAsync("legacy", token));
        var nodePath = other.Context.ResolveNodeProjectPath(other.Context.NodeProjects.Single())!;
        await File.WriteAllTextAsync(nodePath,
            "{\"FormatVersion\":2,\"ProjectName\":\"legacy\",\"Nodes\":[],\"Connections\":[]}", token);
        _workspace.Events.Clear();

        Assert.False(await _projects.OpenProjectFromPathAsync(oldProject, cancellationToken: token));

        Assert.Equal(original, _projects.CurrentProjectPath);
        Assert.Empty(_workspace.Events);
        Assert.Equal(ProjectOpenFailure.InvalidNodeProject, _projects.OpenFailure);
        Assert.Equal(2, _projects.OpenFailureStoredVersion);
        Assert.Equal(nodePath, _projects.OpenFailurePath);
        Assert.Contains("\"FormatVersion\":2", await File.ReadAllTextAsync(nodePath, token));
    }

    [Fact]
    public async Task OpenProject_NewerSolutionFormat_ReportsInvalidSolutionWithStoredVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "future.uotan");
        await File.WriteAllTextAsync(path,
            "{\"FormatVersion\":2,\"SolutionName\":\"future\",\"SubProjects\":null,\"NodeProjects\":null}", token);
        var before = _projects.CurrentProjectPath;

        Assert.False(await _projects.OpenProjectFromPathAsync(path, cancellationToken: token));

        Assert.Equal(ProjectOpenFailure.InvalidSolution, _projects.OpenFailure);
        Assert.Equal(2, _projects.OpenFailureStoredVersion);
        Assert.Equal(path, _projects.OpenFailurePath);
        Assert.Equal(before, _projects.CurrentProjectPath);
    }

    [Fact]
    public async Task OpenProject_MissingFile_ReportsMissingFileWithoutStoredVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "absent.uotan");

        Assert.False(await _projects.OpenProjectFromPathAsync(path, cancellationToken: token));

        Assert.Equal(ProjectOpenFailure.MissingFile, _projects.OpenFailure);
        Assert.Null(_projects.OpenFailureStoredVersion);
        Assert.Equal(path, _projects.OpenFailurePath);
    }

    [Fact]
    public async Task OpenProject_Succeeds_OpenFailureIsCleared()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "clean.uotan");
        await _projects.CreateNewSolutionAsync(path, cancellationToken: token);
        Assert.False(await _projects.OpenProjectFromPathAsync(Path.Combine(_root, "absent.uotan"), cancellationToken: token));

        Assert.True(await _projects.OpenProjectFromPathAsync(path, cancellationToken: token));

        Assert.Equal(ProjectOpenFailure.None, _projects.OpenFailure);
        Assert.Null(_projects.OpenFailureStoredVersion);
        Assert.Null(_projects.OpenFailurePath);
    }

    [Fact]
    public async Task SaveNodeProject_UnsupportedSchema_PreservesExistingDocument()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "existing.uotannode");
        Assert.True(await SolutionPersistenceService.SaveNodeProjectAsync(path, new NodeProjectSnapshot(), token));
        var previous = await File.ReadAllBytesAsync(path, token);

        Assert.False(await SolutionPersistenceService.SaveNodeProjectAsync(path, new NodeProjectSnapshot { FormatVersion = 2 }, token));

        Assert.Equal(previous, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(NodeProjectSnapshot.CurrentFormatVersion,
            (await SolutionPersistenceService.LoadNodeProjectAsync(path, token))!.FormatVersion);
    }

    [Fact]
    public async Task ExecuteFileOperation_ProjectChange_WaitsUntilFileOperationFinishes()
    {
        var token = TestContext.Current.CancellationToken;
        var original = Path.Combine(_root, "original.uotan");
        var next = Path.Combine(_root, "next.uotan");
        await _projects.CreateNewSolutionAsync(original, cancellationToken: token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = _projects.ExecuteFileOperationAsync(async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, token);
        await entered.Task.WaitAsync(token);
        var change = _projects.CreateNewSolutionAsync(next, cancellationToken: token);
        try
        {
            Assert.True(_projects.IsBusy);
            Assert.False(change.IsCompleted);
            Assert.Equal(original, _projects.CurrentProjectPath);
            Assert.False(File.Exists(next));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(operation, change);
        Assert.Equal(next, _projects.CurrentProjectPath);
        Assert.False(_projects.IsBusy);
    }

    [Fact]
    public async Task CreateAndOpen_RestoresDataInOrder_ExcludesMissingImages()
    {
        var path = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        Assert.True(await _projects.CreateNodeProjectAsync("flow", TestContext.Current.CancellationToken));
        var imagePath = Path.Combine(context.EnsureImagesDirectory(), "boot.img");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3], TestContext.Current.CancellationToken);
        var relative = Path.GetRelativePath(context.RootDirectory!, imagePath);
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(context.FirmwareProjectPath!, new()
        {
            ProjectName = "sample",
            Settings = new(),
            Images = [new() { RelativePath = relative, SizeBytes = 3, Format = "Boot" }, new() { RelativePath = "missing.img" }],
            OpenDocuments = [new() { Kind = "Text", RelativePath = "readme.txt" }]
        }, TestContext.Current.CancellationToken));
        _workspace.Events.Clear();

        Assert.True(await _projects.OpenProjectFromPathAsync(path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(relative, Assert.Single(_workspace.Images).RelativePath);
        Assert.Equal(["clear-images", "settings", "images", "settings", "documents", "clear-nodes", "workflow"], _workspace.Events);
        Assert.Equal("readme.txt", Assert.Single(_workspace.Documents).RelativePath);
        Assert.Equal(path, _projects.CurrentProjectPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectChange_BusyOrCancelled_LeavesCurrentProjectAndFilesUntouched(bool busy)
    {
        _projects.CurrentProjectPath = "original.uotan";
        _workspace.IsImageOperationRunning = busy;
        var confirmed = 0;
        _projects.BeforeProjectChangeAsync = () => { confirmed++; return Task.FromResult(false); };
        var path = Path.Combine(_root, "cancelled.uotan");

        await _projects.CreateNewSolutionAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(busy ? 0 : 1, confirmed);
        Assert.Equal("original.uotan", _projects.CurrentProjectPath);
        Assert.False(File.Exists(path));
        Assert.Empty(_workspace.Events);
    }

    private string DestinationRoot(bool existing)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetFileName(_root) + "-dest-" + Guid.NewGuid().ToString("N"));
        if (existing) Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAs_CopiesImages_AndPersistsWorkspaceData(bool existingTarget)
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "original.uotan"), cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        var originalImage = Path.Combine(context.EnsureImagesDirectory(), "boot.img");
        await File.WriteAllBytesAsync(originalImage, [4, 5, 6], TestContext.Current.CancellationToken);
        _workspace.Images.Add(new() { RelativePath = Path.GetRelativePath(context.RootDirectory!, originalImage), Format = "Boot", SizeBytes = 3 });
        var originalProjectPath = context.FirmwareProjectPath!;
        var originalProject = File.ReadAllBytes(originalProjectPath);
        var destination = DestinationRoot(existingTarget);
        var target = Path.Combine(destination, "copy.uotan");
        var unrelated = Path.Combine(destination, "keep.txt");
        if (existingTarget)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(unrelated, "keep");
            File.WriteAllText(target, "old-destination-solution");
        }

        await _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(originalProject, File.ReadAllBytes(originalProjectPath));
        Assert.True(File.Exists(originalImage));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(context.EnsureImagesDirectory(), "boot.img"), TestContext.Current.CancellationToken));
        if (existingTarget) Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal(target, _projects.CurrentProjectPath);
        Assert.Equal(destination, _projects.Context.RootDirectory);
        var saved = await SolutionPersistenceService.LoadFirmwareProjectAsync(context.FirmwareProjectPath!, TestContext.Current.CancellationToken);
        Assert.Equal("Boot", Assert.Single(saved!.Images).Format);
        Assert.True(await _projects.OpenProjectFromPathAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Boot", Assert.Single(_workspace.Images).Format);
    }

    [Fact]
    public async Task SaveAs_CopiesAllSubprojectsNestedFilesAndMetadata_PreservesActiveProject()
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        var oldRoot = context.RootDirectory!;
        await _projects.AddSubProjectAsync(Path.Combine(oldRoot, "second", "second.uotanfw"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(context.SetActiveSubProject("second"));
        var nested = Path.Combine(context.EnsureImagesDirectory(), "nested", "system.img");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, [7, 8, 9]);
        var metadata = Path.Combine(context.GetMetadataRoot()!, "system", RepackMetadata.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(metadata, "metadata-marker");
        var first = Path.Combine(oldRoot, "original", "original.uotanfw");
        var firstBytes = File.ReadAllBytes(first);
        _workspace.Images.Clear();
        _workspace.Images.Add(new() { RelativePath = Path.GetRelativePath(oldRoot, nested), Format = "EXT4", SizeBytes = 3 });

        var destination = DestinationRoot(existing: false);
        var target = Path.Combine(destination, "copy.uotan");
        await _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, context.SubProjects.Count);
        Assert.Equal("second", context.ActiveSubProject!.Name);
        Assert.Equal(destination, context.RootDirectory);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(destination, "original", "original.uotanfw")));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(destination, "second", "images", "nested", "system.img")));
        Assert.Equal("metadata-marker", File.ReadAllText(Path.Combine(destination, "second", ".uotan", "metadata", "system", RepackMetadata.FileName)));
        var snapshot = await SolutionPersistenceService.LoadSolutionAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot!.SubProjects!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAs_WriteOrCommitFailure_PreservesSourceContextAndExistingDestination(bool commitFailure)
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await _projects.CreateNodeProjectAsync("flow", TestContext.Current.CancellationToken));
        var context = _projects.Context;
        var oldRoot = context.RootDirectory;
        var oldActive = context.ActiveSubProject;
        var original = File.ReadAllBytes(source);
        var firmwarePath = context.FirmwareProjectPath!;
        var firmwareBytes = File.ReadAllBytes(firmwarePath);
        var destination = DestinationRoot(existing: false);
        var target = Path.Combine(destination, "copy.uotan");
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "keep.txt");
        File.WriteAllText(marker, "untouched");
        if (commitFailure) Directory.CreateDirectory(target);
        else Directory.CreateDirectory(Path.Combine(destination, "nodes", "flow.uotannode"));

        var error = await Record.ExceptionAsync(() => _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected error: {error}");

        Assert.Equal(source, _projects.CurrentProjectPath);
        Assert.Equal(source, context.CurrentSolutionPath);
        Assert.Equal(oldRoot, context.RootDirectory);
        Assert.Same(oldActive, context.ActiveSubProject);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(firmwareBytes, File.ReadAllBytes(firmwarePath));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.False(_projects.IsBusy);
    }

    [Fact]
    public async Task SaveAs_OverlappingRoots_IsRejectedBeforeChangingContext()
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        var target = Path.Combine(context.RootDirectory!, "original", "nested.uotan");
        await Assert.ThrowsAsync<IOException>(() => _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(source, _projects.CurrentProjectPath);
        Assert.Equal(source, context.CurrentSolutionPath);
        Assert.Single(context.SubProjects);
        Assert.False(Directory.Exists(Path.Combine(_root, "original", "nested")));
    }

    [Fact]
    public async Task SaveAs_WithoutCurrentProject_CreatesDiscoverableDraftProject()
    {
        _projects.Context.CloseProject();
        var target = Path.Combine(_root, "draft.uotan");
        await _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("draft", _projects.Context.ActiveSubProject!.Name);
        Assert.True(File.Exists(_projects.Context.FirmwareProjectPath));
        Assert.True(await _projects.OpenProjectFromPathAsync(target, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(".uotan")]
    [InlineData("..uotan")]
    [InlineData("...uotan")]
    [InlineData("no-extension")]
    public async Task SaveAs_InvalidTargetName_DoesNotTouchParentDirectory(string filename)
    {
        _projects.Context.CloseProject();
        var marker = Path.Combine(_root, "keep.txt");
        File.WriteAllText(marker, "untouched");
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.SaveAsAsync(Path.Combine(_root, filename), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.Null(_projects.CurrentProjectPath);
        Assert.Null(_projects.Context.RootDirectory);
        Assert.Equal([marker], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SaveAs_SamePath_UsesNormalSaveWithoutLosingSubprojects()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var active = _projects.Context.ActiveSubProject;
        _workspace.Images.Add(new() { RelativePath = "sample/images/system.img", Format = "EXT4", SizeBytes = 4096 });
        await _projects.SaveAsAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(active, _projects.Context.ActiveSubProject);
        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(active!.FwProjectPath, TestContext.Current.CancellationToken);
        Assert.Equal("EXT4", Assert.Single(firmware!.Images).Format);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task Save_FailedPersistence_DoesNotReportSuccess()
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), cancellationToken: TestContext.Current.CancellationToken);
        var firmwarePath = _projects.Context.FirmwareProjectPath!;
        File.Delete(firmwarePath);
        Directory.CreateDirectory(firmwarePath);
        await Assert.ThrowsAsync<IOException>(() => _projects.SaveAsync(saveActiveDocument: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(_projects.IsBusy);
    }

    [Fact]
    public async Task SaveAs_EverySubProject_IsCopiedWithoutRewritingSourceFiles()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var shared = Path.Combine(_root, "shared", "shared.uotanfw");
        await _projects.AddSubProjectAsync(shared, cancellationToken: TestContext.Current.CancellationToken);
        var before = File.ReadAllBytes(shared);
        var destination = DestinationRoot(existing: false);
        var target = Path.Combine(destination, "copy.uotan");

        await _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllBytes(shared));
        Assert.Equal(2, _projects.Context.SubProjects.Count);
        Assert.Equal(target, _projects.CurrentProjectPath);
        Assert.True(File.Exists(Path.Combine(destination, "shared", "shared.uotanfw")));
    }

    [Fact]
    public async Task SaveAs_KeepsNestedOnlyAsSubProjectFolder_WithoutRegisteringInnerScaffold()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        var inner = Path.Combine(_root, "sample", "inner", "inner.uotanfw");
        Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
        File.WriteAllBytes(inner, [9, 9, 9]);
        var destination = DestinationRoot(existing: false);
        var target = Path.Combine(destination, "copy.uotan");

        await _projects.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(_projects.Context.SubProjects);
        Assert.True(File.Exists(Path.Combine(destination, "sample", "inner", "inner.uotanfw")));
        Assert.True(await _projects.OpenProjectFromPathAsync(target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(_projects.Context.SubProjects);
    }

    [Fact]
    public async Task PersistWithoutPageState_PreservesPreviouslySavedDocuments()
    {
        var path = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        _workspace.Documents.Add(new() { Kind = "Text", RelativePath = "saved.txt" });
        await _projects.PersistToAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        _workspace.Documents.Clear();
        _workspace.Events.Clear();

        await _projects.PersistToAsync(path, persistPageState: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("capture-documents", _workspace.Events);
        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(_projects.Context.FirmwareProjectPath!, TestContext.Current.CancellationToken);
        Assert.Equal("saved.txt", Assert.Single(firmware!.OpenDocuments!).RelativePath);
        await _projects.PersistToAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(_projects.Context.FirmwareProjectPath!, TestContext.Current.CancellationToken);
        Assert.Null(firmware!.OpenDocuments);
    }

    [Fact]
    public async Task AutoSave_SkipsOverlappingRun_AndReleasesGateAfterFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _workspace.SaveDocuments = async () =>
        {
            calls++;
            entered.SetResult();
            await release.Task;
            throw new IOException("save failed");
        };
        var pending = _projects.AutoSaveAsync(persistProject: false, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            await _projects.AutoSaveAsync(persistProject: false, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, calls);
            Assert.True(_projects.IsBusy);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.False(_projects.IsBusy);
        _workspace.SaveDocuments = () => Task.CompletedTask;
        await _projects.AutoSaveAsync(persistProject: false, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AddAndImportSubprojects_PreserveExistingFirmwareFile()
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        var added = Path.Combine(context.RootDirectory!, "added", "added.uotanfw");
        await _projects.AddSubProjectAsync(added, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(added));
        Assert.Contains(context.SubProjects, sp => sp.Name == "added");
        var imported = Path.Combine(context.RootDirectory!, "imported");
        Directory.CreateDirectory(imported);
        var existing = Path.Combine(imported, "imported.uotanfw");
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(existing, new() { ProjectName = "keep-custom-name" }, TestContext.Current.CancellationToken));
        var bytes = await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken);

        await _projects.ImportSubProjectAsync(imported, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(Path.Combine(imported, "images")));
        Assert.True(Directory.Exists(Path.Combine(imported, "output")));
        Assert.Contains(context.SubProjects, sp => sp.Name == "imported");
        Assert.True(await _projects.OpenProjectFromPathAsync(_projects.CurrentProjectPath!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, context.SubProjects.Count);
    }

    [Fact]
    public async Task CreateNewSolution_ProducesFlatIndependentSubProjectFolder()
    {
        var solution = Path.Combine(_root, "flat.uotan");
        await _projects.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;

        Assert.Equal(_root, context.RootDirectory);
        Assert.Equal("flat", context.SolutionName);
        Assert.Single(context.SubProjects);
        Assert.Equal(Path.Combine(_root, "flat"), context.SubProjects[0].RootPath);
        Assert.True(File.Exists(Path.Combine(_root, "flat", "flat.uotanfw")));
        Assert.Empty(context.NodeProjects);
        Assert.False(Directory.Exists(Path.Combine(_root, "nodes")));
        Assert.False(File.Exists(Path.Combine(_root, "flat", "flat.uotannode")));
        Assert.True(Directory.Exists(Path.Combine(_root, "flat", "images")));
        Assert.True(Directory.Exists(Path.Combine(_root, "flat", "output")));
        Assert.True(File.Exists(solution));

        Assert.True(await _projects.CreateNodeProjectAsync("flow", TestContext.Current.CancellationToken));
        var node = Assert.Single(context.NodeProjects);
        Assert.Equal("flow", node.Name);
        Assert.Equal("nodes/flow.uotannode", node.RelativePath);
        Assert.Equal(Path.Combine(_root, "nodes"), context.NodeProjectsDirectory);
        Assert.Equal(Path.Combine(_root, "nodes", "flow.uotannode"), context.ResolveNodeProjectPath(node));
        Assert.True(File.Exists(context.ResolveNodeProjectPath(node)));
    }

    [Fact]
    public async Task SubProject_DeliveryDirectory_IsSiblingOfImagesAndOutput()
    {
        var solution = Path.Combine(_root, "flat.uotan");
        await _projects.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        var sub = Assert.Single(_projects.Context.SubProjects);

        Assert.Equal(Path.Combine(sub.RootPath!, "delivery"), sub.DeliveryDirectory);
        Assert.NotEqual(sub.OutputDir, sub.DeliveryDirectory);
        Assert.Equal(sub.RootPath, Path.GetDirectoryName(sub.DeliveryDirectory));
        Assert.Equal(sub.RootPath, Path.GetDirectoryName(sub.ImagesDir));
        Assert.True(Directory.Exists(sub.DeliveryDirectory));
    }

    [Fact]
    public async Task MultipleSubProjects_SiblingFlatFolders_EachOwnItsDirectory()
    {
        var solution = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        await _projects.AddSubProjectAsync(Path.Combine(context.RootDirectory!, "second", "second.uotanfw"), cancellationToken: TestContext.Current.CancellationToken);
        await _projects.AddSubProjectAsync(Path.Combine(context.RootDirectory!, "third", "third.uotanfw"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(_root, context.RootDirectory);
        Assert.Equal(3, context.SubProjects.Count);
        foreach (var name in new[] { "sample", "second", "third" })
        {
            var sub = Assert.Single(context.SubProjects, s => s.Name == name);
            Assert.Equal(Path.Combine(_root, name), sub.RootPath);
            Assert.Equal(_root, Path.GetDirectoryName(sub.RootPath));
            Assert.True(File.Exists(Path.Combine(sub.RootPath!, name + ".uotanfw")));
            Assert.True(Directory.Exists(Path.Combine(sub.RootPath!, "images")));
            Assert.True(Directory.Exists(Path.Combine(sub.RootPath!, "output")));
        }

        Assert.Empty(context.NodeProjects);
        Assert.False(Directory.Exists(Path.Combine(_root, "nodes")));

        context.CloseProject();
        Assert.True(await _projects.OpenProjectFromPathAsync(solution, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(_root, _projects.Context.RootDirectory);
        Assert.Equal(["sample", "second", "third"], _projects.Context.SubProjects.Select(s => s.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task OpenSolution_RootDirectoryEqualsSolutionFolder()
    {
        var solution = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        _projects.Context.CloseProject();

        Assert.True(await _projects.OpenProjectFromPathAsync(solution, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(_root, _projects.Context.RootDirectory);
        Assert.Equal(_root, Path.GetDirectoryName(_projects.Context.CurrentSolutionPath));
        Assert.Contains(_projects.Context.SubProjects, sp => sp.Name == "sample");
    }

    [Fact]
    public async Task SubProject_InsideAnotherSubProject_IsNotRegistered()
    {
        var solution = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(solution, cancellationToken: TestContext.Current.CancellationToken);
        var context = _projects.Context;
        var inner = Path.Combine(context.RootDirectory!, "sample", "deep", "deep.uotanfw");
        Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
        File.WriteAllBytes(inner, [1, 2, 3]);

        await _projects.AddSubProjectAsync(Path.Combine(context.RootDirectory!, "second", "second.uotanfw"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(context.SubProjects, sp => sp.Name == "deep");
        Assert.Contains(context.SubProjects, sp => sp.Name == "second");
    }

    public void Dispose()
    {
        _projects.Context.CloseProject();
        Directory.Delete(_root, recursive: true);
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
        public Task RestoreDocumentsAsync(List<OpenDocumentDto> documents, string rootDirectory)
        {
            Events.Add("documents");
            Documents.Clear();
            Documents.AddRange(documents);
            return Task.CompletedTask;
        }
        public Task RestoreWorkflowAsync(string name, NodeProjectSnapshot snapshot)
        {
            Events.Add("workflow");
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
        public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) { Events.Add("capture-documents"); return [.. Documents]; }
        public Task SaveActiveDocumentAsync() => Task.CompletedTask;
        public Task AutoSaveDocumentsAsync() => SaveDocuments();
        public Task ImportFilesAsync(string[] paths) => Task.CompletedTask;
    }
}