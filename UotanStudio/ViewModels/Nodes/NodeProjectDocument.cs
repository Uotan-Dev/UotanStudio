using System.Collections.ObjectModel;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>一个节点项目的画布文档：节点集合、连接集合与它们各自的撤销/重做栈。缩放与小地图属于编辑器
/// 整体状态，不随文档切换。</para>
/// One node project's canvas document: its node and connection collections plus their own undo/redo
/// stacks. Zoom and minimap are editor-wide state and do not follow document switches.
/// </summary>
public sealed class NodeProjectDocument
{
    /// <summary>
    /// <para>创建一个节点项目画布文档。</para>
    /// Creates a node project canvas document.
    /// </summary>
    /// <param name="name">
    /// <para>节点项目名称；未登记到解决方案的草稿画布使用空名称。</para>
    /// The node project name; an unregistered draft canvas uses an empty name.
    /// </param>
    /// <param name="nodes">
    /// <para>可选的既有节点集合，用于把编辑器的当前画布接管为文档内容。</para>
    /// Optional existing node collection, used to adopt the editor's live canvas as the document content.
    /// </param>
    /// <param name="connections">
    /// <para>可选的既有连接集合。</para>
    /// Optional existing connection collection.
    /// </param>
    public NodeProjectDocument(string name,
        ObservableCollection<FlowNodeViewModel>? nodes = null,
        ObservableCollection<NodeConnectionViewModel>? connections = null)
    {
        Name = name;
        Nodes = nodes ?? [];
        Connections = connections ?? [];
    }

    /// <summary>
    /// <para>节点项目名称，用于与解决方案中的节点项目登记项配对。</para>
    /// The node project name, paired with the solution's node project registration.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>文档持有的节点集合。</para>
    /// The node collection owned by this document.
    /// </summary>
    public ObservableCollection<FlowNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// <para>文档持有的连接集合。</para>
    /// The connection collection owned by this document.
    /// </summary>
    public ObservableCollection<NodeConnectionViewModel> Connections { get; } = [];

    /// <summary>
    /// <para>文档的撤销栈。</para>
    /// This document's undo stack.
    /// </summary>
    public Stack<IEditorCommand> UndoStack { get; } = [];

    /// <summary>
    /// <para>文档的重做栈。</para>
    /// This document's redo stack.
    /// </summary>
    public Stack<IEditorCommand> RedoStack { get; } = [];
}