using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ExplorerExpansionStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-explorer-" + Guid.NewGuid().ToString("N"));

    public ExplorerExpansionStateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "alpha", "boot.img"), string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Capture_ExpandedDirectories_ReturnsRootRelativePaths()
    {
        var root = new ExplorerNode(_root, isDirectory: true);
        await root.EnsureChildrenLoadedAsync();
        var alpha = root.Children.Single(node => Path.GetFileName(node.Path) == "alpha");
        alpha.IsExpanded = true;
        await alpha.EnsureChildrenLoadedAsync();
        var nested = alpha.Children.Single(node => Path.GetFileName(node.Path) == "nested");
        nested.IsExpanded = true;
        await nested.EnsureChildrenLoadedAsync();

        var captured = ExplorerExpansionState.Capture([root], _root);

        Assert.Equal(["alpha", "alpha/nested"], captured);
    }

    [Fact]
    public void Capture_CollapsedAndOutsideDirectories_AreSkipped()
    {
        var outside = new ExplorerNode(Path.GetTempPath(), isDirectory: true) { IsExpanded = true };
        var root = new ExplorerNode(_root, isDirectory: true);

        Assert.Empty(ExplorerExpansionState.Capture([root, outside], _root));
    }

    [Fact]
    public async Task Restore_CapturedPath_ExpandsRouteAndLoadsChildren()
    {
        var root = new ExplorerNode(_root, isDirectory: true);

        await ExplorerExpansionState.RestoreAsync([root], _root, ["alpha/nested"]);

        Assert.True(root.IsExpanded);
        var alpha = root.Children.Single(node => Path.GetFileName(node.Path) == "alpha");
        Assert.True(alpha.IsExpanded);
        var nested = alpha.Children.Single(node => Path.GetFileName(node.Path) == "nested");
        Assert.True(nested.IsExpanded);
        Assert.DoesNotContain(root.Children, node => Path.GetFileName(node.Path) == "beta" && node.IsExpanded);
    }

    [Fact]
    public async Task Restore_EscapingOrMissingPaths_LeaveTreeCollapsed()
    {
        var root = new ExplorerNode(_root, isDirectory: true);

        await ExplorerExpansionState.RestoreAsync([root], _root, ["../outside", "missing"]);

        Assert.False(root.IsExpanded);
        Assert.All(root.Children, child => Assert.False(child.IsExpanded));
    }
}