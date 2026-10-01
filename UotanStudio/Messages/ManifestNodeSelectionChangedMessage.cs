namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>Manifest 节点选中变更消息，当 Manifest 编辑器左栏元素树选中项变化时发送，
/// 供属性面板等订阅方刷新显示选中元素的详情。</para>
/// Manifest node selection changed message, sent when the selection in the Manifest editor's left
/// element tree changes, allowing subscribers such as the properties panel to refresh with the
/// selected element's details.
/// </summary>
public record ManifestNodeSelectionChangedMessage(ManifestEditorViewModel? Editor, ManifestTreeNodeViewModel? Node);