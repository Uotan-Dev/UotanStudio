using System.Security.Cryptography;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginAssetStoreTests
{
    [Fact]
    public async Task CopyIntoProjectAsync_NewFile_CopiesAnchorsAndKeepsHash()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(root);
        var source = Path.Combine(temp.Path, "config.xml");
        var bytes = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);

        var asset = await VisualPluginAssetStore.CopyIntoProjectAsync("config", source, root, "tests.plugin",
            TestContext.Current.CancellationToken);

        var expected = Path.Combine(root, ".uotan", "plugin-assets", "tests.plugin", "config.xml");
        Assert.Equal(expected, asset.SourcePath);
        Assert.Equal(".uotan/plugin-assets/tests.plugin/config.xml", asset.RelativePath);
        Assert.Equal(expected, asset.ResolveSourcePath(root));
        Assert.Equal(bytes.LongLength, asset.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CopyIntoProjectAsync_SameNameAndContent_ReusesExistingFile()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(root);
        var source = Path.Combine(temp.Path, "config.xml");
        await File.WriteAllTextAsync(source, "same content", TestContext.Current.CancellationToken);

        var first = await VisualPluginAssetStore.CopyIntoProjectAsync("config", source, root, "tests.plugin",
            TestContext.Current.CancellationToken);
        var second = await VisualPluginAssetStore.CopyIntoProjectAsync("other", source, root, "tests.plugin",
            TestContext.Current.CancellationToken);

        Assert.Equal(first.SourcePath, second.SourcePath);
        Assert.Equal(first.RelativePath, second.RelativePath);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, ".uotan", "plugin-assets", "tests.plugin")));
    }

    [Fact]
    public async Task CopyIntoProjectAsync_SameNameDifferentContent_DoesNotOverwriteAndUsesDistinctPath()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(root);
        var source = Path.Combine(temp.Path, "config.xml");
        await File.WriteAllTextAsync(source, "original content", TestContext.Current.CancellationToken);
        var first = await VisualPluginAssetStore.CopyIntoProjectAsync("config", source, root, "tests.plugin",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(source, "replacement content", TestContext.Current.CancellationToken);

        var second = await VisualPluginAssetStore.CopyIntoProjectAsync("replacement", source, root, "tests.plugin",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first.SourcePath, second.SourcePath);
        Assert.Equal("original content", await File.ReadAllTextAsync(first.SourcePath!,
            TestContext.Current.CancellationToken));
        Assert.Equal("replacement content", await File.ReadAllTextAsync(second.SourcePath!,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(root, ".uotan", "plugin-assets", "tests.plugin")).Count());
    }

    [Fact]
    public async Task CopyIntoProjectAsync_Cancelled_LeavesNoPartialOrTemporaryFile()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(root);
        var source = Path.Combine(temp.Path, "config.xml");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VisualPluginAssetStore.CopyIntoProjectAsync("config", source, root, "tests.plugin", cancellation.Token));

        var directory = Path.Combine(root, ".uotan", "plugin-assets", "tests.plugin");
        if (Directory.Exists(directory))
        {
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
    }

    [Fact]
    public void WithProjectLocation_OutsideCopy_IsRejected()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        var outside = Path.Combine(temp.Path, "outside.bin");
        Directory.CreateDirectory(root);
        var identity = new VisualPluginAsset
        {
            Id = "config",
            Name = "outside.bin",
            Length = 0,
            Sha256 = new string('0', 64)
        };

        Assert.Throws<ArgumentException>(() => VisualPluginAsset.WithProjectLocation(identity, outside, root));
    }

    [Fact]
    public void ResolveSourcePath_MovedProjectAndEscapingAnchor_UseAnchorInsideRootOnly()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        var moved = Path.Combine(temp.Path, "moved");
        var asset = new VisualPluginAsset
        {
            Id = "config",
            Name = "config.bin",
            Length = 0,
            Sha256 = new string('0', 64),
            RelativePath = ".uotan/plugin-assets/tests.plugin/config.bin"
        };

        Assert.Equal(Path.Combine(moved, ".uotan", "plugin-assets", "tests.plugin", "config.bin"),
            asset.ResolveSourcePath(moved));
        Assert.Equal(Path.Combine(root, ".uotan", "plugin-assets", "tests.plugin", "config.bin"),
            asset.ResolveSourcePath(root));

        asset.RelativePath = "../escape.bin";
        Assert.Null(asset.ResolveSourcePath(root));
    }

    [Fact]
    public async Task CopyIntoProjectAsync_InvalidPluginId_IsRejectedBeforeCopying()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(root);
        var source = Path.Combine(temp.Path, "config.xml");
        await File.WriteAllTextAsync(source, "content", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            VisualPluginAssetStore.CopyIntoProjectAsync("config", source, root, "../escape",
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(root, ".uotan")));
    }
}