using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
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
    /// <summary>Resource scope supplied by the composite editor; ordinary project canvases have no package scope.</summary>
    public Services.Projects.VisualPluginPackage? ResourcePackage { get; set; }

    /// <summary>Host-provided dependency check for the package currently being edited.</summary>
    public Func<IReadOnlyList<string>>? ValidatePackage { get; set; }
    private ObservableCollection<NodeViewModels.FlowNodeViewModel> _nodes = [];
    private ObservableCollection<NodeViewModels.NodeConnectionViewModel> _connections = [];
    private double _zoom = 1.0;
    private bool _showMinimap = true;
    private string _searchText = string.Empty;
    private string _executionStatus = LanguageService.Instance.GetString("Node_StatusReady");
    private Point _nextNodeLocation = new(100, 100);
    private static readonly Point DefaultNodeLocation = new(100, 100);

    private NodeViewModels.FlowNodeViewModel? _selectedNode;
    private ObservableCollection<NodeToolboxCategory> _visibleToolboxCategories = [];
    private readonly ObservableCollection<NodeViewModels.FlowNodeViewModel> _selectedNodes = [];
    private readonly ObservableCollection<NodeViewModels.BatchNodeInputViewModel> _batchInputs = [];
    private double _executionProgress;
    private bool _isExecuting;
    private IReadOnlyList<PatchChangeViewModel> _patchChanges = [];
    private PatchRunOutcome _patchOutcome;
    private bool _compositeReportTruncated;

    /// <summary>Whether the bounded report omitted additional skipped composites.</summary>
    public bool CompositeReportTruncated
    {
        get => _compositeReportTruncated;
        private set => SetProperty(ref _compositeReportTruncated, value);
    }
    private IReadOnlyList<PatchCompositeExecutionViewModel> _compositeExecutions = [];

    /// <summary>Individual invocation results; publication remains a root-run decision.</summary>
    public IReadOnlyList<PatchCompositeExecutionViewModel> CompositeExecutions
    {
        get => _compositeExecutions;
        private set
        {
            if (SetProperty(ref _compositeExecutions, value))
            {
                OnPropertyChanged(nameof(HasPatchReview));
            }
        }
    }

    /// <summary>Bounded review rows from the last execution or preview.</summary>
    public IReadOnlyList<PatchChangeViewModel> PatchChanges
    {
        get => _patchChanges;
        private set
        {
            if (SetProperty(ref _patchChanges, value))
            {
                OnPropertyChanged(nameof(HasPatchChanges));
                OnPropertyChanged(nameof(HasPatchReview));
            }
        }
    }

    /// <summary>Whether a patch review panel should be visible.</summary>
    public bool HasPatchChanges => PatchChanges.Count > 0;

    /// <summary>Includes a meaningful preflight result even when no file changes were staged.</summary>
    public bool HasPatchReview => HasPatchChanges || CompositeExecutions.Count > 0 || _patchOutcome is PatchRunOutcome.NotApplicable
        or PatchRunOutcome.NoChanges or PatchRunOutcome.AlreadyApplied or PatchRunOutcome.Conflict;

    /// <summary>Localized publication outcome, distinct from node execution success.</summary>
    public string PatchOutcomeText => LanguageService.Instance.GetString("Node_PatchOutcome_" + _patchOutcome);
    private VisualPluginRegistration? _selectedVisualPlugin;

    // ---- 撤销/重做命令栈 ----
    private readonly Stack<IEditorCommand> _undoStack = [];
    private readonly Stack<IEditorCommand> _redoStack = [];
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
        get => _nodes;
        set => SetProperty(ref _nodes, value);
    }

    /// <summary>
    /// <para>获取或设置节点间的连接线集合。</para>
    /// Gets or sets the collection of connections between nodes.
    /// </summary>
    public ObservableCollection<NodeViewModels.NodeConnectionViewModel> Connections
    {
        get => _connections;
        set
        {
            if (ReferenceEquals(_connections, value)) return;
            _connections.CollectionChanged -= OnConnectionsChanged;
            SetProperty(ref _connections, value);
            _connections.CollectionChanged += OnConnectionsChanged;
            SelectConnection(null);
        }
    }

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
        OnPropertyChanged(nameof(DeleteSelectionText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnConnectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
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
        get => _zoom;
        set => SetProperty(ref _zoom, value);
    }

    /// <summary>
    /// <para>获取或设置是否显示小地图。</para>
    /// Gets or sets whether to show the minimap.
    /// </summary>
    public bool ShowMinimap
    {
        get => _showMinimap;
        set => SetProperty(ref _showMinimap, value);
    }

    /// <summary>
    /// <para>获取或设置节点搜索文本。</para>
    /// Gets or sets the node search text.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // 取消上一次搜索防抖任务
                // Cancel the previous search debounce task
                _searchDebounceCts?.Cancel();
                _searchDebounceCts?.Dispose();

                var cts = new CancellationTokenSource();
                _searchDebounceCts = cts;

                _ = RefreshToolboxAfterDelayAsync(cts.Token);
            }
        }
    }

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
        get => _executionStatus;
        set => SetProperty(ref _executionStatus, value);
    }

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
        get => _visibleToolboxCategories;
        private set => SetProperty(ref _visibleToolboxCategories, value);
    }

    /// <summary>
    /// <para>获取或设置当前选中的节点，绑定到属性面板。</para>
    /// Gets or sets the currently selected node, bound to the properties panel.
    /// </summary>
    public NodeViewModels.FlowNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
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
        get => _executionProgress;
        set => SetProperty(ref _executionProgress, value);
    }

    /// <summary>
    /// <para>获取或设置是否正在执行工作流。</para>
    /// Gets or sets whether a workflow is currently executing.
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        set => SetProperty(ref _isExecuting, value);
    }

    /// <summary>
    /// <para>执行日志面板条目集合（底部面板展示），记录执行引擎、编辑器及节点状态变更。</para>
    /// Execution log panel entries (shown in the bottom panel), recording engine, editor and node status changes.
    /// </summary>
    public ObservableCollection<string> ExecutionLog { get; } = [];

    /// <summary>Installed composite nodes available for isolated graph editing.</summary>
    public ObservableCollection<VisualPluginRegistration> EditableVisualPlugins { get; } = [];

    /// <summary>The composite node selected in the property-pane plugin workspace.</summary>
    public VisualPluginRegistration? SelectedVisualPlugin
    {
        get => _selectedVisualPlugin;
        set => SetProperty(ref _selectedVisualPlugin, value);
    }

    /// <summary>Whether the property pane exposes the plugin workspace.</summary>
    public bool ShowVisualPluginWorkspace { get; }

    /// <summary>
    /// <para>执行期间订阅 LogService 的退订委托。</para>
    /// Unsubscribe delegate for the LogService subscription during execution.
    /// </summary>
    private Action? _executionLogUnsubscribe;

    /// <summary>
    /// <para>执行日志条数上限，超过后移除最旧条目，防止长工作流下集合无限增长。</para>
    /// Execution log entry cap; the oldest entries are removed beyond it to keep the
    /// collection bounded for long-running workflows.
    /// </summary>
    private const int MaxExecutionLogEntries = 1000;

    /// <summary>
    /// <para>执行日志允许的日志来源集合（Engine、NodeEditor 或任一节点 Title）。</para>
    /// Allowed log sources for the execution log (Engine, NodeEditor, or any node Title).
    /// </summary>
    private readonly HashSet<string> _executionLogSources = new(StringComparer.Ordinal);

    /// <summary>
    /// <para>获取是否能执行撤销操作。</para>
    /// Gets whether an undo operation is available.
    /// </summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>
    /// <para>获取是否能执行重做操作。</para>
    /// Gets whether a redo operation is available.
    /// </summary>
    public bool CanRedo => _redoStack.Count > 0;

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
        ShowVisualPluginWorkspace = showVisualPluginWorkspace;
        SelectedNodes = new ReadOnlyObservableCollection<NodeViewModels.FlowNodeViewModel>(_selectedNodes);
        _connections.CollectionChanged += OnConnectionsChanged;
        BatchInputs = new ReadOnlyObservableCollection<NodeViewModels.BatchNodeInputViewModel>(_batchInputs);
        InitializeToolbox();
        PendingConnection = new PendingConnectionViewModel(this);
        RefreshVisibleToolbox();
        WeakReferenceMessenger.Default.Register<NodeEditorActionMessage>(this, OnNodeEditorAction);
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        NodeFactoryRegistrar.DescriptorsChanged += OnDescriptorsChanged;
        VisualPluginManager.Instance.Changed += OnVisualPluginsChanged;
        RefreshEditableVisualPlugins();
    }

    private void OnVisualPluginsChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RefreshEditableVisualPlugins();
        else Avalonia.Threading.Dispatcher.UIThread.Post(RefreshEditableVisualPlugins);
    }

    private void RefreshEditableVisualPlugins()
    {
        var selectedId = SelectedVisualPlugin?.Id;
        EditableVisualPlugins.Clear();
        foreach (var registration in VisualPluginManager.Instance.Registrations.Where(registration =>
                     registration.Kind == VisualPluginKind.CompositeNode && registration.IsAvailable))
        {
            EditableVisualPlugins.Add(registration);
        }

        SelectedVisualPlugin = EditableVisualPlugins.FirstOrDefault(registration =>
            string.Equals(registration.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? EditableVisualPlugins.FirstOrDefault();
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
        PatchChanges = PatchChanges.Select(item => new PatchChangeViewModel(item.Change)).ToArray();
        CompositeExecutions = CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item.Execution)).ToArray();
        OnPropertyChanged(nameof(PatchOutcomeText));
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
        switch (message.Action)
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
    }

    /// <summary>
    /// <para>撤销最近一次编辑操作。</para>
    /// Undoes the most recent edit operation.
    /// </summary>
    public void Undo()
    {
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
        RefreshSelectionProperties();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        _log.Write(LogLevel.Info, "NodeEditor", $"Undo: {command.DisplayName}");
    }

    /// <summary>
    /// <para>重做最近一次被撤销的编辑操作。</para>
    /// Redoes the most recently undone edit operation.
    /// </summary>
    public void Redo()
    {
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
        var selectedSet = selected.ToHashSet();
        var indexByNode = selected.Select((node, index) => (node, index))
            .ToDictionary(item => item.node, item => item.index);
        _clipboard = new NodeClipboard(
            selected.Select(node => new ClipboardNode(
                node.TypeKey,
                node.Title,
                node.Category,
                node.Location,
                node.NodeValueType,
                node.NodeValue,
                node.SelectedImage,
                node.Input
                    .Where(port => port.HasPersistableInitialValue)
                    .ToDictionary(port => port.Title, port => port.InitialValue),
                node.InstanceId)).ToList(),
            Connections
                .Where(connection => connection.Source is not null && connection.Target is not null)
                .Select(connection => (connection, source: selected.FirstOrDefault(node => node.Output.Contains(connection.Source!)), target: selected.FirstOrDefault(node => node.Input.Contains(connection.Target!))))
                .Where(item => item.source is not null && item.target is not null && selectedSet.Contains(item.source) && selectedSet.Contains(item.target))
                .Select(item => new ClipboardConnection(
                    indexByNode[item.source!],
                    SourceTitle: item.connection.Source!.Title,
                    indexByNode[item.target!],
                    TargetTitle: item.connection.Target!.Title))
                .ToList());
        _log.Write(LogLevel.Info, "NodeEditor", $"Copied {selected.Count} node(s)");
    }

    /// <summary>
    /// <para>从剪贴板粘贴节点，复制到原位置偏移。</para>
    /// Pastes nodes from the clipboard, offset from their original positions.
    /// </summary>
    private void PasteFromClipboard()
    {
        if (_clipboard is not { Nodes.Count: > 0 } clipboard) return;
        var offset = new Point(40, 40);
        var pasted = new List<NodeViewModels.FlowNodeViewModel>();
        var pastedIndexByClipboardIndex = new Dictionary<int, int>();
        for (var clipboardIndex = 0; clipboardIndex < clipboard.Nodes.Count; clipboardIndex++)
        {
            var item = clipboard.Nodes[clipboardIndex];
            var node = NodeTypeRegistry.Create(item.TypeKey);
            if (node is null)
            {
                _log.Write(LogLevel.Warn, "NodeEditor", $"Cannot paste unknown node type '{item.TypeKey}'.");
                continue;
            }
            node.Title = item.Title + " (copy)";
            node.Location = item.Location + offset;
            if (item.NodeValueType is not null) node.NodeValueType = item.NodeValueType;
            if (item.NodeValue is not null) node.NodeValue = item.NodeValue;
            if (item.SelectedImage is not null) node.SelectedImage = item.SelectedImage;
            foreach (var (title, value) in item.InitialValues)
            {
                if (node.Input.FirstOrDefault(port => port.Title == title) is { } port)
                    port.InitialValue = value;
            }
            pasted.Add(node);
            pastedIndexByClipboardIndex[clipboardIndex] = pasted.Count - 1;
        }
        if (pasted.Count == 0) return;

        var commands = pasted
            .Select(node => (NodeViewModels.EditorCommands.IEditorCommand)new NodeViewModels.EditorCommands.AddNodeCommand(Nodes, Connections, node))
            .ToList();
        foreach (var connection in clipboard.Connections)
        {
            if (!pastedIndexByClipboardIndex.TryGetValue(connection.SourceIndex, out var sourceIndex)
                || !pastedIndexByClipboardIndex.TryGetValue(connection.TargetIndex, out var targetIndex)) continue;
            var source = pasted[sourceIndex].Output.FirstOrDefault(port => port.Title == connection.SourceTitle);
            var target = pasted[targetIndex].Input.FirstOrDefault(port => port.Title == connection.TargetTitle);
            if (source is not null && target is not null)
                commands.Add(new NodeViewModels.EditorCommands.AddConnectionCommand(Connections, new NodeViewModels.NodeConnectionViewModel(source, target)));
        }
        ExecuteCommand(new NodeViewModels.EditorCommands.CompoundCommand(commands));
        foreach (var node in pasted) AttachNodePropertyTracking(node);
        _nextNodeLocation = new Point(_nextNodeLocation.X + offset.X, _nextNodeLocation.Y + offset.Y);
        _log.Write(LogLevel.Info, "NodeEditor", $"Pasted {pasted.Count} node(s)");
    }

    private NodeClipboard? _clipboard;

    private sealed record NodeClipboard(List<ClipboardNode> Nodes, List<ClipboardConnection> Connections);
    private sealed record ClipboardNode(string TypeKey, string Title, NodeCategory Category, Point Location,
        string? NodeValueType, string? NodeValue, string? SelectedImage, Dictionary<string, object?> InitialValues, string InstanceId);
    private sealed record ClipboardConnection(int SourceIndex, string SourceTitle, int TargetIndex, string TargetTitle);

    private void AttachNodePropertyTracking(NodeViewModels.FlowNodeViewModel node)
    {
        AttachPropertyTracking(node);
        foreach (var connector in node.Input.Concat(node.Output))
        {
            AttachPropertyTracking(connector);
        }
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
    public string SelectedCategoryDisplay => CommonSelectionDisplay(nodes => nodes.Select(node => node.Category.ToString()));

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
        OnPropertyChanged(nameof(SelectedNodeCount));
        OnPropertyChanged(nameof(HasSelectedNodes));
        OnPropertyChanged(nameof(HasSelection));
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
        NodeViewModels.FlowNodeViewModel => ["NodeValueType", "NodeValue", "SelectedImage"],
        NodeViewModels.NodeConnectorViewModel => ["InitialValue"],
        _ => []
    };

    private static object? ReadTrackedValue(object target, string property) => (target, property) switch
    {
        (NodeViewModels.FlowNodeViewModel node, "NodeValueType") => node.NodeValueType,
        (NodeViewModels.FlowNodeViewModel node, "NodeValue") => node.NodeValue,
        (NodeViewModels.FlowNodeViewModel node, "SelectedImage") => node.SelectedImage,
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
            case (NodeViewModels.NodeConnectorViewModel connector, "InitialValue"):
                connector.InitialValue = value;
                break;
        }
    }

    private void OnTrackedPropertyChanged(INotifyPropertyChanged target, string? propertyName)
    {
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

    private void InitializeToolbox()
    {
        ToolboxCategories.Clear();
        NodeFactoryRegistrar.RegisterAll();
        var language = LanguageService.Instance;
        foreach (var group in Enum.GetValues<NodeToolboxGroup>())
        {
            var descriptors = NodeFactoryRegistrar.Descriptors.Where(d => d.Group == group).ToList();
            if (descriptors.Count == 0) continue;
            ToolboxCategories.Add(new NodeToolboxCategory(
                language.GetString(GetGroupResourceKey(group)),
                GetNodeCategory(group),
                new ObservableCollection<NodeToolboxItem>(descriptors.Select(d => new NodeToolboxItem(d, language)))));
        }
    }

    private static string GetGroupResourceKey(NodeToolboxGroup group) => group switch
    {
        NodeToolboxGroup.FirmwareContainer => "Node_CatFirmwareContainer",
        NodeToolboxGroup.FirmwareImage => "Node_CatFirmwareImage",
        NodeToolboxGroup.Android => "Node_CatAndroid",
        NodeToolboxGroup.Delivery => "Node_CatDelivery",
        NodeToolboxGroup.Flash => "Node_CatFlash",
        NodeToolboxGroup.Data => "Node_CatData",
        NodeToolboxGroup.Network => "Node_CatNetwork",
        NodeToolboxGroup.Flow => "Node_CatFlow",
        NodeToolboxGroup.Output => "Node_CatOutput",
        NodeToolboxGroup.Patch => "Node_CatPatch",
        NodeToolboxGroup.VisualPlugins => "Node_CatVisualPlugins",
        _ => "Node_CatFirmware"
    };

    private static NodeViewModels.NodeCategory GetNodeCategory(NodeToolboxGroup group) => group switch
    {
        NodeToolboxGroup.Flash => NodeViewModels.NodeCategory.Flash,
        NodeToolboxGroup.Data => NodeViewModels.NodeCategory.Data,
        NodeToolboxGroup.Network => NodeViewModels.NodeCategory.Data,
        NodeToolboxGroup.Patch => NodeViewModels.NodeCategory.Data,
        NodeToolboxGroup.Flow => NodeViewModels.NodeCategory.Flow,
        NodeToolboxGroup.Output => NodeViewModels.NodeCategory.Output,
        NodeToolboxGroup.VisualPlugins => NodeViewModels.NodeCategory.Plugin,
        _ => NodeViewModels.NodeCategory.Firmware
    };

    /// <summary>
    /// <para>将指定节点添加到画布。</para>
    /// Adds the specified node to the canvas.
    /// </summary>
    /// <param name="node">
    /// <para>要添加的节点。</para>
    /// The node to add.
    /// </param>
    public void AddNode(NodeViewModels.FlowNodeViewModel node)
    {
        if (node.Location == DefaultNodeLocation)
        {
            node.Location = _nextNodeLocation;
            _nextNodeLocation = new Point(_nextNodeLocation.X + 40, _nextNodeLocation.Y + 40);
        }

        ExecuteCommand(new NodeViewModels.EditorCommands.AddNodeCommand(Nodes, Connections, node));
        AttachNodePropertyTracking(node);
    }

    /// <summary>
    /// <para>从画布移除指定节点（可撤销）。</para>
    /// Removes the specified node from the canvas (undoable).
    /// </summary>
    /// <param name="node">
    /// <para>要移除的节点。</para>
    /// The node to remove.
    /// </param>
    public void RemoveNode(NodeViewModels.FlowNodeViewModel node)
    {
        ExecuteCommand(new NodeViewModels.EditorCommands.RemoveNodeCommand(Nodes, Connections, node));
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
        if (source == target) return;
        if (source.IsInput == target.IsInput)
        {
            _log.Write(LogLevel.Warn, "NodeEditor", "Connection rejected: connectors have the same direction");
            return;
        }
        if (source.IsInput)
        {
            (source, target) = (target, source);
        }
        // 检查是否已存在相同连接
        if (Connections.Any(c => c.Source == source && c.Target == target)) return;
        // 端口类型兼容性校验：不兼容则拒绝连接并记录警告
        if (!NodeDataUtil.CanConnect(source.PortType, target.PortType))
        {
            _log.Write(LogLevel.Warn, "NodeEditor", $"Connection rejected: type {source.PortType} -> {target.PortType} incompatible");
            return;
        }
        ExecuteCommand(new NodeViewModels.EditorCommands.AddConnectionCommand(Connections, new NodeViewModels.NodeConnectionViewModel(source, target)));
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
    }

    [RelayCommand]
    private void ToggleMinimap()
    {
        ShowMinimap = !ShowMinimap;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
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
        // 删除当前选中的节点及其相关连接，作为单个可撤销命令整批执行
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
    [RelayCommand]
    private Task RunAsync() => ExecuteGraphAsync(false);

    /// <summary>Evaluates supported patch graphs without publishing staged files.</summary>
    [RelayCommand]
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
        PatchChanges = [];
        CompositeExecutions = [];
        CompositeReportTruncated = false;
        _patchOutcome = PatchRunOutcome.None;
        OnPropertyChanged(nameof(PatchOutcomeText));
        OnPropertyChanged(nameof(HasPatchReview));
        ExecutionStatus = LanguageService.Instance.GetString("Node_Run");
        ExecutionProgress = 0.0;
        _runCts = new CancellationTokenSource();

        // 清空执行日志并订阅相关日志源（Engine、NodeEditor 或任一节点 Title）
        // Clear the execution log and subscribe to relevant log sources (Engine, NodeEditor, or any node Title).
        ExecutionLog.Clear();
        _executionLogSources.Clear();
        _executionLogSources.Add("Engine");
        _executionLogSources.Add("NodeEditor");
        foreach (var n in Nodes)
        {
            if (!string.IsNullOrEmpty(n.Title)) _executionLogSources.Add(n.Title);
        }
        _executionLogUnsubscribe = _log.Subscribe(OnExecutionLogEnqueued);

        try
        {
            _engine ??= new NodeExecutionEngine(_log);
            _engine.ProgressChanged += OnExecutionProgress;
            _engine.NodeStatusChanged += OnNodeStatusChanged;
            _engine.ExecutionCompleted += OnExecutionCompleted;

            var nodes = Nodes.ToList();
            var connections = Connections
                .Where(c => c.Source != null && c.Target != null)
                .Select(c => new NodeConnection
                {
                    SourceInstanceId = FindOwnerInstanceId(c.Source!) ?? string.Empty,
                    TargetInstanceId = FindOwnerInstanceId(c.Target!) ?? string.Empty,
                    SourceConnector = c.Source!.Title,
                    TargetConnector = c.Target!.Title
                })
                .Where(c => !string.IsNullOrEmpty(c.SourceInstanceId) && !string.IsNullOrEmpty(c.TargetInstanceId))
                .ToList();

            ExecutionResult? result = null;
            await ProjectOperationsService.Instance.ExecuteFileOperationAsync(async () =>
            {
                result = await _engine.ExecuteAsync(nodes, connections, _runCts.Token,
                    new NodeExecutionOptions { Preview = preview, Package = ResourcePackage, ValidatePackage = ValidatePackage }).ConfigureAwait(false);
            }, _runCts.Token).ConfigureAwait(false);
            if (result is null) { throw new InvalidOperationException("Workflow returned no execution result."); }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                PatchChanges = result.PatchChanges.Select(change => new PatchChangeViewModel(change)).ToArray();
                CompositeExecutions = result.CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item)).ToArray();
                CompositeReportTruncated = result.CompositeReportTruncated;
                _patchOutcome = result.PatchOutcome;
                OnPropertyChanged(nameof(PatchOutcomeText));
                OnPropertyChanged(nameof(HasPatchReview));
            });

            ExecutionStatus = result.Status switch
            {
                Services.Execution.ExecutionStatus.Succeeded => LanguageService.Instance.GetString(preview ? "Node_Patch_PreviewFinished" : "Node_Patch_RunFinished"),
                Services.Execution.ExecutionStatus.PartiallySucceeded => LanguageService.Instance.GetString("Node_Patch_RunPartial", result.FailedNodes.Count),
                Services.Execution.ExecutionStatus.Cancelled => LanguageService.Instance.GetString("Node_Patch_RunCancelled"),
                Services.Execution.ExecutionStatus.Failed => result.ErrorMessage ?? LanguageService.Instance.GetString("Node_Patch_RunFailed"),
                _ => LanguageService.Instance.GetString("Node_StatusReady")
            };
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "NodeEditor", $"Run failed: {ex.Message}");
            ExecutionStatus = $"Run failed: {ex.Message}";
        }
        finally
        {
            // 退订执行日志，避免跨次运行的重复收集
            // Unsubscribe from the execution log to avoid duplicate collection across runs.
            _executionLogUnsubscribe?.Invoke();
            _executionLogUnsubscribe = null;

            IsExecuting = false;
            if (_engine != null)
            {
                _engine.ProgressChanged -= OnExecutionProgress;
                _engine.NodeStatusChanged -= OnNodeStatusChanged;
                _engine.ExecutionCompleted -= OnExecutionCompleted;
            }
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// <para>停止正在执行的节点工作流。</para>
    /// Stops the currently executing node workflow.
    /// </summary>
    [RelayCommand]
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

    private void OnExecutionProgress(object? sender, double progress)
    {
        ExecutionProgress = progress;
    }

    /// <summary>
    /// <para>向执行日志追加一行并裁剪超限条目（保持集合有界）。</para>
    /// Appends one line to the execution log and trims over-limit entries (bounded collection).
    /// </summary>
    private void AppendExecutionLog(string line)
    {
        ExecutionLog.Add(line);
        var overflow = ExecutionLog.Count - MaxExecutionLogEntries;
        for (var i = 0; i < overflow; i++)
        {
            ExecutionLog.RemoveAt(0);
        }
    }

    private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs e)
    {
        // 节点状态变更：向执行日志写一行 “<Title> -> <状态>”，并记录一条调试日志
        // Node status changed: append “<Title> -> <status>” to the execution log and write a debug log entry.
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
    private void RefreshVisibleToolbox()
    {
        var keyword = (SearchText ?? string.Empty).Trim();

        VisibleToolboxCategories.Clear();

        if (string.IsNullOrEmpty(keyword))
        {
            foreach (var cat in ToolboxCategories)
            {
                VisibleToolboxCategories.Add(cat);
            }
            return;
        }

        foreach (var cat in ToolboxCategories)
        {
            var matchedItems = cat.Items
                .Where(i => i.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (matchedItems.Count > 0)
            {
                VisibleToolboxCategories.Add(new NodeToolboxCategory(cat.Name, cat.Category, new ObservableCollection<NodeToolboxItem>(matchedItems)));
            }
        }
    }

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
        ArgumentNullException.ThrowIfNull(snapshot);
        Clear();

        // 重建节点：按 TypeKey 查注册表创建具体节点；未注册的类型跳过并告警（软件未发布，无旧工程兼容需求）
        // Rebuild nodes: create concrete nodes via the registry by TypeKey; skip and warn on unregistered types
        // (no released builds exist, so there are no legacy projects to keep loading).
        var created = new System.Collections.Generic.Dictionary<string, NodeViewModels.FlowNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in snapshot.Nodes)
        {
            if (!System.Enum.TryParse<NodeViewModels.NodeCategory>(dto.Category, true, out var category))
            {
                category = NodeViewModels.NodeCategory.Firmware;
            }
            if (!Services.Projects.WorkflowReconstruction.TryCreateNode(dto, out var node, preserveUnknown: true,
                    restoreInstanceId: true))
            {
                _log.Write(LogLevel.Warn, "NodeEditor", $"Unknown node type '{dto.TypeKey}' skipped while loading workflow.");
                continue;
            }
            node.Location = new Point(dto.X, dto.Y);
            // 恢复输入端口初始值：标题匹配输入端口，值为字符串；数字/布尔按原类型宽松转换。
            // Restore input initial values: match input ports by title; the value is a string, leniently converted for numbers/booleans.
            Services.Projects.WorkflowReconstruction.RestoreInitialValues(node, dto.InputInitialValues,
                dto.InputInitialValuesByPortId);
            Nodes.Add(node); // 加载操作直接写入画布，不进入撤销栈
            AttachNodePropertyTracking(node);
            if (!string.IsNullOrEmpty(dto.InstanceId)) created[dto.InstanceId] = node;
        }

        // 重建连接：优先按持久化的源/目标实例 ID 精确匹配（新快照）；缺失时回退为按连接器标题匹配
        //（简易匹配，仅当标题唯一时成功）。
        // Rebuild connections: prefer exact matching via persisted source/target instance IDs (new snapshots);
        // fall back to connector-title matching when absent (lenient matching, succeeds only when titles are unique).
        foreach (var cdto in snapshot.Connections)
        {
            var (_, src) = Services.Projects.WorkflowReconstruction.ResolveEndpoint(Nodes, created,
                cdto.SourceInstanceId, cdto.SourceConnectorTitle, output: true, fallbackOnMissingPort: true,
                cdto.SourcePortId);
            var (_, tgt) = Services.Projects.WorkflowReconstruction.ResolveEndpoint(Nodes, created,
                cdto.TargetInstanceId, cdto.TargetConnectorTitle, output: false, fallbackOnMissingPort: true,
                cdto.TargetPortId);
            if (src != null && tgt != null)
            {
                Connections.Add(new NodeViewModels.NodeConnectionViewModel(src, tgt));
            }
            else
            {
                _log.Write(LogLevel.Warn, "NodeEditor",
                    $"Could not rebuild connection: '{cdto.SourceConnectorTitle}' → '{cdto.TargetConnectorTitle}'");
            }
        }

        _log.Write(LogLevel.Info, "NodeEditor",
            $"Loaded {Nodes.Count} node(s), {Connections.Count} connection(s) from snapshot");
        return Task.CompletedTask;
    }

    /// <summary>Appends a portable workflow template as one undoable canvas edit.</summary>
    public void ImportTemplate(Services.Projects.VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Kind != Services.Projects.VisualPluginKind.WorkflowTemplate)
            throw new InvalidDataException("Only workflow-template packages can be imported into the canvas.");

        var graph = package.Graph;
        var created = new Dictionary<string, NodeViewModels.FlowNodeViewModel>(StringComparer.Ordinal);
        var imported = new List<NodeViewModels.FlowNodeViewModel>();
        var minimumX = graph.Nodes.Count == 0 ? 0 : graph.Nodes.Min(node => node.X);
        var minimumY = graph.Nodes.Count == 0 ? 0 : graph.Nodes.Min(node => node.Y);
        var offset = new Point(_nextNodeLocation.X - minimumX, _nextNodeLocation.Y - minimumY);

        foreach (var dto in graph.Nodes)
        {
            if (!Services.Projects.WorkflowReconstruction.TryCreateNode(dto, out var node, preserveUnknown: true))
                continue;
            Services.Projects.WorkflowReconstruction.RestoreInitialValues(node, dto.InputInitialValues,
                dto.InputInitialValuesByPortId);
            node.Location = new Point(dto.X + offset.X, dto.Y + offset.Y);
            imported.Add(node);
            if (!string.IsNullOrWhiteSpace(dto.InstanceId)) created[dto.InstanceId] = node;
        }
        if (imported.Count == 0) throw new InvalidDataException("The workflow template contains no importable nodes.");

        var commands = imported.Select(node => (IEditorCommand)new AddNodeCommand(Nodes, Connections, node)).ToList();
        foreach (var connection in graph.Connections)
        {
            var (_, source) = Services.Projects.WorkflowReconstruction.ResolveEndpoint(imported, created,
                connection.SourceInstanceId, connection.SourceConnectorTitle, true, false, connection.SourcePortId);
            var (_, target) = Services.Projects.WorkflowReconstruction.ResolveEndpoint(imported, created,
                connection.TargetInstanceId, connection.TargetConnectorTitle, false, false, connection.TargetPortId);
            if (source is not null && target is not null)
                commands.Add(new AddConnectionCommand(Connections, new NodeConnectionViewModel(source, target)));
        }

        ExecuteCommand(new CompoundCommand(commands));
        foreach (var node in imported) AttachNodePropertyTracking(node);
        SetSelection(imported);
        _nextNodeLocation += new Point(40, 40);
        _log.Write(LogLevel.Info, "NodeEditor", $"Imported template '{package.Manifest.Name}' with {imported.Count} node(s).");
    }

    /// <summary>Captures the current canvas as a portable visual-plugin package.</summary>
    public Services.Projects.VisualPluginPackage CreateVisualPluginPackage(
        string name, string id, Services.Projects.VisualPluginKind kind, string author = "")
        => Services.VisualPlugins.VisualPluginPackageBuilder.Create(name, id, kind, Nodes, Connections, author);

    public void Dispose()
    {
        _connections.CollectionChanged -= OnConnectionsChanged;
        DetachAllPropertyTracking();
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        NodeFactoryRegistrar.DescriptorsChanged -= OnDescriptorsChanged;
        VisualPluginManager.Instance.Changed -= OnVisualPluginsChanged;
        WeakReferenceMessenger.Default.Unregister<NodeEditorActionMessage>(this);
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _executionLogUnsubscribe?.Invoke();
        _executionLogUnsubscribe = null;
    }
}

/// <summary>
/// <para>节点工具箱分类，包含同类别的节点项列表。</para>
/// Node toolbox category, containing a list of node items in the same category.
/// </summary>
public class NodeToolboxCategory
{
    /// <summary>
    /// <para>获取分类名称。</para>
    /// Gets the category name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>获取节点类别。</para>
    /// Gets the node category.
    /// </summary>
    public NodeViewModels.NodeCategory Category { get; }

    /// <summary>
    /// <para>获取分类下的节点项列表。</para>
    /// Gets the list of node items in this category.
    /// </summary>
    public ObservableCollection<NodeToolboxItem> Items { get; }

    /// <summary>
    /// <para>创建工具箱分类。</para>
    /// Creates a toolbox category.
    /// </summary>
    /// <param name="name">
    /// <para>分类名称。</para>
    /// The category name.
    /// </param>
    /// <param name="category">
    /// <para>节点类别。</para>
    /// The node category.
    /// </param>
    /// <param name="items">
    /// <para>分类下的节点项列表。</para>
    /// The list of node items in this category.
    /// </param>
    public NodeToolboxCategory(string name, NodeViewModels.NodeCategory category, ObservableCollection<NodeToolboxItem> items)
    {
        Name = name;
        Category = category;
        Items = items;
    }
}

/// <summary>
/// <para>节点工具箱项，表示工具箱中可添加到画布的节点模板。</para>
/// Node toolbox item, representing a node template in the toolbox that can be added to the canvas.
/// </summary>
public class NodeToolboxItem
{
    /// <summary>
    /// <para>获取节点项名称。</para>
    /// Gets the node item name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>获取创建节点实例的工厂方法。</para>
    /// Gets the factory method to create a node instance.
    /// </summary>
    public Func<NodeViewModels.FlowNodeViewModel> CreateNode { get; }

    public string TypeKey { get; }

    public bool IsDestructive { get; }

    public NodeCapability Capabilities { get; }


    /// <summary>
    /// <para>创建工具箱项。</para>
    /// Creates a toolbox item.
    /// </summary>
    /// <param name="name">
    /// <para>节点项名称。</para>
    /// The node item name.
    /// </param>
    /// <param name="createNode">
    /// <para>创建节点实例的工厂方法。</para>
    /// The factory method to create a node instance.
    /// </param>
    public NodeToolboxItem(string name, Func<NodeViewModels.FlowNodeViewModel> createNode)
    {
        Name = name;
        CreateNode = createNode;
        TypeKey = string.Empty;
    }

    public NodeToolboxItem(NodeDescriptor descriptor, LanguageService language)
    {
        Name = descriptor.DisplayName ?? language.GetString(descriptor.NameResourceKey);
        CreateNode = descriptor.Factory;
        TypeKey = descriptor.TypeKey;
        IsDestructive = descriptor.IsDestructive;
        Capabilities = descriptor.Capabilities;
    }
}