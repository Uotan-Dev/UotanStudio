using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Messages;

/// <summary>
/// Message announcing that the node selected on a node canvas changed.
/// <para>宣告节点画布上选中的节点发生变化的消息。</para>
/// </summary>
/// <param name="Node">
/// The node now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的节点，选中被清空时为空。</para>
/// </param>
public record NodeSelectionChangedMessage(FlowNodeViewModel? Node);