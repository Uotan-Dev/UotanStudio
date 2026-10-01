namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>ARSC 节点选中变更消息，当 ARSC 编辑器左栏资源树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中包/类型/条目的详情。</para>
/// ARSC node selection changed message, sent when the selection in the ARSC editor's left resource
/// tree changes, allowing subscribers such as the properties panel to refresh with the selected
/// package/type/entry details.
/// </summary>
public record ArscNodeSelectionChangedMessage(ArscEditorViewModel? Editor, ArscTreeNodeViewModel? Node);