using System.Collections.ObjectModel;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// One node project's canvas document: its node and connection collections plus their own undo/redo
/// stacks. Zoom and minimap are editor-wide state and do not follow document switches.
/// <para>一个节点项目的画布文档：节点集合、连接集合与它们各自的撤销/重做栈。缩放与小地图属于编辑器
/// 整体状态，不随文档切换。</para>
/// </summary>
public sealed class NodeProjectDocument
{
    /// <summary>Creates a node project canvas document.<para>创建一个节点项目画布文档。</para></summary>
    /// <param name="name">The node project name; an unregistered draft canvas uses an empty name.
    /// <para>节点项目名称；未登记到解决方案的草稿画布使用空名称。</para></param>
    /// <param name="nodes">Optional existing node collection, used to adopt the editor's live canvas as the document content.
    /// <para>可选的既有节点集合，用于把编辑器的当前画布接管为文档内容。</para></param>
    /// <param name="connections">Optional existing connection collection.
    /// <para>可选的既有连接集合。</para></param>
    public NodeProjectDocument(string name,
        ObservableCollection<FlowNodeViewModel>? nodes = null,
        ObservableCollection<NodeConnectionViewModel>? connections = null)
    {
        Name = name;
        Nodes = nodes ?? [];
        Connections = connections ?? [];
    }

    /// <summary>
    /// The node project name, paired with the solution's node project registration.
    /// <para>节点项目名称，用于与解决方案中的节点项目登记项配对。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The node collection owned by this document.
    /// <para>文档持有的节点集合。</para>
    /// </summary>
    public ObservableCollection<FlowNodeViewModel> Nodes { get; } = [];

    /// <summary>
    /// The connection collection owned by this document.
    /// <para>文档持有的连接集合。</para>
    /// </summary>
    public ObservableCollection<NodeConnectionViewModel> Connections { get; } = [];

    /// <summary>
    /// Whether this document carries canvas edits that have not been persisted yet.
    /// <para>文档是否包含尚未持久化的画布编辑。</para>
    /// </summary>
    /// <value><see langword="true"/> while the canvas differs from the last save.
    /// <para>画布与最近一次保存不一致时为 <see langword="true"/>。</para></value>
    public bool IsDirty { get; internal set; }

    /// <summary>
    /// This document's undo stack.
    /// <para>文档的撤销栈。</para>
    /// </summary>
    public Stack<IEditorCommand> UndoStack { get; } = [];

    /// <summary>
    /// This document's redo stack.
    /// <para>文档的重做栈。</para>
    /// </summary>
    public Stack<IEditorCommand> RedoStack { get; } = [];
}