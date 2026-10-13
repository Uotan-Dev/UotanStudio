using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Editors;

/// <summary>
/// DTB device-tree dual-pane editor view, hosting the left node tree and the right editable DTS source,
/// implementing bi-directional linkage between them.
/// <para>DTB 设备树双栏编辑器视图，承载左栏节点树与右栏可编辑 DTS 源码，并实现双向联动。</para>
/// </summary>
public partial class DtbEditorView : UserControl
{
    private readonly EditorOwnerBinding<DtbEditorViewModel> _editorOwner =
        new(static model => model.Editor, static (model, editor) => model.Editor = editor);

    /// <summary>
    /// The localized title of the save button.
    /// <para>保存按钮的本地化标题。</para>
    /// </summary>
    public static string SaveLabel => LanguageService.Instance.GetString("Dock_Dtb_Save");

    /// <summary>
    /// Creates the DTB editor view and initializes its components.
    /// <para>创建 DTB 编辑器视图并初始化组件。</para>
    /// </summary>
    public DtbEditorView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateEditorOwner();
        Unloaded += (_, _) => UpdateEditorOwner(detach: true);
        DataContextChanged += (_, _) => UpdateEditorOwner();

        if (SourceEditor.TextArea?.Caret is { } caret)
        {
            caret.PositionChanged += OnSourceCaretChanged;
        }
    }

    /// <summary>
    /// Left-pane node selection scrolls and highlights the corresponding source region in the right
    /// pane, and broadcasts the node-selection change to the properties panel.
    /// <para>左栏选中节点 → 右栏滚动并高亮对应源码段，并向属性面板广播节点选中变更。</para>
    /// </summary>
    private void OnNodeTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DtbEditorViewModel vm)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new DtbNodeSelectionChangedMessage(vm, NodeTree.SelectedItem as DtbNodeViewModel));

        if (NodeTree.SelectedItem is not DtbNodeViewModel node)
        {
            return;
        }

        var line = vm.GetNodeSourceLine(node.FullPath);
        if (line > 0)
        {
            SourceEditor.ScrollToLine(line);

            if (SourceEditor.TextArea?.Caret is { } caret)
            {
                caret.Line = line;
                caret.Column = 1;
            }

            Dispatcher.UIThread.Post(() => SourceEditor.TextArea?.Focus());
        }
    }

    private void UpdateEditorOwner(bool detach = false)
        => _editorOwner.Update(!detach && IsLoaded ? DataContext as DtbEditorViewModel : null, SourceEditor);

    /// <summary>
    /// Right-pane caret/selection change highlights the owning node in the left tree.
    /// <para>右栏光标/选区变化 → 左栏高亮所属节点。</para>
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

        if (NodeTree.SelectedItem is not DtbNodeViewModel cur || cur.FullPath != path)
        {
            Dispatcher.UIThread.Post(() => vm.TrySelectNode(path));
        }
    }

}