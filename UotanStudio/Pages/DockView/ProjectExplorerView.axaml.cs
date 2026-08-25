using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages.DockView;

/// <summary>
/// <para>项目资源管理器视图，处理目录展开加载与文件节点的双击打开。</para>
/// Project explorer view, handling directory expansion loading and double-click opening of file nodes.
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
}