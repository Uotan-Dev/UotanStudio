using System.Security.Cryptography;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>
/// Scheme asset materialization: relative-path containment, length and digest guards, atomic copies, overwrite
/// protection, project-owned destinations and fallback resolution.
/// <para>Scheme 资产物化：相对路径约束、长度与摘要校验、原子复制、覆盖保护、工程目标目录与回退解析。</para>
/// </summary>
public sealed class SchemeAssetStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _sourceDirectory;
    private readonly string _destinationDirectory;

    public SchemeAssetStoreTests()
    {
        _sourceDirectory = Path.Combine(_temp.Path, "source");
        _destinationDirectory = Path.Combine(_temp.Path, "destination");
        Directory.CreateDirectory(_sourceDirectory);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task InspectAsync_ValidAssets_ReportsDigestsAndResolvedPaths()
    {
        var token = TestContext.Current.CancellationToken;
        var first = NewAsset("alpha.bin", "alpha"u8.ToArray());
        var second = NewAsset("nested/beta.bin", "beta"u8.ToArray());
        var package = new SchemePackage { Id = "inspect", Name = "Inspect", Assets = [first, second] };

        var checks = await SchemeAssetStore.InspectAsync(package, _sourceDirectory, cancellationToken: token);

        Assert.Equal(2, checks.Count);
        Assert.All(checks, check =>
        {
            Assert.Equal(SchemeAssetState.Valid, check.State);
            Assert.True(check.IsValid);
            Assert.Null(check.Error);
            Assert.NotNull(check.SourcePath);
            Assert.True(File.Exists(check.SourcePath));
        });
        Assert.EndsWith("alpha.bin", checks[0].SourcePath!, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("nested", "beta.bin"), checks[1].SourcePath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_MissingAsset_ReportsMissing()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "missing",
            Name = "Missing",
            Assets = [new SchemeAsset { Id = "missing", Name = "Missing", RelativePath = "absent.bin" }]
        };

        var check = Assert.Single(await SchemeAssetStore.InspectAsync(package, _sourceDirectory,
            cancellationToken: token));

        Assert.Equal(SchemeAssetState.Missing, check.State);
        Assert.Contains("missing", check.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsync_HashMismatch_ReportsHashMismatch()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = NewAsset("hash.bin", "content"u8.ToArray());
        asset.Sha256 = new string('0', 64);
        var package = new SchemePackage { Id = "hash", Name = "Hash", Assets = [asset] };

        var check = Assert.Single(await SchemeAssetStore.InspectAsync(package, _sourceDirectory,
            cancellationToken: token));

        Assert.Equal(SchemeAssetState.HashMismatch, check.State);
    }

    [Fact]
    public async Task InspectAsync_LengthMismatch_ReportsLengthMismatch()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = NewAsset("length.bin", "content"u8.ToArray());
        asset.Length -= 1;
        var package = new SchemePackage { Id = "length", Name = "Length", Assets = [asset] };

        var check = Assert.Single(await SchemeAssetStore.InspectAsync(package, _sourceDirectory,
            cancellationToken: token));

        Assert.Equal(SchemeAssetState.LengthMismatch, check.State);
    }

    [Fact]
    public async Task InspectAsync_EscapingRelativePath_ReportsUnsafePath()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "unsafe",
            Name = "Unsafe",
            Assets = [new SchemeAsset { Id = "escape", Name = "Escape", RelativePath = "../escape.bin" }]
        };

        var check = Assert.Single(await SchemeAssetStore.InspectAsync(package, _sourceDirectory,
            cancellationToken: token));

        Assert.Equal(SchemeAssetState.UnsafePath, check.State);
    }

    [Fact]
    public async Task InspectAsync_FallbackDirectory_ResolvesProjectOwnedAsset()
    {
        var token = TestContext.Current.CancellationToken;
        var fallback = Path.Combine(_temp.Path, "project");
        var relative = ".uotan/scheme-assets/scheme/file.bin";
        var file = Path.Combine(fallback, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, "fallback"u8.ToArray(), token);
        var asset = NewAsset("unused.bin", "fallback"u8.ToArray());
        asset.RelativePath = relative;
        var package = new SchemePackage { Id = "fallback", Name = "Fallback", Assets = [asset] };

        var check = Assert.Single(await SchemeAssetStore.InspectAsync(package, _sourceDirectory, fallback,
            token));

        Assert.Equal(SchemeAssetState.Valid, check.State);
        Assert.Equal(Path.GetFullPath(file), check.SourcePath);
    }

    [Fact]
    public async Task MaterializeAsync_CopiesRelativePathsAndResolvesAssetIds()
    {
        var token = TestContext.Current.CancellationToken;
        var first = NewAsset("alpha.bin", "alpha"u8.ToArray(), "first");
        var second = NewAsset("nested/beta.bin", "beta"u8.ToArray(), "second");
        var package = new SchemePackage { Id = "materialize", Name = "Materialize", Assets = [first, second] };

        using var store = await SchemeAssetStore.MaterializeAsync(package, _sourceDirectory,
            _destinationDirectory, cancellationToken: token);

        var firstPath = Assert.IsType<string>(store.Resolve("first"));
        var secondPath = Assert.IsType<string>(store.Resolve("second"));
        Assert.Equal(Path.GetFullPath(Path.Combine(_destinationDirectory, "alpha.bin")), firstPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_destinationDirectory, "nested", "beta.bin")), secondPath);
        Assert.Equal("alpha"u8.ToArray(), await File.ReadAllBytesAsync(firstPath, token));
        Assert.Null(store.Resolve("unknown"));
    }

    [Fact]
    public async Task MaterializeAsync_Dispose_RemovesOwnedDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "cleanup",
            Name = "Cleanup",
            Assets = [NewAsset("alpha.bin", "alpha"u8.ToArray())]
        };
        var store = await SchemeAssetStore.MaterializeAsync(package, _sourceDirectory, _destinationDirectory,
            cancellationToken: token);

        store.Dispose();

        Assert.False(Directory.Exists(_destinationDirectory));
    }

    [Fact]
    public async Task MaterializeAsync_DeleteOnDisposeFalse_KeepsDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "keep",
            Name = "Keep",
            Assets = [NewAsset("alpha.bin", "alpha"u8.ToArray())]
        };
        var store = await SchemeAssetStore.MaterializeAsync(package, _sourceDirectory, _destinationDirectory,
            deleteOnDispose: false, cancellationToken: token);

        store.Dispose();

        Assert.True(File.Exists(Path.Combine(_destinationDirectory, "alpha.bin")));
    }

    [Fact]
    public async Task MaterializeAsync_MissingAsset_ThrowsAndLeavesNoDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "missing-materialize",
            Name = "Missing",
            Assets = [new SchemeAsset { Id = "missing", Name = "Missing", RelativePath = "absent.bin" }]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => SchemeAssetStore.MaterializeAsync(package,
            _sourceDirectory, _destinationDirectory, cancellationToken: token));

        Assert.False(Directory.Exists(_destinationDirectory));
    }

    [Fact]
    public async Task MaterializeAsync_DifferentContentAtDestination_ThrowsWithoutOverwriting()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_destinationDirectory);
        var target = Path.Combine(_destinationDirectory, "alpha.bin");
        await File.WriteAllBytesAsync(target, "different"u8.ToArray(), token);
        var package = new SchemePackage
        {
            Id = "overwrite",
            Name = "Overwrite",
            Assets = [NewAsset("alpha.bin", "alpha"u8.ToArray())]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => SchemeAssetStore.MaterializeAsync(package,
            _sourceDirectory, _destinationDirectory, cancellationToken: token));

        Assert.Equal("different"u8.ToArray(), await File.ReadAllBytesAsync(target, token));
    }

    [Fact]
    public async Task MaterializeAsync_IdenticalDestinationContent_ReusesTheFile()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_destinationDirectory);
        var target = Path.Combine(_destinationDirectory, "alpha.bin");
        await File.WriteAllBytesAsync(target, "alpha"u8.ToArray(), token);
        var asset = NewAsset("alpha.bin", "alpha"u8.ToArray());
        var package = new SchemePackage { Id = "reuse", Name = "Reuse", Assets = [asset] };

        using var store = await SchemeAssetStore.MaterializeAsync(package, _sourceDirectory,
            _destinationDirectory, cancellationToken: token);

        Assert.Equal(Path.GetFullPath(target), store.Resolve(asset.Id));
        Assert.Equal(asset.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(target, token))));
    }

    [Fact]
    public async Task ValidateAsync_ProvidedPaths_ChecksDeclaredDigest()
    {
        var token = TestContext.Current.CancellationToken;
        var good = Path.Combine(_sourceDirectory, "good.bin");
        await File.WriteAllBytesAsync(good, "good"u8.ToArray(), token);
        var asset = NewAsset("good.bin", "good"u8.ToArray());
        var package = new SchemePackage { Id = "paths", Name = "Paths", Assets = [asset] };
        Dictionary<string, string> paths = new(StringComparer.Ordinal) { [asset.Id] = good };

        var check = Assert.Single(await SchemeAssetStore.ValidateAsync(package, paths, token));

        Assert.Equal(SchemeAssetState.Valid, check.State);

        paths.Clear();

        var missing = Assert.Single(await SchemeAssetStore.ValidateAsync(package, paths, token));

        Assert.Equal(SchemeAssetState.Missing, missing.State);
    }

    [Fact]
    public async Task ImportAsync_CopiesIntoProjectDirectoryAndComputesDigest()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_temp.Path, "picked.bin");
        var content = "picked content"u8.ToArray();
        await File.WriteAllBytesAsync(source, content, token);
        var stateDirectory = Path.Combine(_temp.Path, "project", ".uotan");
        var destination = SchemeAssetStore.GetProjectAssetDirectory(stateDirectory, "scheme-id");

        var imported = await SchemeAssetStore.ImportAsync(source, destination, "asset1", token);

        var expected = Path.Combine(Path.GetFullPath(destination), "picked.bin");
        Assert.Equal(expected, imported.Path);
        Assert.True(File.Exists(expected));
        Assert.Equal("asset1", imported.Asset.Id);
        Assert.Equal("picked", imported.Asset.Name);
        Assert.Equal((long)content.Length, imported.Asset.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), imported.Asset.Sha256);
    }

    [Fact]
    public void GetProjectAssetDirectory_PlacesAssetsUnderTheSubProjectState()
    {
        var stateDirectory = Path.Combine(_temp.Path, "project", ".uotan");

        var directory = SchemeAssetStore.GetProjectAssetDirectory(stateDirectory, "My Scheme/1");

        Assert.Equal(Path.Combine(Path.GetFullPath(stateDirectory), "scheme-assets", "my_scheme_1"), directory);
    }

    [Fact]
    public void TryGetContainedRelativePath_OutsideDirectory_ReturnsNull()
    {
        var root = Path.Combine(_temp.Path, "root");
        var outside = Path.Combine(_temp.Path, "outside.bin");

        Assert.Null(SchemeAssetStore.TryGetContainedRelativePath(root, outside));
        Assert.Equal("nested/file.bin",
            SchemeAssetStore.TryGetContainedRelativePath(root, Path.Combine(root, "nested", "file.bin")));
    }

    [Fact]
    public void CreateRunDirectory_UsesTheSuppliedParent()
    {
        var parent = Path.Combine(_temp.Path, "runs");

        var directory = SchemeAssetStore.CreateRunDirectory(parent, "scheme-id");

        Assert.True(Directory.Exists(directory));
        Assert.Equal(Path.GetFullPath(parent), Path.GetDirectoryName(directory));
        Assert.StartsWith("scheme-id-run-", Path.GetFileName(directory), StringComparison.Ordinal);
    }

    private SchemeAsset NewAsset(string relativePath, byte[] content, string? id = null)
    {
        var full = Path.Combine(_sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return new SchemeAsset
        {
            Id = id ?? Path.GetFileNameWithoutExtension(relativePath),
            Name = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Length = content.Length
        };
    }
}