namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// Message announcing that the selected element of a Manifest tree changed.
/// <para>宣告 Manifest 树中选中的元素发生变化的消息。</para>
/// </summary>
/// <param name="Editor">
/// The Manifest document that owns the element tree, or <see langword="null"/> when none is attached.
/// <para>拥有该元素树的 Manifest 文档，未附带时为空。</para>
/// </param>
/// <param name="Node">
/// The element node now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的元素节点，选中被清空时为空。</para>
/// </param>
public record ManifestNodeSelectionChangedMessage(ManifestEditorViewModel? Editor, ManifestTreeNodeViewModel? Node);