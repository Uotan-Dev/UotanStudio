namespace UotanStudio.Messages;

using UotanStudio.ViewModels.Editors;

/// <summary>
/// Message announcing that the selected node in an ARSC resource tree changed.
/// <para>宣告 ARSC 资源树中的选中节点发生变化的消息。</para>
/// </summary>
/// <param name="Editor">
/// The ARSC document that owns the resource tree, or <see langword="null"/> when none is attached.
/// <para>拥有该资源树的 ARSC 文档，未附带时为空。</para>
/// </param>
/// <param name="Node">
/// The resource node now selected, or <see langword="null"/> when the selection was cleared.
/// <para>当前选中的资源节点，选中被清空时为空。</para>
/// </param>
public record ArscNodeSelectionChangedMessage(ArscEditorViewModel? Editor, ArscTreeNodeViewModel? Node);