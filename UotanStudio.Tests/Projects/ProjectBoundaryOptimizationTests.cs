using System.IO.Compression;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Projects;

public sealed class ProjectBoundaryOptimizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-boundaries-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ProjectBoundaryOptimizationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task FailedSave_RestoresAllPreviouslyPublishedMetadata()
    {
        var session = new ProjectSession();
        var solution = Path.Combine(_root, "sample.uotan");
        await session.CreateNewSolutionAsync(solution, Token);
        var firmware = session.Context.FirmwareProjectPath!;
        var node = session.Context.NodeProjectPath!;
        var originalFirmware = await File.ReadAllBytesAsync(firmware, Token);
        var originalNode = await File.ReadAllBytesAsync(node, Token);
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Images.Add(new() { RelativePath = "new.img" });
        data.Workflow.Nodes.Add(new() { TypeKey = "Unknown" });
        File.Delete(solution);
        Directory.CreateDirectory(solution); // Publication fails after firmware and node files were published.
        await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(cancellationToken: Token));
        Assert.Equal(originalFirmware, await File.ReadAllBytesAsync(firmware, Token));
        Assert.Equal(originalNode, await File.ReadAllBytesAsync(node, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.transaction-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Package_RoundTripIncludesSolutionUnknownWorkflowAndImages()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        var data = Assert.IsType<ProjectDataWorkspace>(session.Workspace);
        data.Workflow.Nodes.Add(new() { TypeKey = "Vendor.Unknown", InstanceId = "node1" });
        var imagePath = Path.Combine(session.Context.EnsureImagesDirectory(), "boot.img");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4], Token);
        data.Images.Add(new() { RelativePath = session.Context.RelativePath(imagePath)! });
        var archivePath = Path.Combine(_root, "project.zip");
        await session.ExportPackageAsync(archivePath, Token);
        var imported = new ProjectSession();
        Assert.True(await imported.ImportPackageAsync(archivePath, Path.Combine(_root, "imported"), Token));
        var importedData = Assert.IsType<ProjectDataWorkspace>(imported.Workspace);
        Assert.Equal("Vendor.Unknown", Assert.Single(importedData.Workflow.Nodes).TypeKey);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(imported.Context.ResolvePath(Assert.Single(importedData.Images).RelativePath)!, Token));
        Assert.NotEqual(session.CurrentProjectPath, imported.CurrentProjectPath);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("sample/file:stream")]
    [InlineData("sample/../escape")]
    [InlineData("sample/NUL.txt")]
    public async Task Package_InvalidPathsLeaveNoPublishedDestination(string entryName)
    {
        var zip = Path.Combine(_root, "invalid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry(entryName);
        var destination = Path.Combine(_root, "imported");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectPackage.ImportAsync(zip, destination, Token));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, "*.import-*"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task SubProjectReferenceEdits_PreserveFilesAndActiveIdentity()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        var original = session.Context.FirmwareProjectPath!;
        var second = Path.Combine(session.Context.RootDirectory!, "second", "second.uotanfw");
        await session.AddSubProjectAsync(second, Token);
        Assert.True(await session.RenameSubProjectAsync("sample", "renamed", Token));
        Assert.Equal("renamed", session.State.ActiveSubProjectName);
        Assert.Equal(original, session.Context.FirmwareProjectPath);
        Assert.True(await session.RemoveSubProjectAsync("renamed", Token));
        Assert.Equal("second", session.State.ActiveSubProjectName);
        Assert.True(File.Exists(original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RemoveSubProjectAsync("second", Token));
        Assert.True(await session.OpenProjectFromPathAsync(session.CurrentProjectPath!, Token));
        Assert.Equal("second", Assert.Single(session.Context.SubProjects).Name);
    }

    [Fact]
    public async Task AsyncHistory_DoesNotMoveStacksUntilSuccess_AndRejectsConcurrentMutation()
    {
        var history = new ProjectHistory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.RecordAsync("async", null, async _ => { entered.SetResult(); await release.Task; }, _ => Task.CompletedTask);
        var undo = history.UndoAsync(Token);
        await entered.Task.WaitAsync(Token);
        Assert.True(history.IsExecuting);
        Assert.False(history.CanRedo);
        Assert.Throws<InvalidOperationException>(history.Clear);
        release.SetResult();
        Assert.True(await undo);
        Assert.True(history.CanRedo);
        Assert.True(await history.RedoAsync(Token));
        Assert.True(history.CanUndo);
    }

    [Fact]
    public async Task FailedHistoryActions_RetainOriginalStack()
    {
        var history = new ProjectHistory();
        history.Record("sync", () => throw new IOException("failed"), () => { });
        Assert.Throws<IOException>(() => history.Undo());
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        history.Clear();
        history.RecordAsync("async", null, _ => Task.FromException(new OperationCanceledException()), _ => Task.CompletedTask);
        await Assert.ThrowsAsync<OperationCanceledException>(() => history.UndoAsync(Token));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public async Task Copy_CancellationAndCollisionPreserveExistingFiles()
    {
        var source = Path.Combine(_root, "source.img");
        var destination = Path.Combine(_root, "destination.img");
        await File.WriteAllBytesAsync(source, [1, 2, 3], Token);
        await File.WriteAllBytesAsync(destination, [9], Token);
        await Assert.ThrowsAsync<IOException>(() => ProjectFiles.CopyAsync(source, destination, cancellationToken: Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectFiles.CopyAsync(source, destination, true, cancelled.Token));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(destination, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.copy-*"));
    }

    [Fact]
    public async Task OversizedMetadata_IsRejectedBeforeDeserialization()
    {
        var path = Path.Combine(_root, "large.uotan");
        using (var file = File.Create(path)) file.SetLength(32 * 1024 * 1024 + 1);
        Assert.Null(await SolutionPersistenceService.LoadSolutionAsync(path, Token));
    }

    [Fact]
    public void InvalidSolution_DoesNotReplacePublishedContext()
    {
        var context = new ProjectContext();
        context.OpenSolution(Path.Combine(_root, "original.uotan"));
        Assert.Throws<InvalidDataException>(() => context.OpenSolution(Path.Combine(_root, "invalid.uotan"), new() { FormatVersion = 999 }));
        Assert.Equal("original", context.SolutionName);
    }

    [Fact]
    public async Task FailedRegistration_DoesNotPublishNewFirmwareFile()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        var firmware = Path.Combine(session.Context.RootDirectory!, "second", "second.uotanfw");
        await File.WriteAllTextAsync(session.CurrentProjectPath!, "invalid json", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.AddSubProjectAsync(firmware, Token));
        Assert.False(File.Exists(firmware));
        Assert.Equal("sample", Assert.Single(session.Context.SubProjects).Name);
    }

    [Fact]
    public async Task AsyncHistoryFactory_ReconstructsAwaitableActions()
    {
        var source = new ProjectHistory();
        source.Record("saved", new() { Description = "saved" }, () => { }, () => { });
        var path = Path.Combine(_root, "history.json");
        await source.SaveAsync(path, Token);
        var calls = 0;
        var target = new ProjectHistory
        {
            ReconstructAsyncOperation = _ => (async token => { await Task.Delay(1, token); calls++; }, _ => Task.CompletedTask)
        };
        await target.LoadAsync(path, Token);
        Assert.True(await target.UndoAsync(Token));
        Assert.Equal(1, calls);
        Assert.True(target.CanRedo);
    }

    [Fact]
    public void ContextDirectoryFailure_DoesNotPublishPartialState()
    {
        var context = new ProjectContext();
        context.OpenSolution(Path.Combine(_root, "original.uotan"));
        File.WriteAllText(Path.Combine(_root, "blocked"), "file instead of directory");
        Assert.Throws<IOException>(() => context.OpenSolution(Path.Combine(_root, "blocked.uotan")));
        Assert.Equal("original", context.SolutionName);
        Assert.EndsWith("original.uotan", context.CurrentSolutionPath);
    }

    [Fact]
    public async Task PackageIndex_RejectsOversizedCentralDirectoryBeforeAllocation()
    {
        var path = Path.Combine(_root, "oversized.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) archive.CreateEntry("small.txt");
        var bytes = await File.ReadAllBytesAsync(path, Token);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 12), 32 * 1024 * 1024 + 1);
        await File.WriteAllBytesAsync(path, bytes, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectPackage.ImportAsync(path, Path.Combine(_root, "imported"), Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "imported")));
    }
}