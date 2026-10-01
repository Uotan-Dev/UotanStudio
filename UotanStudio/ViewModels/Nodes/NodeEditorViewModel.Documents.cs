using System.Collections.ObjectModel;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>节点项目文档集合：编辑器同时持有多个已登记的画布文档，切换文档即替换画布集合与撤销/重做栈。</para>
/// Node project document set: the editor holds several registered canvas documents at once, and switching a
/// document swaps the canvas collections and the undo/redo stacks.
/// </summary>
public partial class NodeEditorViewModel
{
    private readonly NodeProjectDocument _draftDocument;
    private NodeProjectDocument _activeDocument;

    /// <summary>
    /// <para>当前打开的节点项目文档，按打开顺序排列，供画布标签条绑定。草稿画布不在其中。</para>
    /// The open node project documents in open order, bound by the canvas tab strip. The draft canvas is not
    /// included.
    /// </summary>
    public ObservableCollection<NodeProjectDocument> OpenDocuments { get; } = [];

    /// <summary>
    /// <para>编辑器当前呈现的画布文档；未打开节点项目时为草稿画布。</para>
    /// The canvas document the editor currently presents; the draft canvas when no node project is open.
    /// </summary>
    public NodeProjectDocument ActiveDocument => _activeDocument;

    /// <summary>
    /// <para>活动节点项目名称；草稿画布返回空名称。</para>
    /// The active node project name; the draft canvas reports an empty name.
    /// </summary>
    public string ActiveDocumentName => _activeDocument.Name;

    /// <summary>
    /// <para>是否存在已登记的节点项目文档，决定画布标签条是否可见。</para>
    /// Whether any registered node project document is open, which shows the canvas tab strip.
    /// </summary>
    public bool HasOpenNodeProjects => OpenDocuments.Count > 0;

    /// <summary>
    /// <para>打开或替换同名文档，并将其呈现为活动画布。</para>
    /// Opens a document, replacing a same-named one, and presents it as the active canvas.
    /// </summary>
    /// <param name="document">要打开的文档。<para>The document to open.</para></param>
    public void OpenDocument(NodeProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (FindDocument(document.Name) is { } existing) OpenDocuments.Remove(existing);
        OpenDocuments.Add(document);
        OnPropertyChanged(nameof(HasOpenNodeProjects));
        Present(document);
    }

    /// <summary>
    /// <para>把已打开的同名文档呈现为活动画布。</para>
    /// Presents an already-open same-named document as the active canvas.
    /// </summary>
    /// <param name="name">节点项目名称。<para>The node project name.</para></param>
    /// <returns>找到并切换时返回 <see langword="true"/>。<para><see langword="true"/> when found and switched.</para></returns>
    public bool ActivateDocument(string name)
    {
        if (FindDocument(name) is not { } document) return false;
        Present(document);
        return true;
    }

    /// <summary>
    /// <para>关闭一个节点项目文档：编辑器丢弃其画布内容，解决方案仍保留登记与磁盘文件。</para>
    /// Closes a node project document: the editor discards its canvas content while the solution keeps the
    /// registration and the file on disk.
    /// </summary>
    /// <param name="name">节点项目名称。<para>The node project name.</para></param>
    /// <returns>确实关闭了文档时返回 <see langword="true"/>。
    /// <para><see langword="true"/> when a document was closed.</para></returns>
    public bool CloseDocument(string name)
    {
        if (FindDocument(name) is not { } document) return false;
        OpenDocuments.Remove(document);
        OnPropertyChanged(nameof(HasOpenNodeProjects));
        if (ReferenceEquals(_activeDocument, document))
            Present(OpenDocuments.FirstOrDefault() ?? _draftDocument);
        return true;
    }

    /// <summary>
    /// <para>关闭全部节点项目文档并把画布重置为空白草稿。</para>
    /// Closes every node project document and resets the canvas to a blank draft.
    /// </summary>
    public void CloseAllDocuments()
    {
        OpenDocuments.Clear();
        OnPropertyChanged(nameof(HasOpenNodeProjects));
        _draftDocument.Nodes.Clear();
        _draftDocument.Connections.Clear();
        _draftDocument.UndoStack.Clear();
        _draftDocument.RedoStack.Clear();
        Present(_draftDocument);
    }

    /// <summary>
    /// <para>按名称取得已打开的文档。</para>
    /// Gets an open document by name.
    /// </summary>
    /// <param name="name">节点项目名称。<para>The node project name.</para></param>
    /// <returns>找到的文档；未打开或空名称时为 <see langword="null"/>。
    /// <para>The document, or <see langword="null"/> when it is not open or the name is empty.</para></returns>
    public NodeProjectDocument? FindDocument(string name)
        => name.Length == 0 ? null
            : OpenDocuments.FirstOrDefault(owned => string.Equals(owned.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Present(NodeProjectDocument document)
    {
        _activeDocument = document;
        Nodes = document.Nodes;
        Connections = document.Connections;
        _undoStack = document.UndoStack;
        _redoStack = document.RedoStack;
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveDocumentName));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }
}