using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>节点编辑器主视图模型，管理节点画布、连接线、工具箱、选择状态与工作流执行。</para>
/// Main view model for the node editor, managing the node canvas, connections, toolbox, selection state, and workflow execution.
/// </summary>
public partial class NodeEditorViewModel : ViewModelBase, IDisposable
{
    public Guid EditorId { get; } = Guid.NewGuid();
    /// <summary>Interaction service supplied by the visible desktop host.</summary>
    public Services.Interactions.IWorkflowInteractionService Interactions { get; set; }
        = Services.Interactions.UnsupportedWorkflowInteractionService.Instance;
    /// <summary>Resource scope supplied by the composite editor; ordinary project canvases have no package scope.</summary>
    public Services.Projects.VisualPluginPackage? ResourcePackage { get; set; }

    /// <summary>Host-provided dependency check for the package currently being edited.</summary>
    public Func<IReadOnlyList<string>>? ValidatePackage { get; set; }
    private Point _nextNodeLocation = new(100, 100);
    private static readonly Point DefaultNodeLocation = new(100, 100);

    private readonly ObservableCollection<NodeViewModels.FlowNodeViewModel> _selectedNodes = [];
    private readonly ObservableCollection<NodeViewModels.BatchNodeInputViewModel> _batchInputs = [];

    /// <summary>Review state for the last patch execution or preview, extracted from the editor shell.</summary>
    public PatchReviewViewModel PatchReview { get; } = new();

    /// <summary>Individual invocation results; publication remains a root-run decision.</summary>
    public IReadOnlyList<PatchCompositeExecutionViewModel> CompositeExecutions => PatchReview.CompositeExecutions;

    /// <summary>Bounded review rows from the last execution or preview.</summary>
    public IReadOnlyList<PatchChangeViewModel> PatchChanges => PatchReview.PatchChanges;

    /// <summary>Whether a patch review panel should be visible.</summary>
    public bool HasPatchChanges => PatchReview.HasPatchChanges;

    /// <summary>Includes a meaningful preflight result even when no file changes were staged.</summary>
    public bool HasPatchReview => PatchReview.HasPatchReview;

    /// <summary>Localized publication outcome, distinct from node execution success.</summary>
    public string PatchOutcomeText => PatchReview.PatchOutcomeText;

    /// <summary>Whether the bounded report omitted additional skipped composites.</summary>
    public bool CompositeReportTruncated => PatchReview.CompositeReportTruncated;

    private Stack<IEditorCommand> _undoStack = [];
    private Stack<IEditorCommand> _redoStack = [];
    private const int MaxUndoDepth = 100;
    private readonly Dictionary<(object Target, string Property), object?> _trackedPropertyValues = [];
    private readonly Dictionary<object, PropertyChangedEventHandler> _propertyHandlers = [];
    private bool _suppressPropertyTracking;

    /// <summary>
    /// <para>统一日志服务实例，节点编辑器与执行引擎共享。</para>
    /// Unified log service instance, shared between the node editor and the execution engine.
    /// </summary>
    private readonly Services.LogService _log = Services.LogService.Instance;

    /// <summary>
    /// <para>节点工作流执行引擎实例，按需懒构造。</para>
    /// Node workflow execution engine instance, lazily constructed on demand.
    /// </summary>
    private Services.Execution.NodeExecutionEngine? _engine;

    /// <summary>
    /// <para>执行取消令牌源。</para>
    /// Cancellation token source for execution.
    /// </summary>
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// <para>工具箱搜索防抖取消令牌源。</para>
    /// Cancellation token source for toolbox search debounce.
    /// </summary>
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>
    /// <para>获取或设置画布上的节点集合。</para>
    /// Gets or sets the collection of nodes on the canvas.
    /// </summary>
    public ObservableCollection<NodeViewModels.FlowNodeViewModel> Nodes
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureEditable();
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field.CollectionChanged -= OnNodesChanged;
            DetachAllPropertyTracking();
            SetProperty(ref field, value);
            field.CollectionChanged += OnNodesChanged;
            OnNodesChanged(this, new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            SetSelection([]);
        }
    } = [];

    /// <summary>
    /// <para>获取或设置节点间的连接线集合。</para>
    /// Gets or sets the collection of connections between nodes.
    /// </summary>
    public ObservableCollection<NodeViewModels.NodeConnectionViewModel> Connections
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureEditable();
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field.CollectionChanged -= OnConnectionsChanged;
            SetProperty(ref field, value);
            field.CollectionChanged += OnConnectionsChanged;
            SelectConnection(null);
            RefreshParameterPresentation();
        }
    } = [];

    private NodeConnectionViewModel? _selectedConnection;

    /// <summary>The exclusively selected canvas connection.</summary>
    public NodeConnectionViewModel? SelectedConnection => _selectedConnection;

    /// <summary>Whether the shared delete action has a node or connection target.</summary>
    public bool HasSelection => HasSelectedNodes || SelectedConnection is not null;

    /// <summary>Selects a connection and clears node selection, or clears the connection selection.</summary>
    public void SelectConnection(NodeConnectionViewModel? connection)
    {
        if (connection is not null && !Connections.Contains(connection)) return;
        if (connection is not null) SetSelection([]);
        if (_selectedConnection is not null) _selectedConnection.IsSelected = false;
        _selectedConnection = connection;
        if (connection is not null) connection.IsSelected = true;
        OnPropertyChanged(nameof(SelectedConnection));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        OnPropertyChanged(nameof(DeleteSelectionText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnConnectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Services.Interactions.InteractionSchemaResolver.Refresh(Nodes, Connections);
        RefreshParameterPresentation();
        if (_selectedConnection is not null && !Connections.Contains(_selectedConnection))
        {
            SelectConnection(null);
        }
    }

    /// <summary>
    /// <para>获取或设置画布的缩放级别。</para>
    /// Gets or sets the zoom level of the canvas.
    /// </summary>
    public double Zoom
    {
        get => field;
        set => SetProperty(ref field, value);
    } = 1.0;

    /// <summary>
    /// <para>获取或设置是否显示小地图。</para>
    /// Gets or sets whether to show the minimap.
    /// </summary>
    public bool ShowMinimap
    {
        get => field;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>
    /// <para>获取或设置节点搜索文本。</para>
    /// Gets or sets the node search text.
    /// </summary>
    public string SearchText
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {

                _searchDebounceCts?.Cancel();
                _searchDebounceCts?.Dispose();

                var cts = new CancellationTokenSource();
                _searchDebounceCts = cts;

                _ = RefreshToolboxAfterDelayAsync(cts.Token);
            }
        }
    } = string.Empty;

    private async Task RefreshToolboxAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested) RefreshVisibleToolbox();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Nodes", ex.Message); }
    }

    /// <summary>
    /// <para>获取或设置执行状态文本。</para>
    /// Gets or sets the execution status text.
    /// </summary>
    public string ExecutionStatus
    {
        get => field;
        set => SetProperty(ref field, value);
    } = LanguageService.Instance.GetString("Node_StatusReady");

    /// <summary>
    /// <para>获取节点工具箱分类列表（全量，由插件注册填充）。</para>
    /// Gets the full list of node toolbox categories, populated from registered plugins.
    /// </summary>
    public ObservableCollection<NodeToolboxCategory> ToolboxCategories { get; } = [];

    /// <summary>
    /// <para>获取搜索过滤后的工具箱分类视图，UI 绑定此项。</para>
    /// Gets the search-filtered toolbox category view bound by the UI.
    /// </summary>
    public ObservableCollection<NodeToolboxCategory> VisibleToolboxCategories
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>
    /// <para>获取或设置当前选中的节点，绑定到属性面板。</para>
    /// Gets or sets the currently selected node, bound to the properties panel.
    /// </summary>
    public NodeViewModels.FlowNodeViewModel? SelectedNode
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                WeakReferenceMessenger.Default.Send(new NodeSelectionChangedMessage(value));
            }
        }
    }

    /// <summary>
    /// <para>获取或设置执行进度（0.0 ~ 1.0）。</para>
    /// Gets or sets the execution progress (0.0 ~ 1.0).
    /// </summary>
    public double ExecutionProgress
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取或设置是否正在执行工作流。</para>
    /// Gets or sets whether a workflow is currently executing.
    /// </summary>
    public bool IsExecuting
    {
        get => field;
        private set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }
            foreach (var node in Nodes)
            {
                node.IsEditingLocked = value;
                foreach (var port in node.Input.Concat(node.Output))
                {
                    port.IsEditingLocked = value;
                }
            }
            OnPropertyChanged(nameof(CanEditGraph));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(CanDeleteSelection));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(CanCollapseExpandedComposite));
            RunCommand.NotifyCanExecuteChanged();
            PreviewCommand.NotifyCanExecuteChanged();
            ValidateGraphCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanEditGraph => !IsExecuting;
    public bool CanRun => !IsExecuting && Nodes.Count > 0;
    public bool CanDeleteSelection => !IsExecuting && HasSelection;
    /// <summary>Short workflow summaries shown in the bottom panel; detailed diagnostics remain in the global log.</summary>
    public ObservableCollection<string> ExecutionLog { get; } = [];

    /// <summary>Installed composite nodes available for isolated graph editing.</summary>
    public VisualPluginWorkspaceViewModel PluginWorkspace { get; }

    /// <summary>Whether the property pane exposes the plugin workspace.</summary>
    public bool ShowVisualPluginWorkspace => PluginWorkspace.ShowWorkspace;

    /// <summary>Installed composite nodes available for isolated graph editing.</summary>
    public ObservableCollection<VisualPluginRegistration> EditableVisualPlugins => PluginWorkspace.EditableVisualPlugins;

    /// <summary>The composite node selected in the property-pane plugin workspace.</summary>
    public VisualPluginRegistration? SelectedVisualPlugin
    {
        get => PluginWorkspace.SelectedVisualPlugin;
        set => PluginWorkspace.SelectedVisualPlugin = value;
    }

    /// <summary>Whether the current publish form blocks any plugin kind in the property-pane workspace.</summary>
    public bool CodeRuntimeDisabled => PluginWorkspace.CodeRuntimeDisabled;

    /// <summary>Localized explanation shown while plugin kinds are blocked; empty otherwise.</summary>
    public string CodeRuntimeDisabledText => PluginWorkspace.CodeRuntimeDisabledText;

    /// <summary>
    /// <para>执行期间订阅 LogService 的退订委托。</para>
    /// Unsubscribe delegate for the LogService subscription during execution.
    /// </summary>

    private Action? _executionLogUnsubscribe;
    private readonly HashSet<string> _executionLogSources = new(StringComparer.Ordinal);

    /// <summary>
    /// <para>执行日志允许的日志来源集合（Engine、NodeEditor 或任一节点 Title）。</para>
    /// Allowed log sources for the execution log (Engine, NodeEditor, or any node Title).
    /// </summary>

    /// <summary>
    /// <para>获取是否能执行撤销操作。</para>
    /// Gets whether an undo operation is available.
    /// </summary>
    public bool CanUndo => !IsExecuting && _undoStack.Count > 0;

    /// <summary>
    /// <para>获取是否能执行重做操作。</para>
    /// Gets whether a redo operation is available.
    /// </summary>
    public bool CanRedo => !IsExecuting && _redoStack.Count > 0;

    /// <summary>
    /// <para>获取待处理连接视图模型，管理拖拽连线状态。</para>
    /// Gets the pending connection view model, managing the drag-connection state.
    /// </summary>
    public PendingConnectionViewModel PendingConnection { get; }

    /// <summary>
    /// <para>初始化节点编辑器视图模型，从插件注册表构建工具箱，并初始化搜索过滤视图。</para>
    /// Initializes the node editor view model, building the toolbox from the plugin registry, and initializing the search-filter view.
    /// </summary>
    public NodeEditorViewModel(bool showVisualPluginWorkspace = true)
    {
        PluginWorkspace = new VisualPluginWorkspaceViewModel(showVisualPluginWorkspace);
        PatchReview.PropertyChanged += (_, args) => OnPropertyChanged(args.PropertyName);
        SelectedNodes = new ReadOnlyObservableCollection<NodeViewModels.FlowNodeViewModel>(_selectedNodes);
        _draftDocument = new NodeProjectDocument(string.Empty, Nodes, Connections);
        _activeDocument = _draftDocument;
        Connections.CollectionChanged += OnConnectionsChanged;
        Nodes.CollectionChanged += OnNodesChanged;
        BatchInputs = new ReadOnlyObservableCollection<NodeViewModels.BatchNodeInputViewModel>(_batchInputs);
        InitializeToolbox();
        PendingConnection = new PendingConnectionViewModel(this);
        RefreshVisibleToolbox();
        WeakReferenceMessenger.Default.Register<NodeEditorActionMessage>(this, OnNodeEditorAction);
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        NodeFactoryRegistrar.DescriptorsChanged += OnDescriptorsChanged;
    }

    private void OnDescriptorsChanged(object? sender, EventArgs e)
    {
        void Refresh()
        {
            InitializeToolbox();
            RefreshVisibleToolbox();
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Refresh();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var node in Nodes)
        {
            node.RefreshLocalization();
        }
        foreach (var choice in ValueTypeChoices)
        {
            choice.RefreshLocalization();
        }
        PatchReview.RefreshLocalization();
        InitializeToolbox();
        RefreshVisibleToolbox();
        OnPropertyChanged(nameof(DeleteSelectionText));
        OnPropertyChanged(nameof(SelectedCategoryDisplay));
        OnPropertyChanged(nameof(SelectedTypeDisplay));
        RefreshBatchInputs();
    }

    /// <summary>
    /// <para>响应节点编辑器动作消息，执行撤销/重做/剪切/复制/粘贴/删除/全选等编辑操作。</para>
    /// Responds to node-editor action messages, performing undo/redo/cut/copy/paste/delete/select-all edit operations.
    /// </summary>
    /// <param name="recipient">
    /// <para>消息接收者。</para>
    /// The message recipient.
    /// </param>
    /// <param name="message">
    /// <para>包含动作类型的消息。</para>
    /// The message containing the action type.
    /// </param>
    private void OnNodeEditorAction(object recipient, NodeEditorActionMessage message)
    {
        if (message.EditorId == EditorId)
        {
            ExecuteAction(message.Action);
        }
    }

    internal void ExecuteAction(NodeEditorAction action)
    {
        switch (action)
        {
            case NodeEditorAction.Delete:
                DeleteSelected();
                break;
            case NodeEditorAction.SelectAll:
                SetSelection(Nodes);
                break;
            case NodeEditorAction.Copy:
                CopySelectedToClipboard();
                break;
            case NodeEditorAction.Cut:
                if (IsExecuting)
                {
                    return;
                }
                CopySelectedToClipboard();
                DeleteSelectedNodes();
                break;
            case NodeEditorAction.Paste:
                PasteFromClipboard();
                break;
            case NodeEditorAction.Undo:
                Undo();
                break;
            case NodeEditorAction.Redo:
                Redo();
                break;
            case NodeEditorAction.Run:
                _ = RunAsync();
                break;
            case NodeEditorAction.Stop:
                Stop();
                break;
            case NodeEditorAction.ZoomIn:
                ZoomIn();
                break;
            case NodeEditorAction.ZoomOut:
                ZoomOut();
                break;
            case NodeEditorAction.ResetZoom:
                ResetZoom();
                break;
            case NodeEditorAction.ToggleMinimap:
                ToggleMinimap();
                break;
        }
    }

    /// <summary>
    /// <para>将命令推入撤销栈并执行，同时清空重做栈。当撤销栈达到深度上限时丢弃最旧命令。</para>
    /// Pushes a command onto the undo stack, executes it, and clears the redo stack. Drops the oldest
    /// command when the undo stack reaches its depth limit.
    /// </summary>
    /// <param name="command">
    /// <para>要执行的命令。</para>
    /// The command to execute.
    /// </param>
    private void ExecuteCommand(NodeViewModels.EditorCommands.IEditorCommand command)
    {
        if (IsExecuting)
        {
            return;
        }
        command.Execute();
        _undoStack.Push(command);
        if (_undoStack.Count > MaxUndoDepth)
        {
            var retained = _undoStack.Take(MaxUndoDepth).ToArray();
            _undoStack.Clear();
            for (var index = retained.Length - 1; index >= 0; index--)
            {
                _undoStack.Push(retained[index]);
            }
        }
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanCollapseExpandedComposite));
    }

    /// <summary>
    /// <para>撤销最近一次编辑操作。</para>
    /// Undoes the most recent edit operation.
    /// </summary>
    public void Undo()
    {
        if (IsExecuting)
        {
            return;
        }
        if (_undoStack.Count == 0)
        {
            _log.Write(LogLevel.Info, "NodeEditor", "Undo requested but undo stack is empty");
            return;
        }
        var command = _undoStack.Pop();
        _suppressPropertyTracking = true;
        try
        {
            command.Undo();
        }
        finally
        {
            _suppressPropertyTracking = false;
        }
        _redoStack.Push(command);
        SetSelection([.. _selectedNodes.Where(Nodes.Contains)]);
        RefreshSelectionProperties();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanCollapseExpandedComposite));
        _log.Write(LogLevel.Info, "NodeEditor", $"Undo: {command.DisplayName}");
    }

    /// <summary>
    /// <para>重做最近一次被撤销的编辑操作。</para>
    /// Redoes the most recently undone edit operation.
    /// </summary>
    public void Redo()
    {
        if (IsExecuting)
        {
            return;
        }
        if (_redoStack.Count == 0)
        {
            _log.Write(LogLevel.Info, "NodeEditor", "Redo requested but redo stack is empty");
            return;
        }
        var command = _redoStack.Pop();
        _suppressPropertyTracking = true;
        try
        {
            command.Execute();
        }
        finally
        {
            _suppressPropertyTracking = false;
        }
        _undoStack.Push(command);
        RefreshSelectionProperties();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanCollapseExpandedComposite));
        _log.Write(LogLevel.Info, "NodeEditor", $"Redo: {command.DisplayName}");
    }

    /// <summary>
    /// <para>赋予节点通过可撤销命令入栈的编辑操作，供批量操作合并为单个命令使用。</para>
    /// Records an edit via a command pushed onto the stack; used to merge batch operations into one command.
    /// </summary>
    /// <param name="command">
    /// <para>要执行的命令。</para>
    /// The command to execute.
    /// </param>
    public void Record(NodeViewModels.EditorCommands.IEditorCommand command)
    {
        ExecuteCommand(command);
    }

    /// <summary>
    /// <para>将选中节点序列化到剪贴板（简易内存剪贴板）。</para>
    /// Serializes selected nodes to the clipboard (simple in-memory clipboard).
    /// </summary>
    private void CopySelectedToClipboard()
    {
        var selected = Nodes.Where(n => n.IsSelected).ToList();
        if (selected.Count == 0) return;
        _clipboard = WorkflowPersistenceService.CloneForEditing(new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(selected),
            Connections = [.. WorkflowPersistenceService.BuildConnectionDtos(selected, Connections).Where(edge => edge.SourceInstanceId is not null && edge.TargetInstanceId is not null)]
        });
        _pasteCount = 0;
        _log.Write(LogLevel.Info, "NodeEditor", $"Copied {selected.Count} node(s)");
    }

    /// <summary>
    /// <para>从剪贴板粘贴节点，复制到原位置偏移。</para>
    /// Pastes nodes from the clipboard, offset from their original positions.
    /// </summary>
    private void PasteFromClipboard()
    {
        if (IsExecuting || _clipboard is not { Nodes.Count: > 0 } clipboard)
        {
            return;
        }
        try
        {
            var graph = BuildEditableGraph(WorkflowPersistenceService.CloneForEditing(clipboard), restoreInstanceIds: false);
            var offset = new Point(40 * (++_pasteCount), 40 * _pasteCount);
            foreach (var node in graph.Nodes)
            {
                node.Location += offset;
            }
            AppendGraph(graph);
            FocusSelection();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ExecutionStatus = UserError.Describe(error, "UserError_RunFailed");
        }
    }

    private static WorkflowSnapshot? _clipboard;
    private int _pasteCount;

    private void AttachNodePropertyTracking(NodeViewModels.FlowNodeViewModel node)
    {
        AttachPropertyTracking(node);
        foreach (var connector in node.Input.Concat(node.Output))
        {
            AttachPropertyTracking(connector);
        }
        node.RefreshCanvasInputs(CompactNodes);
    }

    /// <summary>Gets the nodes currently selected on the canvas.</summary>
    public ReadOnlyObservableCollection<NodeViewModels.FlowNodeViewModel> SelectedNodes { get; }

    /// <summary>Gets the shared input properties for a multi-node selection.</summary>
    public ReadOnlyObservableCollection<NodeViewModels.BatchNodeInputViewModel> BatchInputs { get; }

    /// <summary>Gets the number of selected nodes.</summary>
    public int SelectedNodeCount => _selectedNodes.Count;

    /// <summary>Gets whether at least one node is selected.</summary>
    public bool HasSelectedNodes => _selectedNodes.Count > 0;

    /// <summary>Gets whether exactly one node is selected.</summary>
    public bool IsSingleSelection => _selectedNodes.Count == 1;

    /// <summary>Gets whether more than one node is selected.</summary>
    public bool IsMultipleSelection => _selectedNodes.Count > 1;

    /// <summary>Gets the localized label for deleting the current selection.</summary>
    public string DeleteSelectionText => SelectedConnection is not null
        ? LanguageService.Instance.GetString("Node_DeleteConnection")
        : HasSelectedNodes
        ? LanguageService.Instance.GetString("Node_DeleteSelectionCount", SelectedNodeCount)
        : LanguageService.Instance.GetString("Node_DeleteSelected");

    /// <summary>Gets the common category, or a localized mixed-value label when selected nodes differ.</summary>
    public string SelectedCategoryDisplay => CommonSelectionDisplay(nodes => nodes.Select(node =>
        LanguageService.Instance.GetString("Node_Category_" + (NodeFactoryRegistrar.Descriptors.FirstOrDefault(
            descriptor => descriptor.TypeKey == node.TypeKey)?.CategoryPath ?? "Composite"))));

    /// <summary>Gets the common type key, or a localized mixed-value label when selected nodes differ.</summary>
    public string SelectedTypeDisplay => CommonSelectionDisplay(nodes => nodes.Select(node => node.TypeKey));

    /// <summary>Gets whether every selected node supports constant-value editing.</summary>
    public bool CanEditBatchConstant => IsMultipleSelection && _selectedNodes.All(node => node.IsConstantNode);

    /// <summary>Gets whether every selected node supports project-image editing.</summary>
    public bool CanEditBatchProjectImage => IsMultipleSelection && _selectedNodes.All(node => node.IsProjectImageNode);

    /// <summary>Gets or sets the common constant value type for the current selection.</summary>
    public string BatchNodeValueType
    {
        get => CommonSelectionValue(nodes => nodes.Select(node => node.NodeValueType));
        set => ApplyBatchNodeProperty(node => node.NodeValueType, (node, value) => node.NodeValueType = value,
            value, nameof(BatchNodeValueType));
    }

    /// <summary>Gets or sets the common constant value for the current selection.</summary>
    public string BatchNodeValue
    {
        get => CommonSelectionValue(nodes => nodes.Select(node => node.NodeValue));
        set => ApplyBatchNodeProperty(node => node.NodeValue, (node, value) => node.NodeValue = value,
            value, nameof(BatchNodeValue));
    }

    /// <summary>Gets or sets the common selected image for the current selection.</summary>
    public string BatchSelectedImage
    {
        get => CommonSelectionValue(nodes => nodes.Select(node => node.SelectedImage));
        set => ApplyBatchNodeProperty(node => node.SelectedImage, (node, value) => node.SelectedImage = value,
            value, nameof(BatchSelectedImage));
    }

    private string CommonSelectionValue(Func<IEnumerable<NodeViewModels.FlowNodeViewModel>, IEnumerable<string?>> selector)
    {
        if (_selectedNodes.Count == 0) return string.Empty;
        var values = selector(_selectedNodes).Distinct(StringComparer.Ordinal).ToList();
        return values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
    }

    private string CommonSelectionDisplay(Func<IEnumerable<NodeViewModels.FlowNodeViewModel>, IEnumerable<string?>> selector)
    {
        if (_selectedNodes.Count == 0) return string.Empty;
        var values = selector(_selectedNodes).Distinct(StringComparer.Ordinal).ToList();
        return values.Count == 1
            ? values[0] ?? string.Empty
            : LanguageService.Instance.GetString("Node_BatchMixed");
    }

    private void ApplyBatchNodeProperty(
        Func<NodeViewModels.FlowNodeViewModel, string> getter,
        Action<NodeViewModels.FlowNodeViewModel, string> setter,
        string value,
        string propertyName)
    {
        if (!CanEditBatchConstant
            && (propertyName == nameof(BatchNodeValueType)
                || propertyName == nameof(BatchNodeValue))) return;
        if (!CanEditBatchProjectImage && propertyName == nameof(BatchSelectedImage)) return;

        var commands = _selectedNodes
            .Where(node => !string.Equals(getter(node), value, StringComparison.Ordinal))
            .Select(node => (IEditorCommand)new PropertyChangeCommand(
                $"Edit {propertyName}",
                propertyValue => setter(node, propertyValue as string ?? string.Empty),
                getter(node),
                value))
            .ToList();
        if (commands.Count == 0) return;

        _suppressPropertyTracking = true;
        try
        {
            ExecuteCommand(new CompoundCommand(commands));
        }
        finally
        {
            _suppressPropertyTracking = false;
        }
        RefreshSelectionProperties();
    }

    /// <summary>
    /// Synchronizes the canvas selection with the ViewModel. The first item is kept as the primary
    /// selection for status text, while all selected nodes remain available to the batch panel.
    /// </summary>
    public void SetSelection(IEnumerable<NodeViewModels.FlowNodeViewModel> nodes,
        NodeViewModels.FlowNodeViewModel? primary = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (_selectedConnection is not null) SelectConnection(null);
        var selected = nodes.Where(Nodes.Contains).Distinct().ToList();
        foreach (var node in _selectedNodes)
        {
            if (!selected.Contains(node)) node.IsSelected = false;
        }

        foreach (var node in Nodes)
        {
            var shouldSelect = selected.Contains(node);
            if (node.IsSelected != shouldSelect) node.IsSelected = shouldSelect;
        }

        _selectedNodes.Clear();
        foreach (var node in selected) _selectedNodes.Add(node);
        var nextPrimary = primary is not null && selected.Contains(primary) ? primary : selected.FirstOrDefault();
        SelectedNode = nextPrimary;
        RefreshParameterPresentation();
        OnPropertyChanged(nameof(SelectedNodeCount));
        OnPropertyChanged(nameof(HasSelectedNodes));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(IsMultipleSelection));
        OnPropertyChanged(nameof(DeleteSelectionText));
        OnPropertyChanged(nameof(SelectedCategoryDisplay));
        OnPropertyChanged(nameof(SelectedTypeDisplay));
        OnPropertyChanged(nameof(CanEditBatchConstant));
        OnPropertyChanged(nameof(CanEditBatchProjectImage));
        OnPropertyChanged(nameof(BatchNodeValueType));
        OnPropertyChanged(nameof(BatchNodeValue));
        OnPropertyChanged(nameof(BatchSelectedImage));
        RefreshBatchInputs();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedCategoryDisplay));
        OnPropertyChanged(nameof(SelectedTypeDisplay));
        OnPropertyChanged(nameof(BatchNodeValueType));
        OnPropertyChanged(nameof(BatchNodeValue));
        OnPropertyChanged(nameof(BatchSelectedImage));
        RefreshBatchInputs();
    }

    private void RefreshBatchInputs()
    {
        _batchInputs.Clear();
        if (!IsMultipleSelection) return;

        var common = _selectedNodes
            .SelectMany(node => node.Input)
            .GroupBy(port => (port.Title, port.PortType))
            .Where(group => group.Count() == _selectedNodes.Count)
            .OrderBy(group => group.Key.Title, StringComparer.Ordinal);

        foreach (var group in common)
        {
            var ports = group.ToList();
            var values = ports.Select(port => port.InitialValue).ToList();
            var distinct = values.Distinct().ToList();
            _batchInputs.Add(new BatchNodeInputViewModel(
                group.Key.Title,
                group.Key.PortType,
                distinct.Count == 1 ? distinct[0] : null,
                distinct.Count > 1,
                value => ApplyBatchInput(ports, value)));
        }
    }

    private void ApplyBatchInput(IReadOnlyList<NodeConnectorViewModel> ports, object? value)
    {
        var commands = ports
            .Where(port => !Equals(port.InitialValue, value))
            .Select(port => (IEditorCommand)new PropertyChangeCommand(
                $"Edit input {port.Title}",
                propertyValue => port.InitialValue = propertyValue,
                port.InitialValue,
                value))
            .ToList();
        if (commands.Count == 0) return;

        _suppressPropertyTracking = true;
        try
        {
            ExecuteCommand(new CompoundCommand(commands));
        }
        finally
        {
            _suppressPropertyTracking = false;
        }
        RefreshBatchInputs();
    }

    private void AttachPropertyTracking(INotifyPropertyChanged target)
    {
        if (_propertyHandlers.ContainsKey(target)) return;

        var handler = new PropertyChangedEventHandler((_, args) =>
            OnTrackedPropertyChanged(target, args.PropertyName));
        _propertyHandlers[target] = handler;
        target.PropertyChanged += handler;

        foreach (var property in TrackedProperties(target))
        {
            _trackedPropertyValues[(target, property)] = ReadTrackedValue(target, property);
        }
    }

    private static IEnumerable<string> TrackedProperties(object target) => target switch
    {
        NodeViewModels.FlowNodeViewModel => ["NodeValueType", "NodeValue", "SelectedImage", "CustomName"],
        NodeViewModels.NodeConnectorViewModel => ["InitialValue"],
        _ => []
    };

    private static object? ReadTrackedValue(object target, string property) => (target, property) switch
    {
        (NodeViewModels.FlowNodeViewModel node, "NodeValueType") => node.NodeValueType,
        (NodeViewModels.FlowNodeViewModel node, "NodeValue") => node.NodeValue,
        (NodeViewModels.FlowNodeViewModel node, "SelectedImage") => node.SelectedImage,
        (NodeViewModels.FlowNodeViewModel node, "CustomName") => node.CustomName,
        (NodeViewModels.NodeConnectorViewModel connector, "InitialValue") => connector.InitialValue,
        _ => null
    };

    private static void SetTrackedValue(object target, string property, object? value)
    {
        switch (target, property)
        {
            case (NodeViewModels.FlowNodeViewModel node, "NodeValueType"):
                node.NodeValueType = value as string ?? "Text";
                break;
            case (NodeViewModels.FlowNodeViewModel node, "NodeValue"):
                node.NodeValue = value as string ?? string.Empty;
                break;
            case (NodeViewModels.FlowNodeViewModel node, "SelectedImage"):
                node.SelectedImage = value as string ?? string.Empty;
                break;
            case (NodeViewModels.FlowNodeViewModel node, "CustomName"):
                node.CustomName = value as string ?? string.Empty;
                break;
            case (NodeViewModels.NodeConnectorViewModel connector, "InitialValue"):
                connector.InitialValue = value;
                break;
        }
    }

    private void OnTrackedPropertyChanged(INotifyPropertyChanged target, string? propertyName)
    {
        if (target is NodeConnectorViewModel && propertyName == nameof(NodeConnectorViewModel.InitialValue))
        {
            Services.Interactions.InteractionSchemaResolver.Refresh(Nodes, Connections);
            RefreshParameterPresentation();
        }
        else if (propertyName == nameof(FlowNodeViewModel.CustomName))
        {
            RefreshParameterPresentation();
        }
        if (propertyName is null || !TrackedProperties(target).Contains(propertyName, StringComparer.Ordinal)) return;

        var key = ((object)target, propertyName);
        var current = ReadTrackedValue(target, propertyName);
        var original = _trackedPropertyValues.GetValueOrDefault(key);
        _trackedPropertyValues[key] = current;
        if (_suppressPropertyTracking || Equals(original, current)) return;

        var setter = (object? value) =>
        {
            _suppressPropertyTracking = true;
            try
            {
                SetTrackedValue(target, propertyName, value);
            }
            finally
            {
                _suppressPropertyTracking = false;
            }
        };
        Record(new PropertyChangeCommand($"Edit {propertyName}", setter, original, current));
    }

    /// <summary>
    /// <para>将指定节点添加到画布。</para>
    /// Adds the specified node to the canvas.
    /// </summary>
    /// <param name="node">
    /// <para>要添加的节点。</para>
    /// The node to add.
    /// </param>
    public void AddNode(NodeViewModels.FlowNodeViewModel node, bool preserveLocation = false)
    {
        if (IsExecuting)
        {
            return;
        }
        if (!preserveLocation && (node.Location == DefaultNodeLocation || node.Location == default))
        {
            node.Location = InsertionLocationProvider?.Invoke() ?? _nextNodeLocation;
            _nextNodeLocation = new Point(_nextNodeLocation.X + 40, _nextNodeLocation.Y + 40);
        }

        ExecuteCommand(new NodeViewModels.EditorCommands.AddNodeCommand(Nodes, Connections, node));
        AttachNodePropertyTracking(node);
    }

    /// <summary>
    /// <para>在两个连接器之间创建连接。</para>
    /// Creates a connection between two connectors.
    /// </summary>
    /// <param name="source">
    /// <para>源连接器（输出端口）。</para>
    /// The source connector (output port).
    /// </param>
    /// <param name="target">
    /// <para>目标连接器（输入端口）。</para>
    /// The target connector (input port).
    /// </param>
    public void CreateConnection(NodeViewModels.NodeConnectorViewModel source, NodeViewModels.NodeConnectorViewModel target)
    {
        if (IsExecuting)
        {
            return;
        }
        if (source == target) return;
        if (source.IsInput == target.IsInput)
        {
            RejectConnection("Node_Connection_SameDirection", "Connection rejected: connectors have the same direction");
            return;
        }
        if (source.IsInput)
        {
            (source, target) = (target, source);
        }
        if (WorkflowGraphValidation.ValidateConnection(Nodes, Connections, source, target) is { } error)
        {
            ExecutionStatus = error;
            AppendExecutionLog(error);
            return;
        }
        var edge = new NodeConnectionViewModel(source, target);
        var issues = WorkflowGraphValidation.Validate(Nodes, [.. Connections, edge]);
        if (issues.Count > 0)
        {
            ExecutionStatus = issues[0].DisplayText;
            return;
        }
        ExecuteCommand(new AddConnectionCommand(Connections, edge));
    }

    /// <summary>Reports a rejected connection attempt to the execution log so the user sees why nothing happened.</summary>
    private void RejectConnection(string reasonKey, string detail)
    {
        _log.Write(LogLevel.Warn, "NodeEditor", detail);
        ExecutionStatus = LanguageService.Instance.GetString(reasonKey);
        AppendExecutionLog(ExecutionStatus);
    }

    [RelayCommand]
    private void ZoomIn()
    {
        Zoom = Math.Min(Zoom * 1.2, 3.0);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        Zoom = Math.Max(Zoom / 1.2, 0.2);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        Zoom = 1.0;
        ViewportRequested?.Invoke(NodeViewportAction.Reset);
    }

    private void ToggleMinimap()
    {
        ShowMinimap = !ShowMinimap;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelection))]
    private void DeleteSelected()
    {
        if (SelectedConnection is { } connection)
        {
            RemoveConnection(connection);
        }
        else
        {
            DeleteSelectedNodes();
        }
    }

    /// <summary>Deletes every selected node as one undoable operation.</summary>
    public void DeleteSelectedNodes()
    {

        var toDelete = Nodes.Where(n => n.IsSelected).ToList();
        if (toDelete.Count == 0) return;

        var commands = toDelete
            .Select(n => (NodeViewModels.EditorCommands.IEditorCommand)new NodeViewModels.EditorCommands.RemoveNodeCommand(Nodes, Connections, n))
            .ToList();

        ExecuteCommand(new NodeViewModels.EditorCommands.CompoundCommand(commands));
        SetSelection([]);
    }

    /// <summary>
    /// <para>选中指定节点，同时清除其他节点的选中状态。</para>
    /// Selects the specified node, clearing the selection state of others.
    /// </summary>
    /// <param name="node">
    /// <para>要选中的节点；<c>null</c> 表示清空选择。</para>
    /// The node to select; <c>null</c> clears the selection.
    /// </param>
    public void SelectNode(NodeViewModels.FlowNodeViewModel? node)
    {
        SetSelection(node is null ? [] : [node], node);
    }

    /// <summary>
    /// <para>异步执行节点工作流。基于拓扑排序依次执行节点，并将上游输出注入下游输入。</para>
    /// Asynchronously executes the node workflow. Nodes are executed in topological order; upstream outputs are injected into downstream inputs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => ExecuteGraphAsync(false);

    /// <summary>Evaluates supported patch graphs without publishing staged files.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task PreviewAsync() => ExecuteGraphAsync(true);

    private async Task ExecuteGraphAsync(bool preview)
    {
        if (IsExecuting)
        {
            _log.Write(LogLevel.Warn, "NodeEditor", "Run requested while already executing — ignored");
            return;
        }

        if (Nodes.Count == 0)
        {
            _log.Write(LogLevel.Warn, "NodeEditor", "Cannot run: no nodes on canvas");
            return;
        }

        IsExecuting = true;
        FailedNodes = [];
        foreach (var node in Nodes)
        {
            node.ExecutionError = string.Empty;
            node.ExecutionStatus = NodeExecutionStatus.Idle;
        }
        PatchReview.Reset();
        NodeArtifactRegistry.Reset();
        ExecutionStatus = LanguageService.Instance.GetString("Node_Run");
        ExecutionProgress = 0.0;
        _runCts = new CancellationTokenSource();

        ExecutionLog.Clear();
        AppendExecutionLog(LanguageService.Instance.GetString(preview ? "Node_Patch_Preview" : "Node_Run"));

        try
        {
            _engine ??= new NodeExecutionEngine(_log);
            _engine.ProgressChanged += OnExecutionProgress;

            var nodes = Nodes.ToList();
            var issues = WorkflowGraphValidation.Validate(nodes, Connections);
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    if (issue.Node is { } node)
                    {
                        node.ExecutionError = issue.Message;
                    }
                }
                throw new InvalidDataException(string.Join(Environment.NewLine, issues.Select(issue => issue.DisplayText)));
            }
            var connections = Connections
                .Where(c => c.Source != null && c.Target != null)
                .Select(c => new NodeConnection
                {
                    SourceInstanceId = FindOwnerInstanceId(c.Source!) ?? string.Empty,
                    TargetInstanceId = FindOwnerInstanceId(c.Target!) ?? string.Empty,
                    SourceConnector = c.Source!.Title,
                    TargetConnector = c.Target!.Title,
                    SourcePortId = c.Source.StableId,
                    TargetPortId = c.Target.StableId
                })
                .Where(c => !string.IsNullOrEmpty(c.SourceInstanceId) && !string.IsNullOrEmpty(c.TargetInstanceId))
                .ToList();

            ExecutionResult? result = null;
            await ProjectOperationsService.Instance.ExecuteFileOperationAsync(async () =>
            {
                result = await _engine.ExecuteAsync(nodes, connections, _runCts.Token,
                    new NodeExecutionOptions { Preview = preview, Package = ResourcePackage, ValidatePackage = ValidatePackage, Interactions = Interactions, Documents = Documents }).ConfigureAwait(false);
            }, _runCts.Token);
            if (result is null) { throw new InvalidOperationException("Workflow returned no execution result."); }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                PatchReview.Publish(result);
            });

            ExecutionStatus = result.Status switch
            {
                Services.Execution.ExecutionStatus.Succeeded => LanguageService.Instance.GetString(preview ? "Node_Patch_PreviewFinished" : "Node_Patch_RunFinished"),
                Services.Execution.ExecutionStatus.PartiallySucceeded => LanguageService.Instance.GetString("Node_Patch_RunPartial", result.FailedNodes.Count),
                Services.Execution.ExecutionStatus.Cancelled => LanguageService.Instance.GetString("Node_Patch_RunCancelled"),
                Services.Execution.ExecutionStatus.Failed => UserError.WithDetails(result.ErrorMessage, "Node_Patch_RunFailed"),
                _ => LanguageService.Instance.GetString("Node_StatusReady")
            };
            AppendExecutionLog(ExecutionStatus);
            if (result.Status == Services.Execution.ExecutionStatus.Succeeded)
            {
                ExecutionProgress = 1;
            }
            IsExecutionLogExpanded |= result.Status is Services.Execution.ExecutionStatus.Failed or Services.Execution.ExecutionStatus.PartiallySucceeded;
        }
        catch (OperationCanceledException)
        {
            ExecutionStatus = LanguageService.Instance.GetString("Node_Patch_RunCancelled");
            AppendExecutionLog(ExecutionStatus);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "NodeEditor", $"Run failed: {ex.Message}");
            ExecutionStatus = global::UotanStudio.Services.UserError.Describe(ex, "UserError_RunFailed");
            AppendExecutionLog(ExecutionStatus);
            IsExecutionLogExpanded = true;
        }
        finally
        {

            IsExecuting = false;
            FailedNodes = [.. Nodes.Where(node => node.ExecutionStatus == NodeExecutionStatus.Failed || node.ExecutionError.Length > 0)];
            _engine?.ProgressChanged -= OnExecutionProgress;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// <para>停止正在执行的节点工作流。</para>
    /// Stops the currently executing node workflow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsExecuting))]
    private void Stop()
    {
        if (!IsExecuting) return;
        _runCts?.Cancel();
        _log.Write(LogLevel.Warn, "NodeEditor", "Stop requested");
    }

    /// <summary>
    /// <para>清空画布上的所有节点和连接线。</para>
    /// Clears all nodes and connections on the canvas.
    /// </summary>
    [RelayCommand]
    public void Clear()
    {
        if (IsExecuting)
        {
            return;
        }
        DetachAllPropertyTracking();
        SetSelection([]);
        Connections.Clear();
        Nodes.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        ExecutionProgress = 0.0;
        ExecutionStatus = LanguageService.Instance.GetString("Node_StatusReady");
        PatchReview.Clear();
        FailedNodes = [];
        IsExecutionLogExpanded = false;
        ExecutionLog.Clear();
        _nextNodeLocation = DefaultNodeLocation;
    }

    private void DetachAllPropertyTracking()
    {
        foreach (var (target, handler) in _propertyHandlers)
        {
            ((INotifyPropertyChanged)target).PropertyChanged -= handler;
        }
        _propertyHandlers.Clear();
        _trackedPropertyValues.Clear();
    }

    private void DetachPropertyTracking(INotifyPropertyChanged target)
    {
        if (_propertyHandlers.Remove(target, out var handler))
        {
            target.PropertyChanged -= handler;
        }
        foreach (var property in TrackedProperties(target))
        {
            _trackedPropertyValues.Remove((target, property));
        }
    }

    private void OnExecutionProgress(object? sender, double progress)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ExecutionProgress = progress;
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsExecuting)
                {
                    ExecutionProgress = progress;
                }
            });
        }
    }

    /// <summary>Appends one line to the execution log for the current run.</summary>
    private void AppendExecutionLog(string line)
    {
        ExecutionLog.Add(line);
    }

    private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs e)
    {

        var node = e.Node;
        void Append()
        {
            AppendExecutionLog($"{node.Title} -> {node.StatusDisplay}");
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Append);
        }

        _log.Write(LogLevel.Debug, "NodeEditor", $"Node '{node.Title}' status changed to {node.ExecutionStatus}  节点状态变更。");
    }

    /// <summary>
    /// <para>执行日志条目回调：按来源过滤（Engine、NodeEditor 或任一节点 Title），并调度到 UI 线程追加。</para>
    /// Execution log entry callback: filters by source (Engine, NodeEditor, or any node Title) and appends on the UI thread.
    /// </summary>
    private void OnExecutionLogEnqueued(object? sender, LogEntry entry)
    {
        if (!_executionLogSources.Contains(entry.Source)) return;

        void Append()
        {
            AppendExecutionLog($"[{entry.Timestamp:HH:mm:ss}] {entry.Source}: {entry.Message}");
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Append);
        }
    }

    private void OnExecutionCompleted(object? sender, ExecutionCompletedEventArgs e)
    {
        _log.Write(LogLevel.Info, "NodeEditor", $"Execution completed: {e.Result.Status}");
    }

    /// <summary>
    /// <para>查找拥有指定连接器的节点实例 ID。若无匹配则返回 <c>null</c>。</para>
    /// Finds the node instance ID that owns the specified connector. Returns <c>null</c> if no match.
    /// </summary>
    private string? FindOwnerInstanceId(NodeViewModels.NodeConnectorViewModel connector)
    {
        foreach (var node in Nodes)
        {
            if (node.Input.Contains(connector) || node.Output.Contains(connector))
            {
                return node.InstanceId;
            }
        }
        return null;
    }

    /// <summary>
    /// <para>应用搜索过滤，更新 <see cref="VisibleToolboxCategories"/>。</para>
    /// Applies the search filter, updating <see cref="VisibleToolboxCategories"/>.
    /// </summary>

    /// <summary>
    /// <para>断开连接器上的所有连接，由 NodifyEditor 的 DisconnectConnectorCommand 触发。</para>
    /// Disconnects all connections on a connector, triggered by NodifyEditor's DisconnectConnectorCommand.
    /// </summary>
    /// <param name="connector">
    /// <para>要断开连接的连接器。</para>
    /// The connector to disconnect.
    /// </param>
    [RelayCommand]
    private void DisconnectConnector(NodeViewModels.NodeConnectorViewModel? connector)
    {
        if (connector == null) return;
        var related = Connections
            .Where(c => c.Source == connector || c.Target == connector)
            .ToList();
        if (related.Count == 0) return;

        var commands = related
            .Select(c => (NodeViewModels.EditorCommands.IEditorCommand)new NodeViewModels.EditorCommands.RemoveConnectionCommand(Connections, c))
            .ToList();
        ExecuteCommand(new NodeViewModels.EditorCommands.CompoundCommand(commands));
    }

    /// <summary>Removes one canvas connection through the undoable editor command stack.</summary>
    [RelayCommand]
    private void RemoveConnection(NodeViewModels.NodeConnectionViewModel? connection)
    {
        if (connection is null || !Connections.Contains(connection)) return;
        ExecuteCommand(new NodeViewModels.EditorCommands.RemoveConnectionCommand(Connections, connection));
    }

    /// <summary>
    /// <para>从工作流快照重建画布：清空当前节点与连接，按 DTO 重建节点，再尝试匹配重建连接。</para>
    /// Rebuilds the canvas from a workflow snapshot: clears current nodes/connections, rebuilds nodes from DTOs, then attempts to rebuild connections.
    /// </summary>
    /// <param name="snapshot">
    /// <para>工作流快照。</para>
    /// The workflow snapshot.
    /// </param>
    /// <returns>
    /// <para>表示异步操作的任务。</para>
    /// A task representing the async operation.
    /// </returns>
    public Task LoadFromAsync(Services.Projects.WorkflowSnapshot snapshot)
    {
        EnsureEditable();
        var graph = BuildEditableGraph(snapshot, restoreInstanceIds: true);
        Clear();
        foreach (var node in graph.Nodes)
        {
            Nodes.Add(node);
            AttachNodePropertyTracking(node);
        }
        foreach (var connection in graph.ViewConnections)
        {
            Connections.Add(connection);
        }
        _log.Write(LogLevel.Info, "NodeEditor",
            $"Loaded {Nodes.Count} node(s), {Connections.Count} connection(s) from snapshot");
        RefreshParameterPresentation();
        FitAll();
        return Task.CompletedTask;
    }

    /// <summary>Appends a portable workflow template as one undoable canvas edit.</summary>
    public void ImportTemplate(Services.Projects.VisualPluginPackage package)
    {
        EnsureEditable();
        ArgumentNullException.ThrowIfNull(package);
        if (package.Kind != Services.Projects.VisualPluginKind.WorkflowTemplate)
            throw new InvalidDataException("Only workflow-template packages can be imported into the canvas.");

        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        var graph = BuildEditableGraph(package.Graph, restoreInstanceIds: false);
        if (graph.Nodes.Count == 0)
        {
            throw new InvalidDataException(LanguageService.Instance.GetString("Node_Graph_Empty"));
        }
        var position = InsertionLocationProvider?.Invoke() ?? _nextNodeLocation;
        var offset = position - new Point(graph.Nodes.Min(node => node.Location.X), graph.Nodes.Min(node => node.Location.Y));
        foreach (var node in graph.Nodes)
        {
            node.Location += offset;
        }
        AppendGraph(graph);
        _nextNodeLocation += new Point(40, 40);
        FocusSelection();
    }

    private static WorkflowRuntime BuildEditableGraph(WorkflowSnapshot snapshot, bool restoreInstanceIds)
    {
        var graph = WorkflowRuntimeBuilder.Build(snapshot, preserveUnknown: true, restoreInstanceIds,
            canvasIdentityComparison: true);
        if (graph.Errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, graph.Errors));
        }
        return graph;
    }

    private void AppendGraph(WorkflowRuntime graph)
    {
        var commands = graph.Nodes.Select(node => (IEditorCommand)new AddNodeCommand(Nodes, Connections, node)).ToList();
        commands.AddRange(graph.ViewConnections.Select(edge => new AddConnectionCommand(Connections, edge)));
        ExecuteCommand(new CompoundCommand(commands));
        foreach (var node in graph.Nodes)
        {
            AttachNodePropertyTracking(node);
        }
        SetSelection(graph.Nodes);
        RefreshParameterPresentation();
    }

    private void EnsureEditable()
    {
        if (IsExecuting)
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Graph_Running"));
        }
    }

    /// <summary>Captures the current canvas as a portable visual-plugin package.</summary>
    public Services.Projects.VisualPluginPackage CreateVisualPluginPackage(
        string name, string id, Services.Projects.VisualPluginKind kind, string author = "")
        => Services.VisualPlugins.VisualPluginPackageBuilder.Create(name, id, kind, Nodes, Connections, author);

    public void Dispose()
    {
        Nodes.CollectionChanged -= OnNodesChanged;
        Connections.CollectionChanged -= OnConnectionsChanged;
        DetachAllPropertyTracking();
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        NodeFactoryRegistrar.DescriptorsChanged -= OnDescriptorsChanged;
        PluginWorkspace.Dispose();
        WeakReferenceMessenger.Default.Unregister<NodeEditorActionMessage>(this);
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _executionLogUnsubscribe?.Invoke();
        _executionLogUnsubscribe = null;
    }
}