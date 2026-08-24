using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>节点编辑器主视图模型，管理节点画布、连接线、工具箱、选择状态与工作流执行。</para>
    /// Main view model for the node editor, managing the node canvas, connections, toolbox, selection state, and workflow execution.
    /// </summary>
    public partial class NodeEditorViewModel : ViewModelBase
    {
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
        private double _executionProgress;
        private bool _isExecuting;

        // ---- 撤销/重做命令栈 ----
        private readonly Stack<IEditorCommand> _undoStack = [];
        private readonly Stack<IEditorCommand> _redoStack = [];
        private const int MaxUndoDepth = 100;

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
            set => SetProperty(ref _connections, value);
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
            set => SetProperty(ref _searchText, value);
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
            set => SetProperty(ref _selectedNode, value);
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
        public NodeEditorViewModel()
        {
            InitializeToolbox();
            PendingConnection = new PendingConnectionViewModel(this);
            RefreshVisibleToolbox();
            WeakReferenceMessenger.Default.Register<NodeEditorActionMessage>(this, OnNodeEditorAction);
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
                    foreach (var n in Nodes) n.IsSelected = true;
                    if (Nodes.Count > 0) SelectedNode = Nodes.First(n => n.IsSelected);
                    break;
                case NodeEditorAction.Copy:
                    CopySelectedToClipboard();
                    break;
                case NodeEditorAction.Cut:
                    CopySelectedToClipboard();
                    DeleteSelected();
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
                _undoStack.Pop();
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
            command.Undo();
            _redoStack.Push(command);
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
            command.Execute();
            _undoStack.Push(command);
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
            _clipboardNodeTitles = selected.Select(n => n.Title).ToList();
            _log.Write(LogLevel.Info, "NodeEditor", $"Copied {selected.Count} node(s)");
        }

        /// <summary>
        /// <para>从剪贴板粘贴节点，复制到原位置偏移。</para>
        /// Pastes nodes from the clipboard, offset from their original positions.
        /// </summary>
        private void PasteFromClipboard()
        {
            if (_clipboardNodeTitles == null || _clipboardNodeTitles.Count == 0) return;
            var offset = new Point(40, 40);
            foreach (var title in _clipboardNodeTitles)
            {
                var node = new NodeViewModels.FlowNodeViewModel(title + " (copy)", NodeViewModels.NodeCategory.Firmware)
                {
                    Location = _nextNodeLocation
                };
                AddNode(node);
                _nextNodeLocation = new Point(_nextNodeLocation.X + offset.X, _nextNodeLocation.Y + offset.Y);
            }
            _log.Write(LogLevel.Info, "NodeEditor", $"Pasted {_clipboardNodeTitles.Count} node(s)");
        }

        private List<string>? _clipboardNodeTitles;

        private void InitializeToolbox()
        {
            var svc = LanguageService.Instance;
            ToolboxCategories.Add(new NodeToolboxCategory(
                svc.GetString("Node_CatFirmware"),
                NodeViewModels.NodeCategory.Firmware,
                [
                    new NodeToolboxItem(svc.GetString("Node_Firmware_Unpack"), () => NodeViewModels.FirmwareNodeViewModel.CreateUnpackNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_Pack"), () => NodeViewModels.FirmwareNodeViewModel.CreatePackNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_Verify"), () => NodeViewModels.FirmwareNodeViewModel.CreateVerifyNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_Extract"), () => NodeViewModels.FirmwareNodeViewModel.CreateExtractNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_OtaDelta"), () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateOtaDeltaNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_MergeSignature"), () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateMergeSignatureNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_MergeDualSlot"), () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateMergeDualSlotNode()),
                    new NodeToolboxItem(svc.GetString("Node_Firmware_FlashBoot"), () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateFlashBootNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                svc.GetString("Node_CatFlash"),
                NodeViewModels.NodeCategory.Flash,
                [
                    new NodeToolboxItem(svc.GetString("Node_Flash_Connect"), () => NodeViewModels.FlashNodeViewModel.CreateConnectNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_Flash"), () => NodeViewModels.FlashNodeViewModel.CreateFlashNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_Erase"), () => NodeViewModels.FlashNodeViewModel.CreateEraseNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_Reboot"), () => NodeViewModels.FlashNodeViewModel.CreateRebootNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_Bootloader"), () => NodeViewModels.ExtendedFlashNodeViewModel.CreateFlashBootloaderNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_EraseAll"), () => NodeViewModels.ExtendedFlashNodeViewModel.CreateEraseAllNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_OtaSideload"), () => NodeViewModels.ExtendedFlashNodeViewModel.CreateOtaSideloadNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flash_OverwriteFirmware"), () => NodeViewModels.ExtendedFlashNodeViewModel.CreateOverwriteFirmwareNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                svc.GetString("Node_CatData"),
                NodeViewModels.NodeCategory.Data,
                [
                    new NodeToolboxItem(svc.GetString("Node_Data_ReadFile"), () => NodeViewModels.DataNodeViewModel.CreateFileReadNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Convert"), () => NodeViewModels.DataNodeViewModel.CreateConvertNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Checksum"), () => NodeViewModels.DataNodeViewModel.CreateChecksumNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Compare"), () => NodeViewModels.DataNodeViewModel.CreateCompareNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Concat"), () => NodeViewModels.ExtendedDataNodeViewModel.CreateConcatNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Slice"), () => NodeViewModels.ExtendedDataNodeViewModel.CreateSliceNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Encrypt"), () => NodeViewModels.ExtendedDataNodeViewModel.CreateEncryptNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_Compress"), () => NodeViewModels.ExtendedDataNodeViewModel.CreateCompressNode()),
                    new NodeToolboxItem(svc.GetString("Node_Data_ByteSwap"), () => NodeViewModels.ExtendedDataNodeViewModel.CreateByteSwapNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                svc.GetString("Node_CatFlow"),
                NodeViewModels.NodeCategory.Flow,
                [
                    new NodeToolboxItem(svc.GetString("Node_Flow_Start"), () => NodeViewModels.FlowControlNodeViewModel.CreateStartNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_End"), () => NodeViewModels.FlowControlNodeViewModel.CreateEndNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Branch"), () => NodeViewModels.FlowControlNodeViewModel.CreateBranchNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Loop"), () => NodeViewModels.FlowControlNodeViewModel.CreateLoopNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Delay"), () => NodeViewModels.FlowControlNodeViewModel.CreateDelayNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Parallel"), () => NodeViewModels.FlowControlNodeViewModel.CreateParallelNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Sequence"), () => NodeViewModels.FlowControlNodeViewModel.CreateSequenceNode()),
                    new NodeToolboxItem(svc.GetString("Node_Flow_Terminate"), () => NodeViewModels.FlowControlNodeViewModel.CreateTerminateNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                svc.GetString("Node_CatOutput"),
                NodeViewModels.NodeCategory.Output,
                [
                    new NodeToolboxItem(svc.GetString("Node_Output_Log"), () => NodeViewModels.OutputNodeViewModel.CreateLogNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_Progress"), () => NodeViewModels.OutputNodeViewModel.CreateProgressNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_Save"), () => NodeViewModels.OutputNodeViewModel.CreateSaveNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_Notify"), () => NodeViewModels.ExtendedOutputNodeViewModel.CreateNotifyNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_Email"), () => NodeViewModels.ExtendedOutputNodeViewModel.CreateSendEmailNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_Console"), () => NodeViewModels.ExtendedOutputNodeViewModel.CreateConsoleOutputNode()),
                    new NodeToolboxItem(svc.GetString("Node_Output_FileExport"), () => NodeViewModels.ExtendedOutputNodeViewModel.CreateFileExportNode()),
                ]
            ));
        }

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
            // 检查是否已存在相同连接
            if (Connections.Any(c => c.Source == source && c.Target == target)) return;
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

        [RelayCommand]
        private void DeleteSelected()
        {
            // 删除当前选中的节点及其相关连接，作为单个可撤销命令整批执行
            var toDelete = Nodes.Where(n => n.IsSelected).ToList();
            if (toDelete.Count == 0) return;

            var commands = toDelete
                .Select(n => (NodeViewModels.EditorCommands.IEditorCommand)new NodeViewModels.EditorCommands.RemoveNodeCommand(Nodes, Connections, n))
                .ToList();

            ExecuteCommand(new NodeViewModels.EditorCommands.CompoundCommand(commands));
            SelectedNode = null;
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
            foreach (var n in Nodes) n.IsSelected = false;
            if (node != null) node.IsSelected = true;
            SelectedNode = node;
        }

        /// <summary>
        /// <para>异步执行节点工作流。基于拓扑排序依次执行节点，并将上游输出注入下游输入。</para>
        /// Asynchronously executes the node workflow. Nodes are executed in topological order; upstream outputs are injected into downstream inputs.
        /// </summary>
        [RelayCommand]
        private async Task RunAsync()
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
            ExecutionStatus = LanguageService.Instance.GetString("Node_Run");
            ExecutionProgress = 0.0;
            _runCts = new CancellationTokenSource();

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

                var result = await _engine.ExecuteAsync(nodes, connections, _runCts.Token).ConfigureAwait(false);

                ExecutionStatus = result.Status switch
                {
                    Services.Execution.ExecutionStatus.Succeeded => "Workflow completed",
                    Services.Execution.ExecutionStatus.PartiallySucceeded => $"Completed with {result.FailedNodes.Count} failures",
                    Services.Execution.ExecutionStatus.Cancelled => "Workflow cancelled",
                    Services.Execution.ExecutionStatus.Failed => result.ErrorMessage ?? "Workflow failed",
                    _ => "Unknown"
                };
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, "NodeEditor", $"Run failed: {ex.Message}");
                ExecutionStatus = $"Run failed: {ex.Message}";
            }
            finally
            {
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
            Connections.Clear();
            Nodes.Clear();
            SelectedNode = null;
            ExecutionProgress = 0.0;
            ExecutionStatus = LanguageService.Instance.GetString("Node_StatusReady");
        }

        private void OnExecutionProgress(object? sender, double progress)
        {
            ExecutionProgress = progress;
        }

        private void OnNodeStatusChanged(object? sender, NodeStatusChangedEventArgs e)
        {
            // 节点状态变更由引擎触发；此处可更新节点视觉状态
            // FlowNodeViewModel 尚未内置 Status 字段，后续可扩展
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
            if (string.IsNullOrEmpty(keyword))
            {
                VisibleToolboxCategories = new ObservableCollection<NodeToolboxCategory>(ToolboxCategories);
                return;
            }

            var filtered = new ObservableCollection<NodeToolboxCategory>();
            foreach (var cat in ToolboxCategories)
            {
                var matchedItems = cat.Items
                    .Where(i => i.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (matchedItems.Count > 0)
                {
                    filtered.Add(new NodeToolboxCategory(cat.Name, cat.Category, new ObservableCollection<NodeToolboxItem>(matchedItems)));
                }
            }
            VisibleToolboxCategories = filtered;
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
        public async Task LoadFromAsync(Services.WorkflowSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Clear();

            // 重建节点：按标题匹配已知类别的简易工厂
            var created = new System.Collections.Generic.Dictionary<string, NodeViewModels.FlowNodeViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var dto in snapshot.Nodes)
            {
                if (!System.Enum.TryParse<NodeViewModels.NodeCategory>(dto.Category, true, out var category))
                {
                    category = NodeViewModels.NodeCategory.Firmware;
                }
                var node = new NodeViewModels.FlowNodeViewModel(dto.Title, category)
                {
                    Location = new Point(dto.X, dto.Y)
                };
                Nodes.Add(node); // 加载操作直接写入画布，不进入撤销栈
                if (!string.IsNullOrEmpty(dto.InstanceId)) created[dto.InstanceId] = node;
            }

            // 重建连接：按连接器标题匹配源/目标节点上的端口（简易匹配，仅当标题唯一时成功）
            foreach (var cdto in snapshot.Connections)
            {
                NodeViewModels.NodeConnectorViewModel? src = null, tgt = null;
                foreach (var n in Nodes)
                {
                    foreach (var oc in n.Output) { if (oc.Title == cdto.SourceConnectorTitle) { src = oc; break; } }
                    if (src != null) break;
                }
                foreach (var n in Nodes)
                {
                    foreach (var ic in n.Input) { if (ic.Title == cdto.TargetConnectorTitle) { tgt = ic; break; } }
                    if (tgt != null) break;
                }
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

            await Task.CompletedTask.ConfigureAwait(false);
            _log.Write(LogLevel.Info, "NodeEditor",
                $"Loaded {Nodes.Count} node(s), {Connections.Count} connection(s) from snapshot");
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
        }
    }
}