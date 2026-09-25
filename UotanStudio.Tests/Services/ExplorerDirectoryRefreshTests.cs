using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ExplorerDirectoryRefreshTests
{
    [Fact]
    public void Refresh_RemovesDeletedEntryWithoutReplacingLoadedAncestors()
    {
        using var temp = new TempDirectory();
        var subdirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "project", "images"));
        var deleted = Path.Combine(subdirectory.FullName, "deleted.img");
        var retained = Path.Combine(subdirectory.FullName, "retained.img");
        File.WriteAllText(deleted, "deleted");
        File.WriteAllText(retained, "retained");
        var root = new ExplorerNode(temp.Path, true);
        root.EnsureChildrenLoaded();
        var project = Assert.Single(root.Children);
        project.EnsureChildrenLoaded();
        var images = Assert.Single(project.Children);
        images.EnsureChildrenLoaded();
        var existing = Assert.Single(images.Children, c => c.Path == retained);
        File.Delete(deleted);
        var added = Path.Combine(subdirectory.FullName, "new.img");
        File.WriteAllText(added, "new");

        root.RefreshDescendants();

        Assert.Same(project, Assert.Single(root.Children));
        Assert.Same(images, Assert.Single(project.Children));
        Assert.Same(existing, Assert.Single(images.Children, c => c.Path == retained));
        Assert.DoesNotContain(images.Children, c => c.Path == deleted);
        Assert.Contains(images.Children, c => c.Path == added);
    }

    [Fact]
    public void Refresh_UnchangedTreeDoesNotNotifyOrLoadLazyDirectories()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "lazy"));
        var path = Path.Combine(temp.Path, "file.txt");
        File.WriteAllText(path, "original");
        var root = new ExplorerNode(temp.Path, true);
        root.EnsureChildrenLoaded();
        var notifications = 0;
        root.Children.CollectionChanged += (_, _) => notifications++;
        File.WriteAllText(path, "changed");
        File.WriteAllText(Path.Combine(temp.Path, "session.uotan"), "{}");

        root.RefreshDescendants();

        Assert.Equal(0, notifications);
        var lazy = Assert.Single(root.Children, c => c.IsDirectory);
        Assert.True(Assert.Single(lazy.Children).IsPlaceholder);
    }
}