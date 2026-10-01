namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>设备树节点选中变更消息，当 DTB 双栏编辑器左栏节点树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中节点的详情。</para>
/// DTB node selection changed message, sent when the selection in the DTB dual-pane editor's left
/// node tree changes, allowing subscribers such as the properties panel to refresh with the selected
/// node's details.
/// </summary>
public record DtbNodeSelectionChangedMessage(DtbEditorViewModel? Editor, DtbNodeViewModel? Node);