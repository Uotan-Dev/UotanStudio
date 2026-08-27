namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels;

/// <summary>
/// <para>DEX 结构树节点选中变更消息，当 DEX 编辑器左栏结构树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中节点（类/方法/字段）的详情。</para>
/// DEX structure-tree node selection changed message, sent when the selection in the DEX editor's
/// left structure tree changes, allowing subscribers such as the properties panel to refresh with the
/// selected node's (class/method/field) details.
/// </summary>
public class DexNodeSelectionChangedMessage
{
    /// <summary>
    /// <para>获取所属的 DEX 编辑器视图模型；<c>null</c> 表示编辑器已卸载。</para>
    /// Gets the owning DEX editor view model; <c>null</c> when the editor has been unloaded.
    /// </summary>
    public DexEditorViewModel? Editor { get; }

    /// <summary>
    /// <para>获取当前选中的结构树项；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected tree item; <c>null</c> means the selection was cleared.
    /// </summary>
    public DexTreeNodeViewModel? Node { get; }

    /// <summary>
    /// <para>创建 DEX 节点选中变更消息。</para>
    /// Creates a DEX node selection changed message.
    /// </summary>
    /// <param name="editor">
    /// <para>所属的 DEX 编辑器视图模型；可为 <c>null</c>。</para>
    /// The owning DEX editor view model; may be <c>null</c>.
    /// </param>
    /// <param name="node">
    /// <para>当前选中的结构树项；<c>null</c> 表示清空选择。</para>
    /// The currently selected tree item; <c>null</c> clears the selection.
    /// </param>
    public DexNodeSelectionChangedMessage(DexEditorViewModel? editor, DexTreeNodeViewModel? node)
    {
        Editor = editor;
        Node = node;
    }
}