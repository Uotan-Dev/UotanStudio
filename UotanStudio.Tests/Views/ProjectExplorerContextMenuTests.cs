using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

/// <summary>
/// Targeted tests for the explorer's file-entry context menu: per-container menu instances, node resolution
/// from the right-clicked row, the open-as dispatch to the selected node, and the separator layout that must
/// never leave an empty group or two adjacent separators.
/// <para>资源管理器文件条目右键菜单的定向测试：按容器隔离的菜单实例、从被右键行解析节点、
/// 「打开方式」向选中节点的派发，以及不得留下空分组或相邻分隔符的分隔符布局。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class ProjectExplorerContextMenuTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task ContextMenu_TwoTreeItems_OwnSeparateMenuInstances()
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var model = new ProjectExplorerViewModel();
                var directory = new ExplorerNode(Path.GetTempPath(), isDirectory: true);
                var file = new ExplorerNode(Path.Combine(Path.GetTempPath(), "system.img"), isDirectory: false);
                model.TreeViewContent.Add(directory);
                model.TreeViewContent.Add(file);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var first = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(directory));
                    var second = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(file));

                    Assert.NotNull(first.ContextMenu);
                    Assert.NotNull(second.ContextMenu);
                    Assert.NotSame(first.ContextMenu, second.ContextMenu);
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task FindOwningContextMenu_ElementInsideRow_WalksUpToTheTreeItemMenu()
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var model = new ProjectExplorerViewModel();
                var node = new ExplorerNode(Path.Combine(Path.GetTempPath(), "boot.img"), isDirectory: false);
                model.TreeViewContent.Add(node);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(node));
                    var insideRow = container.GetVisualDescendants().OfType<TextBlock>().First();

                    Assert.NotNull(container.ContextMenu);
                    Assert.Same(container.ContextMenu, ProjectExplorerView.FindOwningContextMenu(insideRow));
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task OpenAsChildClicked_PreparedNode_SendsRequestForThatNode()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var filePath = Path.Combine(temp.Path, "system.img");
                File.WriteAllBytes(filePath, new byte[64]);
                var model = new ProjectExplorerViewModel();
                var node = new ExplorerNode(filePath, isDirectory: false);
                model.TreeViewContent.Add(node);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                var recipient = new object();
                List<OpenDocumentRequestMessage> received = [];
                WeakReferenceMessenger.Default.Register<OpenDocumentRequestMessage>(recipient, (_, message) => received.Add(message));
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(node));
                    var menu = Assert.IsType<ContextMenu>(container.ContextMenu);
                    var openAs = Assert.IsType<OpenAsMenuItem>(ProjectExplorerView.FindOpenAsMenuItem(menu));
                    openAs.RefreshFor(node);
                    var autoEntry = Assert.IsType<MenuItem>(openAs.Items[0]);

                    autoEntry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                    var message = Assert.Single(received);
                    Assert.Equal(filePath, message.Path);
                    Assert.Equal(EditorOpenMode.Auto, message.Mode);
                }
                finally
                {
                    WeakReferenceMessenger.Default.Unregister<OpenDocumentRequestMessage>(recipient);
                    window.Close();
                    model.Dispose();
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task ContextMenu_DirectoryNode_ShowsNoAdjacentSeparators()
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var model = new ProjectExplorerViewModel();
                var node = new ExplorerNode(Path.GetTempPath(), isDirectory: true);
                model.TreeViewContent.Add(node);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(node));
                    var menu = Assert.IsType<ContextMenu>(container.ContextMenu);
                    menu.DataContext = node;
                    Dispatcher.UIThread.RunJobs();

                    var visible = menu.Items.OfType<Control>().Where(item => item.IsVisible).ToArray();
                    var headings = string.Join(" | ", visible.Select(Describe));

                    Assert.NotEmpty(visible);
                    Assert.False(visible[0] is Separator, headings);
                    Assert.False(visible[^1] is Separator, headings);
                    Assert.DoesNotContain(
                        Enumerable.Range(1, visible.Length - 1),
                        index => visible[index] is Separator && visible[index - 1] is Separator);
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task ContextMenu_FileNode_KeepsSeparatorBetweenActionGroups()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previous = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage("en-US");
                var filePath = Path.Combine(temp.Path, "system.img");
                File.WriteAllBytes(filePath, new byte[64]);
                var model = new ProjectExplorerViewModel();
                var node = new ExplorerNode(filePath, isDirectory: false);
                model.TreeViewContent.Add(node);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(node));
                    var menu = Assert.IsType<ContextMenu>(container.ContextMenu);
                    menu.DataContext = node;
                    menu.Open(container);
                    Dispatcher.UIThread.RunJobs();

                    var visible = menu.Items.OfType<Control>().Where(item => item.IsVisible).ToArray();
                    var headings = string.Join(" | ", visible.Select(Describe));
                    var boundary = Array.FindIndex(visible, item => item is Separator);

                    Assert.True(boundary > 0, headings);
                    Assert.True(boundary < visible.Length - 1, headings);
                    Assert.Equal(LanguageService.Instance.GetString("Explorer_Unpack"), Describe(visible[boundary + 1]));
                    Assert.False(visible[^1] is Separator, headings);
                    menu.Close();
                    Assert.DoesNotContain(
                        Enumerable.Range(1, visible.Length - 1),
                        index => visible[index] is Separator && visible[index - 1] is Separator);
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                    LanguageService.Instance.ApplyLanguage(previous);
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task OpenAsEntry_FileAndDirectoryNodes_SharesMenuControlThemeAndFollowsNodeKind()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previous = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage("en-US");
                var filePath = Path.Combine(temp.Path, "system.img");
                File.WriteAllBytes(filePath, new byte[64]);
                var model = new ProjectExplorerViewModel();
                var directory = new ExplorerNode(temp.Path, isDirectory: true);
                var file = new ExplorerNode(filePath, isDirectory: false);
                model.TreeViewContent.Add(directory);
                model.TreeViewContent.Add(file);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var fileContainer = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(file));
                    var directoryContainer = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(directory));
                    var fileMenu = Assert.IsType<ContextMenu>(fileContainer.ContextMenu);
                    var directoryMenu = Assert.IsType<ContextMenu>(directoryContainer.ContextMenu);
                    fileMenu.DataContext = file;
                    directoryMenu.DataContext = directory;
                    var fileEntry = Assert.IsType<OpenAsMenuItem>(ProjectExplorerView.FindOpenAsMenuItem(fileMenu));
                    var directoryEntry = Assert.IsType<OpenAsMenuItem>(ProjectExplorerView.FindOpenAsMenuItem(directoryMenu));

                    fileMenu.Open(fileContainer);
                    Dispatcher.UIThread.RunJobs();
                    fileEntry.ApplyTemplate();
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(typeof(MenuItem), fileEntry.StyleKey);
                    Assert.NotNull(fileEntry.Template);
                    Assert.NotEmpty(fileEntry.GetVisualChildren());
                    Assert.True(fileEntry.IsVisible);
                    Assert.False(directoryEntry.IsVisible);
                    Assert.Equal(LanguageService.Instance.GetString("Explorer_OpenAsMenu"), fileEntry.Header?.ToString());
                    Assert.Equal(DocumentOpenRegistry.MenuEntries.Count + 1, fileEntry.Items.Count);
                    fileMenu.Close();
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                    LanguageService.Instance.ApplyLanguage(previous);
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    private static string Describe(Control item) => item switch
    {
        Separator => "<separator>",
        MenuItem menu => menu.Header?.ToString() ?? "<no header>",
        _ => item.GetType().Name
    };
}