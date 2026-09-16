using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>Verifies metadata and persisted state ownership across sub-project changes.</summary>
[Collection("Workspace state")]
public sealed class SubProjectMetadataIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-owner-" + Guid.NewGuid().ToString("N"));
    private readonly string? _ambient = RepackMetadata.DefaultMetadataRoot;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public SubProjectMetadataIsolationTests() => Directory.CreateDirectory(_root);

    /// <summary>A package destination outside the project root (the flat root is the solution's directory).</summary>
    private string OutsideRoot(string name) => Path.Combine(Path.GetTempPath(), Path.GetFileName(_root) + "-" + name);

    private async Task<ProjectSession> CreateAsync()
    {
        var session = new ProjectSession();
        await session.CreateNewSolutionAsync(Path.Combine(_root, "sample.uotan"), Token);
        await session.AddSubProjectAsync(Path.Combine(session.Context.RootDirectory!, "other", "other.uotanfw"), Token);
        return session;
    }

    [Fact]
    public async Task MetadataRoots_FollowOwnerAndNeverCreateSolutionState()
    {
        var session = await CreateAsync();
        var context = session.Context;
        var first = context.SubProjects.Single(p => p.Name == "sample");
        var second = context.SubProjects.Single(p => p.Name == "other");
        Assert.Equal(first.MetadataRoot, context.GetMetadataRoot());
        Assert.True(await session.SwitchSubProjectAsync("other", cancellationToken: Token));
        Assert.Equal(second.MetadataRoot, context.GetMetadataRoot());
        Assert.Equal(first.MetadataRoot, context.GetMetadataRoot(Path.Combine(first.ImagesDir, "system.img")));
        Assert.Null(context.GetMetadataRoot(first.RootPath + "-unrelated/system.img"));
        Assert.Equal(second.StateDirectory, context.StateDirectory);
        Assert.False(Directory.Exists(Path.Combine(context.RootDirectory!, ".uotan")));
        context.CloseProject();
        Assert.Null(context.GetMetadataRoot());
        Assert.Null(context.StateDirectory);
    }

    [Fact]
    public async Task History_SaveAsAndPackage_KeepChildrenIndependent()
    {
        var session = await CreateAsync();
        var first = session.Context.ActiveSubProject!;
        session.History.Record("first", new() { Description = "first" }, () => { }, () => { });
        WriteMetadata(Path.Combine(first.MetadataRoot, "system"), "first");
        Assert.True(await session.SwitchSubProjectAsync("other", cancellationToken: Token));
        Assert.Empty(session.History.CaptureSnapshot());
        var second = session.Context.ActiveSubProject!;
        session.History.Record("second", new() { Description = "second" }, () => { }, () => { });
        WriteMetadata(Path.Combine(second.MetadataRoot, "system"), "second");
        Assert.True(await session.SwitchSubProjectAsync("sample", cancellationToken: Token));
        Assert.Equal("first", Assert.Single(session.History.CaptureSnapshot()).Description);
        Assert.True(await session.SwitchSubProjectAsync("other", cancellationToken: Token));
        Assert.Equal("second", Assert.Single(session.History.CaptureSnapshot()).Description);
        await session.SaveAsAsync(Path.Combine(_root, "copy.uotan"), Token);
        Assert.False(Directory.Exists(Path.Combine(session.Context.RootDirectory!, ".uotan")));
        foreach (var child in session.Context.SubProjects)
        {
            Assert.True(File.Exists(Path.Combine(child.StateDirectory, "settings.json")));
            Assert.Equal(child.Name == "sample" ? "first" : "second",
                RepackMetadata.ReadFromAbsolute(Path.Combine(child.MetadataRoot, "system", RepackMetadata.FileName))!.PackingNotes);
        }
        var zip = OutsideRoot("package.zip");
        await session.ExportPackageAsync(zip, Token);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith("/history.json", StringComparison.Ordinal));
            Assert.Equal(2, archive.Entries.Count(e => e.FullName.EndsWith(RepackMetadata.FileName, StringComparison.Ordinal)));
        }
        var imported = await ProjectPackage.ImportAsync(zip, Path.Combine(_root, "imported"), Token);
        var reopened = new ProjectSession();
        Assert.True(await reopened.OpenProjectFromPathAsync(imported, Token));
        Assert.False(Directory.Exists(Path.Combine(reopened.Context.RootDirectory!, ".uotan")));
        Assert.Empty(reopened.History.CaptureSnapshot());
        Assert.Equal(2, reopened.Context.SubProjects.Count);
    }

    [Fact]
    public async Task DesktopReadAndPack_UseDirectoryOwnerEvenWhenOtherChildIsActive()
    {
        var session = await CreateAsync();
        var desktop = ProjectContextService.Instance;
        desktop.OpenSolution(session.CurrentProjectPath!, await SolutionPersistenceService.LoadSolutionAsync(session.CurrentProjectPath!, Token));
        var first = desktop.SubProjects.Single(p => p.Name == "sample");
        var second = desktop.SubProjects.Single(p => p.Name == "other");
        var extraction = Path.Combine(first.RootPath, "system");
        Directory.CreateDirectory(extraction);
        WriteMetadata(Path.Combine(first.MetadataRoot, "system"), "first-owner");
        WriteMetadata(Path.Combine(second.MetadataRoot, "system"), "wrong-owner");
        desktop.SetActiveSubProject("other");
        Assert.Equal(Path.Combine(first.MetadataRoot, "system", RepackMetadata.FileName),
            new ExplorerNode(extraction, true).RepackConfigurationPath);
        Assert.Equal("first-owner", PackingService.BuildImage(extraction, Path.Combine(_root, "output.img")).Summary);
        File.Delete(Path.Combine(first.MetadataRoot, "system", RepackMetadata.FileName));
        WriteMetadata(extraction, "wrong-local");
        Assert.Null(new ExplorerNode(extraction, true).RepackConfigurationPath);
    }

    [Fact]
    public async Task FilesystemPreservation_SurvivesProjectPackageImportWithoutOriginalImage()
    {
        var session = await CreateAsync();
        var child = session.Context.ActiveSubProject!;
        var source = Path.Combine(_root, "source-tree");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "data"), "preserved payload");
        var image = Path.Combine(_root, "system.img");
        new ErofsBuilder().Build(source, image);
        byte[] expected;
        using (var stream = File.OpenRead(image)) expected = SHA256.HashData(stream);
        var handler = new ErofsFormatHandler();
        using (RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(child.MetadataRoot)))
        {
            var result = await handler.UnpackAsync(image, Path.Combine(child.RootPath, "system"), cancellationToken: Token);
            Assert.True(result.Success, result.Summary);
        }
        File.Delete(image);
        var package = OutsideRoot("preserved.zip");
        await session.ExportPackageAsync(package, Token);
        var importedPath = await ProjectPackage.ImportAsync(package, Path.Combine(_root, "imported-preserved"), Token);
        var imported = new ProjectSession();
        Assert.True(await imported.OpenProjectFromPathAsync(importedPath, Token));
        var restoredChild = imported.Context.ActiveSubProject!;
        using (RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(restoredChild.MetadataRoot)))
        {
            var output = Path.Combine(_root, "restored.img");
            var result = await handler.PackAsync(Path.Combine(restoredChild.RootPath, "system"), output, Token);
            Assert.True(result.Success, result.Summary);
            using var stream = File.OpenRead(output);
            Assert.Equal(expected, SHA256.HashData(stream));
        }
    }

    [Fact]
    public async Task ServiceExtraction_WritesOnlyToOutputOwner()
    {
        var session = await CreateAsync();
        var desktop = ProjectContextService.Instance;
        desktop.OpenSolution(session.CurrentProjectPath!, await SolutionPersistenceService.LoadSolutionAsync(session.CurrentProjectPath!, Token));
        var first = desktop.SubProjects.Single(p => p.Name == "sample");
        var second = desktop.SubProjects.Single(p => p.Name == "other");
        var source = Path.Combine(_root, "input");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hello"), "content");
        var image = Path.Combine(first.ImagesDir, "ramdisk.cpio");
        var service = FirmwareService.Instance;
        var packed = await service.PackCpioAsync(source, image, Token);
        Assert.True(packed.Success, packed.Summary);
        desktop.SetActiveSubProject("other");
        var output = Path.Combine(first.RootPath, "ramdisk");
        var unpacked = await service.UnpackCpioAsync(image, output);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal("content", File.ReadAllText(Path.Combine(output, "hello")));
        Assert.True(File.Exists(Path.Combine(first.MetadataRoot, "ramdisk", RepackMetadata.FileName)));
        Assert.False(File.Exists(Path.Combine(second.MetadataRoot, "ramdisk", RepackMetadata.FileName)));
        Assert.False(File.Exists(Path.Combine(output, RepackMetadata.FileName)));
    }

    [Fact]
    public async Task ConcurrentScopes_SurviveAwaitGlobalChangesAndNestedLocalMode()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task CheckAsync(string owner)
        {
            using var scope = RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(owner));
            if (Interlocked.Increment(ref entered) == 2) ready.SetResult();
            await gate.Task.WaitAsync(Token);
            Assert.Equal(owner, await Task.Run(() => RepackMetadata.DefaultMetadataRoot, Token));
            using (RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory))
            {
                await Task.Yield();
                Assert.Null(RepackMetadata.DefaultMetadataRoot);
            }
            Assert.Equal(owner, RepackMetadata.DefaultMetadataRoot);
        }
        var first = CheckAsync(Path.Combine(_root, "first"));
        var second = CheckAsync(Path.Combine(_root, "second"));
        await ready.Task.WaitAsync(Token);
        RepackMetadata.DefaultMetadataRoot = "changed-global";
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal("changed-global", RepackMetadata.DefaultMetadataRoot);
    }

    [Fact]
    public void Sidecars_UseExactSourcePartitionAndNeverSearchParent()
    {
        var directory = Path.Combine(_root, "system");
        WriteMetadata(directory, "local");
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        File.WriteAllText(Path.Combine(directory, "vendor_fs_config"), "unrelated");
        Assert.Null(RepackMetadata.DiscoverMetadataFile(directory, "_fs_config"));
        var expected = Path.Combine(directory, "system_fs_config");
        File.WriteAllText(expected, "original");
        Assert.Equal(expected, RepackMetadata.DiscoverMetadataFile(directory, "_fs_config"));
        Directory.CreateDirectory(Path.Combine(directory, "child"));
        Assert.Null(RepackMetadata.DiscoverMetadataFile(Path.Combine(directory, "child"), "_fs_config"));
    }

    private static void WriteMetadata(string directory, string marker)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, RepackMetadata.FileName),
            $$"""{"schema":"UotanStudio.Firmware.Repack","version":1,"formatName":"EXT4","sourceFileName":"system.img","packingNotes":"{{marker}}"}""");
    }

    public void Dispose()
    {
        ProjectContextService.Instance.CloseProject();
        RepackMetadata.DefaultMetadataRoot = _ambient;
        Directory.Delete(_root, true);
    }
}