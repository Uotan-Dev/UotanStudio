using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Messages;

/// <summary>
/// <para>节点选中变更消息，当节点编辑器画布上的选中节点发生变化时发送，
/// 供属性面板等订阅方刷新显示。</para>
/// Node selection changed message, sent when the selected node on the node editor canvas changes,
/// allowing subscribers such as the properties panel to refresh their display.
/// </summary>
public record NodeSelectionChangedMessage(FlowNodeViewModel? Node);