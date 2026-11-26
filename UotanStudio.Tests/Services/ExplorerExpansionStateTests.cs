using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ExplorerExpansionStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-explorer-expand-" + Guid.NewGuid().ToString("N"));

    public ExplorerExpansionStateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "images", "boot"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Capture_CollapsedDirectory_OmitsItsPreviouslyExpandedDescendants()
    {
        var (images, boot) = BuildTree();
        boot.IsExpanded = true;
        images.IsExpanded = false;

        var captured = ExplorerExpansionState.Capture([images], _root);

        Assert.Empty(captured);
    }

    [Fact]
    public void Capture_ExpandedDirectory_ListsItselfAndItsExpandedDescendants()
    {
        var (images, boot) = BuildTree();
        images.IsExpanded = true;
        boot.IsExpanded = true;

        var captured = ExplorerExpansionState.Capture([images], _root);

        Assert.Equal(["images", "images/boot"], captured);
    }

    [Fact]
    public async Task RestoreAsync_FolderExpandedThenCollapsed_BringsBackTheCollapsedShape()
    {
        var (source, sourceBoot) = BuildTree();
        source.IsExpanded = true;
        sourceBoot.IsExpanded = true;

        source.IsExpanded = false;
        var captured = ExplorerExpansionState.Capture([source], _root);

        var (restored, restoredBoot) = BuildTree();
        await ExplorerExpansionState.RestoreAsync([restored], _root, captured);

        Assert.Empty(captured);
        Assert.False(restored.IsExpanded);
        Assert.False(restoredBoot.IsExpanded);
    }

    private (ExplorerNode Images, ExplorerNode Boot) BuildTree()
    {
        var images = BuildDirectory("images");
        var boot = BuildDirectory("images/boot");
        images.Children.Add(boot);
        return (images, boot);
    }

    private ExplorerNode BuildDirectory(string relative)
    {
        var node = new ExplorerNode(
            Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), isDirectory: true);
        node.Children.Clear();
        return node;
    }
}