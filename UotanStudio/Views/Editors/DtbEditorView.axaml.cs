using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>DTB 设备树双栏编辑器视图，承载左栏节点树与右栏可编辑 DTS 源码，并实现双向联动。</para>
/// DTB device-tree dual-pane editor view, hosting the left node tree and the right editable DTS source,
/// implementing bi-directional linkage between them.
/// </summary>
public partial class DtbEditorView : UserControl
{
    /// <summary>
    /// <para>保存按钮的本地化标题。</para>
    /// The localized title of the save button.
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Dtb_Save");

    /// <summary>
    /// <para>创建 DTB 编辑器视图并初始化组件。</para>
    /// Creates the DTB editor view and initializes its components.
    /// </summary>
    public DtbEditorView()
    {
        InitializeComponent();
        SourceEditor.TextChanged += (_, _) => OnSourceEdited();
        // 光标位置联动：右栏光标变化时反查并高亮左栏所属节点（XAML 无法直接绑定该事件）。
        // Caret-position linkage: highlight the owning node in the left tree on caret movement.
        if (SourceEditor.TextArea?.Caret is { } caret)
        {
            caret.PositionChanged += OnSourceCaretChanged;
        }
    }

    /// <summary>
    /// <para>左栏选中节点 → 右栏滚动并高亮对应源码段，并向属性面板广播节点选中变更。</para>
    /// Left-pane node selection scrolls and highlights the corresponding source region in the right
    /// pane, and broadcasts the node-selection change to the properties panel.
    /// </summary>
    private void OnNodeTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DtbEditorViewModel vm)
        {
            return;
        }

        // 广播节点选中变更，供属性面板展示选中节点详情。
        // Broadcast the node selection so the properties panel can show the selected node's details.
        WeakReferenceMessenger.Default.Send(new DtbNodeSelectionChangedMessage(vm, NodeTree.SelectedItem as DtbNodeViewModel));

        if (NodeTree.SelectedItem is not DtbNodeViewModel node)
        {
            return;
        }

        var line = vm.GetNodeSourceLine(node.FullPath);
        if (line > 0)
        {
            SourceEditor.ScrollToLine(line);
            // 把光标定位到节点起始内容行，并使代码编辑器获得键盘焦点，便于立即在节点处输入。
            // Position the caret at the node's first content line and give the editor keyboard focus
            // so input can begin immediately at the node location.
            if (SourceEditor.TextArea?.Caret is { } caret)
            {
                caret.Line = line;
                caret.Column = 1;
            }

            Dispatcher.UIThread.Post(() => SourceEditor.TextArea?.Focus());
        }
    }

    /// <summary>
    /// <para>右栏光标/选区变化 → 左栏高亮所属节点。</para>
    /// Right-pane caret/selection change highlights the owning node in the left tree.
    /// </summary>
    private void OnSourceCaretChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not DtbEditorViewModel vm)
        {
            return;
        }

        var line = SourceEditor.TextArea?.Caret.Line ?? 1;
        var path = vm.FindNodePathForLine(line);
        if (path is null)
        {
            return;
        }

        // 仅当左栏当前选中与所属节点不一致时才切换，避免循环联动。
        // Only switch when the left selection does not already match, to avoid linkage feedback loops.
        if (NodeTree.SelectedItem is not DtbNodeViewModel cur || cur.FullPath != path)
        {
            Dispatcher.UIThread.Post(() => vm.TrySelectNode(path));
        }
    }

    private void OnSourceEdited()
    {
        if (DataContext is DtbEditorViewModel vm && !vm.IsDirty)
        {
            vm.IsDirty = true;
        }
    }
}