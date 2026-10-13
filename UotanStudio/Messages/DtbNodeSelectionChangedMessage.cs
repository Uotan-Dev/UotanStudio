namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// Message announcing that the selected node of a device tree document changed.
/// <para>宣告设备树文档中的选中节点发生变化的消息。</para>
/// </summary>
/// <param name="Editor">
/// The device tree document that owns the node tree, or <see langword="null"/> when none is attached.
/// <para>拥有该节点树的设备树文档，未附带时为空。</para>
/// </param>
/// <param name="Node">
/// The node now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的节点，选中被清空时为空。</para>
/// </param>
public record DtbNodeSelectionChangedMessage(DtbEditorViewModel? Editor, DtbNodeViewModel? Node);