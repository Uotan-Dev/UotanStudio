namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>ARSC 节点选中变更消息，当 ARSC 编辑器左栏资源树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中包/类型/条目的详情。</para>
/// ARSC node selection changed message, sent when the selection in the ARSC editor's left resource
/// tree changes, allowing subscribers such as the properties panel to refresh with the selected
/// package/type/entry details.
/// </summary>
public class ArscNodeSelectionChangedMessage
{
    /// <summary>
    /// <para>获取所属的 ARSC 编辑器视图模型；<c>null</c> 表示编辑器已卸载。</para>
    /// Gets the owning ARSC editor view model; <c>null</c> when the editor has been unloaded.
    /// </summary>
    public ArscEditorViewModel? Editor { get; }

    /// <summary>
    /// <para>获取当前选中的资源树项；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected resource tree item; <c>null</c> means the selection was cleared.
    /// </summary>
    public ArscTreeNodeViewModel? Node { get; }

    /// <summary>
    /// <para>创建 ARSC 节点选中变更消息。</para>
    /// Creates an ARSC node selection changed message.
    /// </summary>
    /// <param name="editor">
    /// <para>所属的 ARSC 编辑器视图模型；可为 <c>null</c>。</para>
    /// The owning ARSC editor view model; may be <c>null</c>.
    /// </param>
    /// <param name="node">
    /// <para>当前选中的资源树项；<c>null</c> 表示清空选择。</para>
    /// The currently selected resource tree item; <c>null</c> clears the selection.
    /// </param>
    public ArscNodeSelectionChangedMessage(ArscEditorViewModel? editor, ArscTreeNodeViewModel? node)
    {
        Editor = editor;
        Node = node;
    }
}