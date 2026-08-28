namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>Manifest 节点选中变更消息，当 Manifest 编辑器左栏元素树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中元素的详情。</para>
/// Manifest node selection changed message, sent when the selection in the Manifest editor's left
/// element tree changes, allowing subscribers such as the properties panel to refresh with the
/// selected element's details.
/// </summary>
public class ManifestNodeSelectionChangedMessage
{
    /// <summary>
    /// <para>获取所属的 Manifest 编辑器视图模型；<c>null</c> 表示编辑器已卸载。</para>
    /// Gets the owning Manifest editor view model; <c>null</c> when the editor has been unloaded.
    /// </summary>
    public ManifestEditorViewModel? Editor { get; }

    /// <summary>
    /// <para>获取当前选中的元素树项；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected element tree item; <c>null</c> means the selection was cleared.
    /// </summary>
    public ManifestTreeNodeViewModel? Node { get; }

    /// <summary>
    /// <para>创建 Manifest 节点选中变更消息。</para>
    /// Creates a Manifest node selection changed message.
    /// </summary>
    /// <param name="editor">
    /// <para>所属的 Manifest 编辑器视图模型；可为 <c>null</c>。</para>
    /// The owning Manifest editor view model; may be <c>null</c>.
    /// </param>
    /// <param name="node">
    /// <para>当前选中的元素树项；<c>null</c> 表示清空选择。</para>
    /// The currently selected element tree item; <c>null</c> clears the selection.
    /// </param>
    public ManifestNodeSelectionChangedMessage(ManifestEditorViewModel? editor, ManifestTreeNodeViewModel? node)
    {
        Editor = editor;
        Node = node;
    }
}