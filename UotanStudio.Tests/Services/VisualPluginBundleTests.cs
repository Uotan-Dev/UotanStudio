using System.Buffers.Binary;
using System.IO.Compression;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginBundleTests
{
    [Fact]
    public async Task Manager_FailedMigration_RestoresJsonAndRemovesNewBundle()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Manifest.Id = "tests.failed-migration";
        var json = Path.Combine(temp.Path, "source.json");
        await VisualPluginFile.SaveAsync(json, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"));
        var original = await manager.InstallAsync(json, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(original.FilePath, TestContext.Current.CancellationToken);
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));
        package.Manifest.RequiredNodeTypes.Add("VisualPlugin.unavailable");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.UpdateAsync(original, package, TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(original.FilePath, TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(manager.Registrations).IsAvailable);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(original.FilePath)!, "*.uotanplugin"));
    }

    [Fact]
    public async Task Manager_UpdateJsonWithResource_MigratesToSingleUsableBundle()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "installed");
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        var json = Path.Combine(temp.Path, "source.json");
        await VisualPluginFile.SaveAsync(json, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(directory);
        var original = await manager.InstallAsync(json, TestContext.Current.CancellationToken);
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));

        var updated = await manager.UpdateAsync(original, package, TestContext.Current.CancellationToken);

        Assert.Single(manager.Registrations);
        Assert.EndsWith(".uotanplugin", updated.FilePath, StringComparison.Ordinal);
        Assert.False(File.Exists(original.FilePath));
        await VisualPluginBundle.CopyAssetAsync(updated.Package, "resource", Stream.Null, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.backup"));
    }

    [Fact]
    public async Task Manager_UnavailableBundleUpdate_RestoresExactArchiveAndResources()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Manifest.Id = "tests.bundle-rollback";
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));
        var bundle = Path.Combine(temp.Path, "source.uotanplugin");
        await VisualPluginBundle.SaveAsync(bundle, package, TestContext.Current.CancellationToken);
        var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"));
        var original = await manager.InstallAsync(bundle, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(original.FilePath, TestContext.Current.CancellationToken);
        var replacement = VisualPluginBundle.Load(original.FilePath);
        replacement.Manifest.RequiredNodeTypes.Add("VisualPlugin.missing-dependency");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.UpdateAsync(original, replacement, TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(original.FilePath, TestContext.Current.CancellationToken));
        var restored = Assert.Single(manager.Registrations);
        Assert.True(restored.IsAvailable);
        await VisualPluginBundle.CopyAssetAsync(restored.Package, "resource", Stream.Null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LoadAsync_LegacyVersionOneJson_RemainsSupported()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "legacy.json");
        await File.WriteAllTextAsync(path,
            """{"FormatVersion":1,"Manifest":{"Id":"legacy.patch","Name":"Legacy"}}""",
            TestContext.Current.CancellationToken);

        var package = await VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(1, package.FormatVersion);
        Assert.Empty(package.Assets);
    }

    [Fact]
    public async Task SaveAsync_Cancelled_PreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VisualPluginBundle.SaveAsync(path, CreatePackage(), cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_ResourceBundle_LoadsAndCopiesExactBytes()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "config.xml");
        var bytes = Enumerable.Range(0, 400000).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("config", source, TestContext.Current.CancellationToken));
        var path = Path.Combine(temp.Path, "patch.uotanplugin");

        await VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var loaded = await VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await VisualPluginBundle.CopyAssetAsync(loaded, "config", output, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, output.ToArray());
        Assert.Null(Assert.Single(loaded.Assets).SourcePath);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var manifest = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SourcePath", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("BundlePath", manifest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_SourceChanged_PreservesExistingBundle(bool changeLength)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(source, changeLength ? "longer replacement" : "modified", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VisualPluginBundle.SaveAsync(path, package, TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task CopyAssetAsync_TamperedArchive_RejectsHashMismatch()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, package, TestContext.Current.CancellationToken);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry("assets/resource")!.Delete();
            await using var entry = archive.CreateEntry("assets/resource").Open();
            await entry.WriteAsync("modified"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        var loaded = VisualPluginBundle.Load(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VisualPluginBundle.CopyAssetAsync(loaded, "resource", Stream.Null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("manifest.json")]
    [InlineData("assets/undeclared")]
    public async Task Load_UnexpectedOrDuplicateEntry_RejectsBundle(string name)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, CreatePackage(), TestContext.Current.CancellationToken);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry(name);
        }

        Assert.Throws<InvalidDataException>(() => VisualPluginBundle.Load(path));
    }

    [Fact]
    public async Task Load_ForgedDirectoryCount_RejectsBeforeArchiveEnumeration()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, CreatePackage(), TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 14), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), 65535);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        Assert.Throws<InvalidDataException>(() => VisualPluginBundle.Load(path));
    }

    [Fact]
    public async Task SaveAsync_LoadedBundle_CanReplaceItsOwnArchive()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "resource");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);
        var package = CreatePackage();
        package.Assets.Add(await VisualPluginAsset.FromFileAsync("resource", source, TestContext.Current.CancellationToken));
        var path = Path.Combine(temp.Path, "patch.uotanplugin");
        await VisualPluginBundle.SaveAsync(path, package, TestContext.Current.CancellationToken);
        var loaded = VisualPluginBundle.Load(path);
        loaded.Manifest.Version = "2.0.0";

        await VisualPluginBundle.SaveAsync(path, loaded, TestContext.Current.CancellationToken);
        var updated = VisualPluginBundle.Load(path);
        await VisualPluginBundle.CopyAssetAsync(updated, "resource", Stream.Null, TestContext.Current.CancellationToken);

        Assert.Equal("2.0.0", updated.Manifest.Version);
    }

    private static VisualPluginPackage CreatePackage() => new()
    {
        Kind = VisualPluginKind.CompositeNode,
        Manifest = new VisualPluginManifest { Id = "tests.resources", Name = "Resources" }
    };
}