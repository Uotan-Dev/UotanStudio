using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView : UserControl
{
    public NodeEditorView()
    {
        InitializeComponent();
        // 订阅画布选中变更：NodifyEditor 内部维护 SelectedItems，需在此同步到 ViewModel
        // Subscribe to canvas selection changes: NodifyEditor keeps its own SelectedItems, so sync to the ViewModel here.
        Editor.SelectionChanged += OnEditorSelectionChanged;
    }

    /// <summary>
    /// <para>画布节点选中变更时，将新增选中中的第一个节点同步到 ViewModel，驱动右侧属性面板。</para>
    /// When the canvas node selection changes, sync the first added node to the ViewModel to drive the properties panel.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>Event source.</param>
    /// <param name="e"><para>选中变更事件参数。</para>The selection changed args.</param>
    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            foreach (var item in e.AddedItems)
            {
                if (item is UotanStudio.ViewModels.NodeViewModels.FlowNodeViewModel node)
                {
                    vm.SelectNode(node);
                    break;
                }
            }
        }
    }

    private void OnToolboxItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is NodeToolboxItem item)
        {
            if (DataContext is NodeEditorViewModel vm)
            {
                var node = item.CreateNode();
                vm.AddNode(node);
            }
        }
    }

    /// <summary>
    /// <para>路径端口「浏览」按钮：通过存储提供程序弹出文件选择器，把所选路径写入该端口的初始值。</para>
    /// Path-port browse button: opens a storage-provider file picker and writes the chosen path into the port's initial value.
    /// </summary>
    /// <param name="sender">The browse button whose Tag carries the target connector.</param>
    /// <param name="e">Routed event args.</param>
    private async void OnBrowsePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeConnectorViewModel connector } || VisualRoot is not Window window)
        {
            return;
        }

        var storage = window.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select file",
            AllowMultiple = false
        });
        if (files is { Count: > 0 } && files[0].TryGetLocalPath() is { Length: > 0 } localPath)
        {
            connector.InitialValue = localPath;
        }
    }
}