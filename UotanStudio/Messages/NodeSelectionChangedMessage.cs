using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Messages;

/// <summary>
/// <para>节点选中变更消息，当节点编辑器画布上的选中节点发生变化时发送，
/// 供属性面板等订阅方刷新显示。</para>
/// Node selection changed message, sent when the selected node on the node editor canvas changes,
/// allowing subscribers such as the properties panel to refresh their display.
/// </summary>
public class NodeSelectionChangedMessage
{
    /// <summary>
    /// <para>获取当前选中的节点；<c>null</c> 表示已清空选择。</para>
    /// Gets the currently selected node; <c>null</c> means the selection was cleared.
    /// </summary>
    public FlowNodeViewModel? Node { get; }

    /// <summary>
    /// <para>创建节点选中变更消息。</para>
    /// Creates a node selection changed message.
    /// </summary>
    /// <param name="node">
    /// <para>当前选中的节点；<c>null</c> 表示清空选择。</para>
    /// The currently selected node; <c>null</c> clears the selection.
    /// </param>
    public NodeSelectionChangedMessage(FlowNodeViewModel? node)
    {
        Node = node;
    }
}