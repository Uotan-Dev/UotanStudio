namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>设备树节点选中变更消息，当 DTB 双栏编辑器左栏节点树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中节点的详情。</para>
/// DTB node selection changed message, sent when the selection in the DTB dual-pane editor's left
/// node tree changes, allowing subscribers such as the properties panel to refresh with the selected
/// node's details.
/// </summary>
public class DtbNodeSelectionChangedMessage
{
    /// <summary>
    /// <para>获取所属的 DTB 编辑器视图模型；<c>null</c> 表示编辑器已卸载。</para>
    /// Gets the owning DTB editor view model; <c>null</c> when the editor has been unloaded.
    /// </summary>
    public DtbEditorViewModel? Editor { get; }

    /// <summary>
    /// <para>获取当前选中的节点树项；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected node tree item; <c>null</c> means the selection was cleared.
    /// </summary>
    public DtbNodeViewModel? Node { get; }

    /// <summary>
    /// <para>创建设备树节点选中变更消息。</para>
    /// Creates a DTB node selection changed message.
    /// </summary>
    /// <param name="editor">
    /// <para>所属的 DTB 编辑器视图模型；可为 <c>null</c>。</para>
    /// The owning DTB editor view model; may be <c>null</c>.
    /// </param>
    /// <param name="node">
    /// <para>当前选中的节点树项；<c>null</c> 表示清空选择。</para>
    /// The currently selected node tree item; <c>null</c> clears the selection.
    /// </param>
    public DtbNodeSelectionChangedMessage(DtbEditorViewModel? editor, DtbNodeViewModel? node)
    {
        Editor = editor;
        Node = node;
    }
}