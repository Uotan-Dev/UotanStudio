using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>项目资源管理器视图，处理目录展开加载、文件双击打开与右键打开方式。</para>
/// Project explorer view, handling directory expansion loading, double-click opening and the
/// right-click open-with menu for file nodes.
/// </summary>
public partial class ProjectExplorerView : UserControl
{
    /// <summary>
    /// <para>创建项目资源管理器视图，订阅树节点的展开事件以触发目录惰性加载。</para>
    /// Creates the project explorer view, subscribing to tree-item expansion events to trigger lazy directory loading.
    /// </summary>
    public ProjectExplorerView()
    {
        InitializeComponent();
        ProjectTree.AddHandler(TreeViewItem.ExpandedEvent, OnItemExpanded);
        ProjectTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Control { DataContext: ExplorerNode node })
        {
            node.EnsureChildrenLoaded();
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: ExplorerNode node } && !node.IsDirectory && !node.IsPlaceholder)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path));
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ProjectTree).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed &&
            e.Source is Control { DataContext: ExplorerNode node })
        {
            ProjectTree.SelectedItem = node;
        }
    }

    private void OnOpenAsText(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Text);
    }

    private void OnStartCompare(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ExplorerNode { IsPlaceholder: false } node })
        {
            ProjectTree.SelectedItem = node;
            WeakReferenceMessenger.Default.Send(new OpenCompareRequestMessage { Path = node.Path });
        }
    }

    private void OnOpenAsHex(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Hex);
    }

    private void OnOpenAsDtb(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Dtb);
    }

    private void OnOpenAsDex(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Dex);
    }

    private void OnOpenAsAxml(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Axml);
    }

    private void OnOpenAsArsc(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Arsc);
    }

    private void OnOpenAsApk(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Apk);
    }

    private void OnOpenAsFirmware(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Firmware);
    }

    private void OnNewEmptySubProject(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && node.IsDirectory)
        {
            var dirName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(node.Path));
            if (!string.IsNullOrEmpty(dirName))
            {
                var fwPath = System.IO.Path.Combine(node.Path, dirName + ".uotanfw");
                _ = ProjectOperationsService.Instance.AddSubProjectAsync(fwPath);
            }
        }
    }

    private void OnImportExistingSubProject(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && node.IsDirectory)
        {
            _ = ProjectOperationsService.Instance.ImportSubProjectAsync(node.Path);
        }
    }

    private void OpenWith(EditorOpenMode mode)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && !node.IsDirectory && !node.IsPlaceholder)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path, mode));
        }
    }
}
