using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services.Ui.Projects;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// Node project document set: the editor holds several registered canvas documents at once, and switching a
/// document swaps the canvas collections and the undo/redo stacks.
/// <para>节点项目文档集合：编辑器同时持有多个已登记的画布文档，切换文档即替换画布集合与撤销/重做栈。</para>
/// </summary>
public partial class NodeEditorViewModel
{
    private readonly NodeProjectDocument _draftDocument;
    private NodeProjectDocument _activeDocument;

    /// <summary>
    /// The open node project documents in open order, bound by the canvas tab strip. The draft canvas is not
    /// included.
    /// <para>当前打开的节点项目文档，按打开顺序排列，供画布标签条绑定。草稿画布不在其中。</para>
    /// </summary>
    public ObservableCollection<NodeProjectDocument> OpenDocuments { get; } = [];

    /// <summary>
    /// The canvas document the editor currently presents; the draft canvas when no node project is open.
    /// <para>编辑器当前呈现的画布文档；未打开节点项目时为草稿画布。</para>
    /// </summary>
    public NodeProjectDocument ActiveDocument => _activeDocument;

    /// <summary>
    /// The active node project name; the draft canvas reports an empty name.
    /// <para>活动节点项目名称；草稿画布返回空名称。</para>
    /// </summary>
    public string ActiveDocumentName => _activeDocument.Name;

    /// <summary>
    /// Whether any registered node project document is open, which shows the canvas tab strip.
    /// <para>是否存在已登记的节点项目文档，决定画布标签条是否可见。</para>
    /// </summary>
    public bool HasOpenNodeProjects => OpenDocuments.Count > 0;

    /// <summary>
    /// Whether the active document carries canvas edits that have not been persisted yet.
    /// <para>活动文档是否包含尚未持久化的画布编辑。</para>
    /// </summary>
    /// <value><see langword="true"/> while the active canvas differs from the last save.
    /// <para>活动画布与最近一次保存不一致时为 <see langword="true"/>。</para></value>
    public bool IsDirty => _activeDocument.IsDirty;

    /// <summary>
    /// Whether any open document, or the draft canvas when it is active, carries unsaved edits.
    /// <para>是否有任一打开的文档（或处于活动状态的草稿画布）包含未保存编辑。</para>
    /// </summary>
    /// <value><see langword="true"/> when at least one document is dirty.
    /// <para>至少一个文档为脏时为 <see langword="true"/>。</para></value>
    public bool HasUnsavedChanges => IsDirty || OpenDocuments.Any(document => document.IsDirty);

    /// <summary>
    /// Marks the active document dirty after a node, connection, parameter or history edit.
    /// <para>在节点、连接、参数或历史记录发生编辑后把活动文档标记为脏。</para>
    /// </summary>
    public void MarkDirty()
    {
        if (_activeDocument.IsDirty)
        {
            return;
        }
        _activeDocument.IsDirty = true;
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Clears the dirty flag of the active document after its content was persisted.
    /// <para>活动文档内容持久化后清除其脏标记。</para>
    /// </summary>
    public void MarkSaved()
    {
        if (!_activeDocument.IsDirty)
        {
            return;
        }
        _activeDocument.IsDirty = false;
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Clears the dirty flag of every open node project after the whole solution was persisted. The draft canvas is
    /// not a registered workflow and is never written by a solution save, so its flag stays set.
    /// <para>整个解决方案持久化后清除全部已打开节点项目的脏标记。草稿画布不是已登记的工作流，解决方案保存不会写入它，
    /// 因此其脏标记保持不变。</para>
    /// </summary>
    public void MarkAllSaved()
    {
        foreach (var document in OpenDocuments)
        {
            document.IsDirty = false;
        }
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Persists the current solution through the project service and then clears every document dirty flag.
    /// <para>经项目服务保存当前解决方案，然后清除所有文档的脏标记。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await ProjectOperationsService.Instance.SaveAsync())
        {
            return;
        }
        MarkAllSaved();
    }

    private void NotifyDirtyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>
    /// Opens a document, replacing a same-named one, and presents it as the active canvas.
    /// <para>打开或替换同名文档，并将其呈现为活动画布。</para>
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
    /// Presents an already-open same-named document as the active canvas.
    /// <para>把已打开的同名文档呈现为活动画布。</para>
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
    /// Closes a node project document: the editor discards its canvas content while the solution keeps the
    /// registration and the file on disk.
    /// <para>关闭一个节点项目文档：编辑器丢弃其画布内容，解决方案仍保留登记与磁盘文件。</para>
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
        else
            OnPropertyChanged(nameof(HasUnsavedChanges));
        return true;
    }

    /// <summary>
    /// Closes every node project document and resets the canvas to a blank draft.
    /// <para>关闭全部节点项目文档并把画布重置为空白草稿。</para>
    /// </summary>
    public void CloseAllDocuments()
    {
        OpenDocuments.Clear();
        OnPropertyChanged(nameof(HasOpenNodeProjects));
        _suppressDirtyTracking = true;
        try
        {
            _draftDocument.Nodes.Clear();
            _draftDocument.Connections.Clear();
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
        _draftDocument.UndoStack.Clear();
        _draftDocument.RedoStack.Clear();
        _draftDocument.IsDirty = false;
        Present(_draftDocument);
    }

    /// <summary>
    /// Gets an open document by name.
    /// <para>按名称取得已打开的文档。</para>
    /// </summary>
    /// <param name="name">节点项目名称。<para>The node project name.</para></param>
    /// <returns>找到的文档；未打开或空名称时为 <see langword="null"/>。
    /// <para>The document, or <see langword="null"/> when it is not open or the name is empty.</para></returns>
    public NodeProjectDocument? FindDocument(string name)
        => name.Length == 0 ? null
            : OpenDocuments.FirstOrDefault(owned => string.Equals(owned.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Present(NodeProjectDocument document)
    {
        _suppressDirtyTracking = true;
        try
        {
            _activeDocument = document;
            Nodes = document.Nodes;
            Connections = document.Connections;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
        _undoStack = document.UndoStack;
        _redoStack = document.RedoStack;
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveDocumentName));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        NotifyDirtyChanged();
    }
}