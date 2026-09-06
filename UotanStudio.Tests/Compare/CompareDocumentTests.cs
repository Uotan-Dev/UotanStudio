using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using UotanStudio.Tests.Infrastructure;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Compare;

[CollectionDefinition("Compare documents", DisableParallelization = true)]
public sealed class CompareDocumentCollection;

[Collection("Compare documents")]
public sealed class CompareDocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("uotan-compare-").FullName;

    [Theory]
    [InlineData("text", CompareMode.Text)]
    [InlineData("binary", CompareMode.Binary)]
    [InlineData("folder", CompareMode.Folder)]
    public async Task ExplorerSelections_LoadLeftThenRight_AndKeepLeftBaseline(string kind, CompareMode mode)
    {
        var left = CreateInput("left", kind);
        var right = CreateInput("right", kind);
        var replacement = CreateInput("replacement", kind);
        var compare = new CompareViewModel();
        try
        {
            await compare.AddComparisonPathAsync(left);
            Assert.Equal(mode, compare.Mode);
            Assert.Equal(left, compare.LeftPath);
            Assert.False(compare.HasRight);
            Assert.NotEmpty(compare.LeftRows!);
            Assert.All(compare.RightRows!, row => Assert.True(row.IsRightEmpty));

            await compare.AddComparisonPathAsync(right);
            Assert.Equal(left, compare.LeftPath);
            Assert.Equal(right, compare.RightPath);
            Assert.Equal(mode, compare.Mode);
            Assert.NotEmpty(compare.RightRows!);
            Assert.Contains(compare.Rows!, row => !row.IsRightEmpty);

            await compare.AddComparisonPathAsync(replacement);
            Assert.Equal(left, compare.LeftPath);
            Assert.Equal(replacement, compare.RightPath);
            Assert.False(compare.HasError, compare.ErrorMessage);
        }
        finally
        {
            compare.Cleanup();
        }
    }

    [Theory]
    [InlineData("text", "folder", CompareMode.Folder)]
    [InlineData("folder", "text", CompareMode.Text)]
    public async Task SwitchingBetweenFilesAndFolders_StartsFreshLeftSide(string firstKind, string nextKind, CompareMode mode)
    {
        var compare = new CompareViewModel();
        try
        {
            await compare.AddComparisonPathAsync(CreateInput("first", firstKind));
            await compare.AddComparisonPathAsync(CreateInput("second", firstKind));
            var next = CreateInput("next", nextKind);
            await compare.AddComparisonPathAsync(next);

            Assert.Equal(next, compare.LeftPath);
            Assert.False(compare.HasRight);
            Assert.Equal(mode, compare.Mode);
            Assert.False(compare.HasError, compare.ErrorMessage);
        }
        finally
        {
            compare.Cleanup();
        }
    }

    [Fact]
    public async Task SecondSelectionDuringFirstLoad_FillsRightSide()
    {
        var left = CreateInput("left", "text");
        var right = CreateInput("right", "text");
        var compare = new CompareViewModel();
        try
        {
            var firstLoad = compare.AddComparisonPathAsync(left);
            var secondLoad = compare.AddComparisonPathAsync(right);
            await Task.WhenAll(firstLoad, secondLoad);

            Assert.Equal(left, compare.LeftPath);
            Assert.Equal(right, compare.RightPath);
            Assert.Equal(CompareMode.Text, compare.Mode);
            Assert.Contains(compare.Rows!, row => row.LeftText == "left" && row.RightText == "right");
            Assert.False(compare.IsBusy);
            Assert.False(compare.HasError, compare.ErrorMessage);
        }
        finally
        {
            compare.Cleanup();
        }
    }

    [Fact]
    public async Task Workspace_ReusesCompareDocument_AndReopensEmptyAfterClose()
    {
        // EmptyContent 的 MaterialIcon 构造时同步解析几何图形，需要平台渲染接口。
        // The EmptyContent MaterialIcon parses geometry in its constructor, which needs the
        // platform render interface.
        HeadlessAvalonia.EnsureInitialized();
        var workspace = new WorkspaceViewModel();
        var navigation = new List<NavigationTarget>();
        var recipient = new object();
        WeakReferenceMessenger.Default.Register<NavigateToTabMessage>(recipient, (_, message) => navigation.Add(message.Target));
        try
        {
            var first = Assert.IsType<CompareViewModel>(workspace.OpenCompareDocument());
            var pane = Assert.IsAssignableFrom<IDocumentDock>(first.Owner);
            Assert.Same(first, pane.ActiveDockable);
            Assert.True(first.CanClose);
            await first.AddComparisonPathAsync(CreateInput("left", "binary"));
            await first.AddComparisonPathAsync(CreateInput("right", "binary"));
            await first.ToggleHexViewCommand.ExecuteAsync(null);
            Assert.IsType<CompareHexRowCollection>(first.Rows);

            var same = workspace.OpenCompareDocument();
            Assert.Same(first, same);
            Assert.Single(pane.VisibleDockables!.OfType<CompareViewModel>());
            Assert.All(navigation, target => Assert.Equal(NavigationTarget.Workspace, target));
            Assert.NotEmpty(navigation);

            var leftPath = first.LeftPath;
            var factory = Assert.IsAssignableFrom<IFactory>(first.Factory);
            factory.CloseDockable(first);
            Assert.Null(first.Owner);
            Assert.Null(first.Rows);
            Assert.False(first.HasComparison);
            using (File.Open(leftPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            var reopened = Assert.IsType<CompareViewModel>(workspace.OpenCompareDocument());
            Assert.NotSame(first, reopened);
            Assert.Same(reopened, pane.ActiveDockable);
            Assert.False(reopened.HasLeft);
            Assert.False(reopened.HasRight);
            Assert.Single(pane.VisibleDockables!.OfType<CompareViewModel>());
            factory.CloseDockable(reopened);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task CleanupDuringLoad_DoesNotRestoreClosedComparison()
    {
        var compare = new CompareViewModel();
        var load = compare.AddComparisonPathAsync(CreateInput("left", "text"));
        compare.Cleanup();
        await load;

        Assert.Null(compare.Rows);
        Assert.False(compare.HasLeft);
        Assert.False(compare.HasComparison);
        Assert.False(compare.IsBusy);
        Assert.False(compare.HasError, compare.ErrorMessage);
    }

    private string CreateInput(string name, string kind)
    {
        var path = Path.Combine(_root, name);
        if (kind == "folder")
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "item.txt"), name);
        }
        else if (kind == "binary")
        {
            path += ".bin";
            File.WriteAllBytes(path, [0, 1, 2, (byte)name.Length]);
        }
        else
        {
            path += ".txt";
            File.WriteAllText(path, name);
        }
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
