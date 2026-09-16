using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Exercises project operations through data only, without constructing a desktop workspace.</summary>
[Collection("Workspace state")]
public sealed class ProjectOperationsBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-project-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly TestWorkspace _workspace = new();
    private readonly ProjectOperationsService _projects;

    public ProjectOperationsBoundaryTests()
    {
        Directory.CreateDirectory(_root);
        _projects = new(_workspace);
    }

    [Fact]
    public async Task CreateAndOpen_RestoresDataInOrder_ExcludesMissingImages()
    {
        var path = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(path);
        var context = ProjectContextService.Instance;
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

        Assert.True(await _projects.OpenProjectFromPathAsync(path));

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

        await _projects.CreateNewSolutionAsync(path);

        Assert.Equal(busy ? 0 : 1, confirmed);
        Assert.Equal("original.uotan", _projects.CurrentProjectPath);
        Assert.False(File.Exists(path));
        Assert.Empty(_workspace.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAs_CopiesImages_AndPersistsWorkspaceData(bool existingTarget)
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "original.uotan"));
        var context = ProjectContextService.Instance;
        var originalImage = Path.Combine(context.EnsureImagesDirectory(), "boot.img");
        await File.WriteAllBytesAsync(originalImage, [4, 5, 6], TestContext.Current.CancellationToken);
        _workspace.Images.Add(new() { RelativePath = Path.GetRelativePath(context.RootDirectory!, originalImage), Format = "Boot", SizeBytes = 3 });
        var originalProjectPath = context.FirmwareProjectPath!;
        var originalProject = File.ReadAllBytes(originalProjectPath);
        var target = Path.Combine(_root, "copy.uotan");
        var unrelated = Path.Combine(_root, "copy", "keep.txt");
        if (existingTarget)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
            File.WriteAllText(unrelated, "keep");
            File.WriteAllText(target, "old-destination-solution");
        }

        await _projects.SaveAsAsync(target);

        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(context.EnsureImagesDirectory(), "boot.img"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(originalImage));
        Assert.Equal(originalProject, File.ReadAllBytes(originalProjectPath));
        if (existingTarget) Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal(target, _projects.CurrentProjectPath);
        var saved = await SolutionPersistenceService.LoadFirmwareProjectAsync(context.FirmwareProjectPath!, TestContext.Current.CancellationToken);
        Assert.Equal("Boot", Assert.Single(saved!.Images).Format);
        Assert.True(await _projects.OpenProjectFromPathAsync(target));
        Assert.Equal("Boot", Assert.Single(_workspace.Images).Format);
    }

    [Fact]
    public async Task SaveAs_CopiesAllSubprojectsNestedFilesAndMetadata_PreservesActiveProject()
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var context = ProjectContextService.Instance;
        var oldRoot = context.RootDirectory!;
        await _projects.AddSubProjectAsync(Path.Combine(oldRoot, "second", "second.uotanfw"));
        Assert.True(context.SetActiveSubProject("second"));
        var nested = Path.Combine(context.EnsureImagesDirectory(), "nested", "system.img");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, [7, 8, 9]);
        var metadata = Path.Combine(context.GetMetadataRoot()!, "system", ".uotanstudio-repack.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(metadata, "metadata-marker");
        var first = Path.Combine(oldRoot, "original.uotanfw");
        var firstBytes = File.ReadAllBytes(first);
        _workspace.Images.Clear();
        _workspace.Images.Add(new() { RelativePath = Path.GetRelativePath(oldRoot, nested), Format = "EXT4", SizeBytes = 3 });

        var target = Path.Combine(_root, "copy.uotan");
        await _projects.SaveAsAsync(target);

        Assert.Equal(2, context.SubProjects.Count);
        Assert.Equal("second", context.ActiveSubProject!.Name);
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(context.RootDirectory!, "original.uotanfw")));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(context.RootDirectory!, "second", "images", "nested", "system.img")));
        Assert.Equal("metadata-marker", File.ReadAllText(Path.Combine(context.GetMetadataRoot()!, "system", ".uotanstudio-repack.json")));
        var snapshot = await SolutionPersistenceService.LoadSolutionAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot!.SubProjects!.Count);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAs_WriteOrCommitFailure_PreservesSourceContextAndExistingDestination(bool commitFailure)
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var context = ProjectContextService.Instance;
        var oldRoot = context.RootDirectory;
        var oldActive = context.ActiveSubProject;
        var oldMetadata = UotanStudio.Firmware.SharedKernel.RepackMetadata.DefaultMetadataRoot;
        var original = File.ReadAllBytes(source);
        var target = Path.Combine(_root, "copy.uotan");
        var destinationRoot = Path.ChangeExtension(target, null);
        Directory.CreateDirectory(destinationRoot);
        var marker = Path.Combine(destinationRoot, "keep.txt");
        File.WriteAllText(marker, "untouched");
        // A directory at the solution filename fails publication after the root swap.
        // A directory at the node filename fails staging before the root swap.
        Directory.CreateDirectory(commitFailure ? target : Path.Combine(destinationRoot, "copy.uotannode"));

        var error = await Record.ExceptionAsync(() => _projects.SaveAsAsync(target));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected error: {error}");

        Assert.Equal(source, _projects.CurrentProjectPath);
        Assert.Equal(source, context.CurrentSolutionPath);
        Assert.Equal(oldRoot, context.RootDirectory);
        Assert.Same(oldActive, context.ActiveSubProject);
        Assert.Equal(oldMetadata, UotanStudio.Firmware.SharedKernel.RepackMetadata.DefaultMetadataRoot);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "original")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*.saveas-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*.previous-*"));
        Assert.False(_projects.IsBusy);
    }

    [Fact]
    public async Task SaveAs_OverlappingRoots_IsRejectedBeforeChangingContext()
    {
        var source = Path.Combine(_root, "original.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var target = Path.Combine(ProjectContextService.Instance.RootDirectory!, "nested.uotan");
        await Assert.ThrowsAsync<IOException>(() => _projects.SaveAsAsync(target));
        Assert.Equal(source, _projects.CurrentProjectPath);
        Assert.False(Directory.Exists(Path.ChangeExtension(target, null)));
    }

    [Fact]
    public async Task SaveAs_WithoutCurrentProject_CreatesDiscoverableDraftProject()
    {
        ProjectContextService.Instance.CloseProject();
        var target = Path.Combine(_root, "draft.uotan");
        await _projects.SaveAsAsync(target);
        Assert.Equal("draft", ProjectContextService.Instance.ActiveSubProject!.Name);
        Assert.True(File.Exists(ProjectContextService.Instance.FirmwareProjectPath));
        Assert.True(await _projects.OpenProjectFromPathAsync(target));
    }

    [Theory]
    [InlineData(".uotan")]
    [InlineData("..uotan")]
    [InlineData("...uotan")]
    [InlineData("no-extension")]
    public async Task SaveAs_InvalidTargetName_DoesNotTouchParentDirectory(string filename)
    {
        ProjectContextService.Instance.CloseProject();
        var marker = Path.Combine(_root, "keep.txt");
        File.WriteAllText(marker, "untouched");
        await Assert.ThrowsAsync<ArgumentException>(() => _projects.SaveAsAsync(Path.Combine(_root, filename)));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.Null(_projects.CurrentProjectPath);
        Assert.Null(ProjectContextService.Instance.RootDirectory);
        Assert.Equal([marker], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SaveAs_SamePath_UsesNormalSaveWithoutLosingSubprojects()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var active = ProjectContextService.Instance.ActiveSubProject;
        _workspace.Images.Add(new() { RelativePath = "sample/images/system.img", Format = "EXT4", SizeBytes = 4096 });
        await _projects.SaveAsAsync(source);
        Assert.Same(active, ProjectContextService.Instance.ActiveSubProject);
        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(active!.FwProjectPath, TestContext.Current.CancellationToken);
        Assert.Equal("EXT4", Assert.Single(firmware!.Images).Format);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*.saveas-*"));
    }

    [Fact]
    public async Task Save_FailedPersistence_DoesNotReportSuccess()
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"));
        var firmwarePath = ProjectContextService.Instance.FirmwareProjectPath!;
        File.Delete(firmwarePath);
        Directory.CreateDirectory(firmwarePath);
        await Assert.ThrowsAsync<IOException>(() => _projects.SaveAsync(saveActiveDocument: false));
        Assert.False(_projects.IsBusy);
    }

    [Fact]
    public async Task SaveAs_ExternalSubproject_IsRejectedWithoutChangingSharedFiles()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var external = Path.Combine(_root, "shared", "shared.uotanfw");
        await _projects.AddSubProjectAsync(external);
        var before = File.ReadAllBytes(external);
        await Assert.ThrowsAsync<IOException>(() => _projects.SaveAsAsync(Path.Combine(_root, "copy.uotan")));
        Assert.Equal(source, _projects.CurrentProjectPath);
        Assert.Equal(before, File.ReadAllBytes(external));
        Assert.False(Directory.Exists(Path.Combine(_root, "copy")));
    }

    [Fact]
    public async Task SaveAs_AliasedInternalSubproject_WritesOnlyToPreparedCopy()
    {
        var source = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(source);
        var context = ProjectContextService.Instance;
        var originalFirmware = context.FirmwareProjectPath!;
        var originalBytes = File.ReadAllBytes(originalFirmware);
        var alias = Path.Combine(_root, "alias");
        try { Directory.CreateSymbolicLink(alias, context.RootDirectory!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symbolic links are unavailable: {ex.Message}");
        }
        try
        {
            context.OpenSolution(source, new SolutionSnapshot
            {
                SolutionName = "sample",
                SubProjects = [new() { Name = "sample", RelativePath = "../alias/sample.uotanfw" }]
            });
            _workspace.Images.Add(new() { RelativePath = "images/boot.img", Format = "Boot" });

            await _projects.SaveAsAsync(Path.Combine(_root, "copy.uotan"));

            Assert.Equal(originalBytes, File.ReadAllBytes(originalFirmware));
            Assert.Equal(Path.Combine(_root, "copy", "sample.uotanfw"), context.FirmwareProjectPath);
            var saved = await SolutionPersistenceService.LoadFirmwareProjectAsync(context.FirmwareProjectPath!, TestContext.Current.CancellationToken);
            Assert.Equal("Boot", Assert.Single(saved!.Images).Format);
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task PersistWithoutPageState_PreservesPreviouslySavedDocuments()
    {
        var path = Path.Combine(_root, "sample.uotan");
        await _projects.CreateNewSolutionAsync(path);
        _workspace.Documents.Add(new() { Kind = "Text", RelativePath = "saved.txt" });
        await _projects.PersistToAsync(path);
        _workspace.Documents.Clear();
        _workspace.Events.Clear();

        await _projects.PersistToAsync(path, persistPageState: false);

        Assert.DoesNotContain("capture-documents", _workspace.Events);
        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(ProjectContextService.Instance.FirmwareProjectPath!, TestContext.Current.CancellationToken);
        Assert.Equal("saved.txt", Assert.Single(firmware!.OpenDocuments!).RelativePath);
        await _projects.PersistToAsync(path);
        firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(ProjectContextService.Instance.FirmwareProjectPath!, TestContext.Current.CancellationToken);
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
        var pending = _projects.AutoSaveAsync(persistProject: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            await _projects.AutoSaveAsync(persistProject: false);
            Assert.Equal(1, calls);
            Assert.True(_projects.IsBusy);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.False(_projects.IsBusy);
        _workspace.SaveDocuments = () => Task.CompletedTask;
        await _projects.AutoSaveAsync(persistProject: false);
    }

    [Fact]
    public async Task AddAndImportSubprojects_PreserveExistingFirmwareFile()
    {
        await _projects.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"));
        var context = ProjectContextService.Instance;
        var added = Path.Combine(context.RootDirectory!, "added", "added.uotanfw");
        await _projects.AddSubProjectAsync(added);
        Assert.True(File.Exists(added));
        Assert.Contains(context.SubProjects, sp => sp.Name == "added");
        var imported = Path.Combine(context.RootDirectory!, "imported");
        Directory.CreateDirectory(imported);
        var existing = Path.Combine(imported, "imported.uotanfw");
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(existing, new() { ProjectName = "keep-custom-name" }, TestContext.Current.CancellationToken));
        var bytes = await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken);

        await _projects.ImportSubProjectAsync(imported);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(Path.Combine(imported, "images")));
        Assert.True(Directory.Exists(Path.Combine(imported, "output")));
        Assert.Contains(context.SubProjects, sp => sp.Name == "imported");
        Assert.True(await _projects.OpenProjectFromPathAsync(_projects.CurrentProjectPath!));
        Assert.Equal(3, context.SubProjects.Count);
    }

    public void Dispose()
    {
        ProjectContextService.Instance.CloseProject();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class TestWorkspace : IProjectWorkspace
    {
        public List<string> Events { get; } = [];
        public List<ImageEntryDto> Images { get; } = [];
        public List<OpenDocumentDto> Documents { get; } = [];
        public Func<Task> SaveDocuments { get; set; } = () => Task.CompletedTask;
        public bool IsImageOperationRunning { get; set; }
        public bool HasNodeEditor => true;
        public void ClearNodes() => Events.Add("clear-nodes");
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
        public Task RestoreWorkflowAsync(NodeProjectSnapshot snapshot) { Events.Add("workflow"); return Task.CompletedTask; }
        public NodeProjectSnapshot CaptureWorkflow(string projectName) => new() { ProjectName = projectName };
        public List<ImageEntryDto> CaptureImages() => [.. Images];
        public ProjectSettingsDto CaptureSettings() => new();
        public List<OpenDocumentDto> CaptureDocuments(string rootDirectory) { Events.Add("capture-documents"); return [.. Documents]; }
        public Task SaveActiveDocumentAsync() => Task.CompletedTask;
        public Task AutoSaveDocumentsAsync() => SaveDocuments();
        public Task ImportFilesAsync(string[] paths) => Task.CompletedTask;
    }
}