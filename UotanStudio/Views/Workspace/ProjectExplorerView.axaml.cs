using UotanStudio.ViewModels.Workspace;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Views.Controls;
using UotanStudio.ViewModels;

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

    private void OnOpenAsText(object? sender, RoutedEventArgs e)
    {
        OpenWith(EditorOpenMode.Text);
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

    private void OpenWith(EditorOpenMode mode)
    {
        if (ProjectTree.SelectedItem is ExplorerNode node && !node.IsDirectory && !node.IsPlaceholder)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(node.Path, mode));
        }
    }
}