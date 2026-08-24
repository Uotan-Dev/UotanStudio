using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Areas.NodeEditor.ViewModels;
using UotanStudio.Core.Flow;
using UotanStudio.Core.Projects;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.NodeEditor;

/// <summary>
/// <para>节点编辑器主视图模型，管理节点画布、连接线、工具箱和执行状态。</para>
/// Main view model for the node editor, managing the node canvas, connections, toolbox, and execution state.
/// </summary>
public partial class NodeEditorViewModel : ViewModelBase
{
    private Point _nextNodeLocation = new(100, 100);
    private static readonly Point DefaultNodeLocation = new(100, 100);
    private NodeConnectorViewModel? _pendingSource;

    /// <summary>
    /// <para>获取或设置画布上的节点集合。</para>
    /// Gets or sets the collection of nodes on the canvas.
    /// </summary>
    public ObservableCollection<FlowNodeViewModel> Nodes
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>
    /// <para>获取或设置节点间的连接线集合。</para>
    /// Gets or sets the collection of connections between nodes.
    /// </summary>
    public ObservableCollection<NodeConnectionViewModel> Connections
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>
    /// <para>获取节点执行日志（每次执行的节点结果与错误记录）。</para>
    /// Gets the node execution log (per-run node results and error records).
    /// </summary>
    public ObservableCollection<string> ExecutionLog { get; } = [];

    private const int MaxExecutionLogEntries = 500;
    private const string FlowExtension = ".uotflow";

    private void AddExecutionLog(string entry)
    {
        ExecutionLog.Add(entry);
        while (ExecutionLog.Count > MaxExecutionLogEntries)
        {
            ExecutionLog.RemoveAt(0);
        }
    }

    /// <summary>
    /// <para>获取或设置画布的缩放级别。</para>
    /// Gets or sets the zoom level of the canvas.
    /// </summary>
    public double Zoom
    {
        get;
        set => SetProperty(ref field, value);
    } = 1.0;

    /// <summary>
    /// <para>获取或设置是否显示小地图。</para>
    /// Gets or sets whether to show the minimap.
    /// </summary>
    public bool ShowMinimap
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>
    /// <para>获取或设置节点搜索文本。</para>
    /// Gets or sets the node search text.
    /// </summary>
    public string SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                FilterToolbox();
            }
        }
    } = string.Empty;

    /// <summary>
    /// <para>根据搜索文本过滤工具箱中的节点项。</para>
    /// Filters the node items in the toolbox based on the search text.
    /// </summary>
    private void FilterToolbox()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;
        foreach (var category in ToolboxCategories)
        {
            foreach (var item in category.Items)
            {
                item.IsVisible = string.IsNullOrEmpty(keyword)
                    || item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// <para>获取或设置执行状态文本。</para>
    /// Gets or sets the execution status text.
    /// </summary>
    public string ExecutionStatus
    {
        get;
        set => SetProperty(ref field, value);
    } = LanguageService.Instance.GetString("Node_StatusReady");

    /// <summary>
    /// <para>获取节点工具箱分类列表。</para>
    /// Gets the list of node toolbox categories.
    /// </summary>
    public ObservableCollection<NodeToolboxCategory> ToolboxCategories { get; } = [];

    /// <summary>
    /// <para>获取或设置当前选中的节点。为 null 时表示无选中。</para>
    /// Gets or sets the currently selected node. Null means no selection.
    /// </summary>
    public FlowNodeViewModel? SelectedNode
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>初始化节点编辑器视图模型，创建默认工具箱分类和示例节点。</para>
    /// Initializes the node editor view model, creating default toolbox categories and example nodes.
    /// </summary>
    public NodeEditorViewModel()
    {
        InitializeToolbox();
    }

    private void InitializeToolbox()
    {
        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatFirmware"),
            NodeCategory.Firmware,
            [
                new NodeToolboxItem(NodeKeys.FirmwareUnpack, FirmwareNodeViewModel.CreateUnpackNode),
                new NodeToolboxItem(NodeKeys.FirmwarePack, FirmwareNodeViewModel.CreatePackNode),
                new NodeToolboxItem(NodeKeys.FirmwareVerify, FirmwareNodeViewModel.CreateVerifyNode),
                new NodeToolboxItem(NodeKeys.FirmwareExtract, FirmwareNodeViewModel.CreateExtractNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatFlash"),
            NodeCategory.Flash,
            [
                new NodeToolboxItem(NodeKeys.FlashConnect, FlashNodeViewModel.CreateConnectNode),
                new NodeToolboxItem(NodeKeys.FlashPartition, FlashNodeViewModel.CreateFlashNode),
                new NodeToolboxItem(NodeKeys.FlashErase, FlashNodeViewModel.CreateEraseNode),
                new NodeToolboxItem(NodeKeys.FlashReboot, FlashNodeViewModel.CreateRebootNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatData"),
            NodeCategory.Data,
            [
                new NodeToolboxItem(NodeKeys.DataReadFile, DataNodeViewModel.CreateFileReadNode),
                new NodeToolboxItem(NodeKeys.DataConvert, DataNodeViewModel.CreateConvertNode),
                new NodeToolboxItem(NodeKeys.DataChecksum, DataNodeViewModel.CreateChecksumNode),
                new NodeToolboxItem(NodeKeys.DataCompare, DataNodeViewModel.CreateCompareNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatFlow"),
            NodeCategory.Flow,
            [
                new NodeToolboxItem(NodeKeys.FlowIf, FlowControlNodeViewModel.CreateIfNode),
                new NodeToolboxItem(NodeKeys.FlowLoop, FlowControlNodeViewModel.CreateLoopNode),
                new NodeToolboxItem(NodeKeys.FlowWait, FlowControlNodeViewModel.CreateWaitNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatOutput"),
            NodeCategory.Output,
            [
                new NodeToolboxItem(NodeKeys.OutputLog, OutputNodeViewModel.CreateLogNode),
                new NodeToolboxItem(NodeKeys.OutputProgress, OutputNodeViewModel.CreateProgressNode),
                new NodeToolboxItem(NodeKeys.OutputSave, OutputNodeViewModel.CreateSaveNode),
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
    public void AddNode(FlowNodeViewModel node)
    {
        if (node.Location == DefaultNodeLocation)
        {
            node.Location = _nextNodeLocation;
            _nextNodeLocation = new Point(_nextNodeLocation.X + 40, _nextNodeLocation.Y + 40);
        }

        Nodes.Add(node);
    }

    /// <summary>
    /// <para>从画布移除指定节点。</para>
    /// Removes the specified node from the canvas.
    /// </summary>
    /// <param name="node">
    /// <para>要移除的节点。</para>
    /// The node to remove.
    /// </param>
    public void RemoveNode(FlowNodeViewModel node)
    {
        var relatedConnections = Connections
            .Where(c => c.Source != null && c.Target != null &&
                (IsConnectorInNode(c.Source, node) || IsConnectorInNode(c.Target, node)))
            .ToList();

        foreach (var conn in relatedConnections)
        {
            Connections.Remove(conn);
        }

        Nodes.Remove(node);
    }

    private static bool IsConnectorInNode(NodeConnectorViewModel connector, FlowNodeViewModel node) => node.Input.Contains(connector) || node.Output.Contains(connector);

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
    public void CreateConnection(NodeConnectorViewModel source, NodeConnectorViewModel target)
    {
        if (Connections.Any(c => c.Source == source && c.Target == target))
        {
            return;
        }

        var connection = new NodeConnectionViewModel
        {
            Source = source,
            Target = target,
            RemoveRequested = OnConnectionRemoveRequested
        };

        Connections.Add(connection);

        source.Node?.NodeConnections.Add(connection);
        target.Node?.NodeConnections.Add(connection);

        source.SetConnected(true);
        target.SetConnected(true);

        target.Value = source.Value;
    }

    /// <summary>
    /// <para>移除指定的连接线。</para>
    /// Removes the specified connection.
    /// </summary>
    /// <param name="connection">
    /// <para>要移除的连接。</para>
    /// The connection to remove.
    /// </param>
    public void RemoveConnection(NodeConnectionViewModel connection)
    {
        if (connection == null)
        {
            return;
        }

        var source = connection.Source;
        var target = connection.Target;

        source?.Node?.NodeConnections.Remove(connection);
        target?.Node?.NodeConnections.Remove(connection);

        Connections.Remove(connection);

        source?.SetConnected(source.Connections.Any());

        target?.SetConnected(target.Connections.Any());
    }

    /// <summary>
    /// <para>连接线 Remove 命令的回调入口：从 Connections 集合移除指定连接。</para>
    /// Callback entry for the connection's Remove command: removes the specified connection from the Connections collection.
    /// </summary>
    /// <param name="connection">
    /// <para>要删除的连接。</para>
    /// The connection to remove.
    /// </param>
    private void OnConnectionRemoveRequested(NodeConnectionViewModel connection) => Connections.Remove(connection);

    /// <summary>
    /// <para>连接开始拖拽时回调（由 NodifyEditor 的 ConnectionStartedCommand 触发）。</para>
    /// Called when a connection drag starts (triggered by NodifyEditor's ConnectionStartedCommand).
    /// </summary>
    /// <param name="connector">
    /// <para>源连接器对象。</para>
    /// The source connector object.
    /// </param>
    [RelayCommand]
    private void OnConnectionStarted(object? connector) => _pendingSource = connector as NodeConnectorViewModel;

    /// <summary>
    /// <para>连接拖拽完成回调（由 NodifyEditor 的 ConnectionCompletedCommand 触发）。</para>
    /// Called when a connection drag completes (triggered by NodifyEditor's ConnectionCompletedCommand).
    /// </summary>
    /// <param name="args">
    /// <para>包含源和目标连接器的元组。</para>
    /// A tuple containing the source and target connectors.
    /// </param>
    [RelayCommand]
    private void OnConnectionCompleted(object? args)
    {
        if (!TryExtractConnectors(args, out var source, out var target))
        {
            _pendingSource = null;
            return;
        }

        _pendingSource = null;

        if (source == null || target == null)
        {
            return;
        }

        if (source.Node == target.Node && source == target)
        {
            return;
        }

        var sourceIsOutput = source.Node?.Output.Contains(source) == true;
        var targetIsInput = target.Node?.Input.Contains(target) == true;
        var sourceIsInput = source.Node?.Input.Contains(source) == true;
        var targetIsOutput = target.Node?.Output.Contains(target) == true;

        if (sourceIsOutput && targetIsInput)
        {
            CreateConnection(source, target);
        }
        else if (sourceIsInput && targetIsOutput)
        {
            CreateConnection(target, source);
        }
    }

    /// <summary>
    /// <para>从 ConnectionCompletedCommand 的回调参数中提取源/目标连接器。</para>
    /// Extracts source/target connectors from the ConnectionCompletedCommand callback argument.
    /// 兼容 ValueTuple / Tuple / 单连接器（取 _pendingSource 作为另一端）等 NodifyM 版本签名，
    /// 全部使用模式匹配以保持 trimming/AOT 安全。
    /// </summary>
    private bool TryExtractConnectors(object? args, out NodeConnectorViewModel? source, out NodeConnectorViewModel? target)
    {
        source = null;
        target = null;
        if (args is null)
        {
            return false;
        }

        // ValueTuple<object,object> — NodifyM 常见签名
        if (args is ValueTuple<object, object> vt)
        {
            source = vt.Item1 as NodeConnectorViewModel;
            target = vt.Item2 as NodeConnectorViewModel;
            return true;
        }

        // Tuple<object,object> 或更长的元组
        if (args is Tuple<object, object> tuple2)
        {
            source = tuple2.Item1 as NodeConnectorViewModel;
            target = tuple2.Item2 as NodeConnectorViewModel;
            return true;
        }

        // 直接传单个 connector —— NodifyM 12.0.0 的实际回调签名：
        // 取 _pendingSource（OnConnectionStarted 时记录）作为另一端，按 Output->Input 方向赋 source/target
        if (args is NodeConnectorViewModel single)
        {
            var other = _pendingSource;
            if (other is null)
            {
                source = single;
                target = null;
                return true;
            }

            var singleIsOutput = single.Node?.Output.Contains(single) == true;
            var otherIsInput = other.Node?.Input.Contains(other) == true;
            var singleIsInput = single.Node?.Input.Contains(single) == true;
            var otherIsOutput = other.Node?.Output.Contains(other) == true;

            if (singleIsOutput && otherIsInput)
            {
                source = single;
                target = other;
            }
            else if (singleIsInput && otherIsOutput)
            {
                source = other;
                target = single;
            }
            else
            {
                source = single;
                target = other;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// <para>连接器断开连接命令（由 NodifyEditor 的 DisconnectConnectorCommand 触发）。</para>
    /// Connector disconnect command (triggered by NodifyEditor's DisconnectConnectorCommand).
    /// </summary>
    /// <param name="connector">
    /// <para>要断开所有连接的连接器。</para>
    /// The connector to disconnect all connections from.
    /// </param>
    [RelayCommand]
    private void DisconnectConnector(object? connector)
    {
        if (connector is not NodeConnectorViewModel c)
        {
            return;
        }

        var conns = Connections.Where(conn => conn.Source == c || conn.Target == c).ToList();
        foreach (var conn in conns)
        {
            RemoveConnection(conn);
        }
    }

    /// <summary>
    /// <para>移除连接命令（由 NodifyEditor 的 RemoveConnectionCommand 触发）。</para>
    /// Remove connection command (triggered by NodifyEditor's RemoveConnectionCommand).
    /// </summary>
    /// <param name="connection">
    /// <para>要移除的连接。</para>
    /// The connection to remove.
    /// </param>
    [RelayCommand]
    private void OnRemoveConnection(NodeConnectionViewModel? connection)
    {
        if (connection != null)
        {
            RemoveConnection(connection);
        }
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(Zoom * 1.2, 3.0);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(Zoom / 1.2, 0.2);

    [RelayCommand]
    private void ResetZoom() => Zoom = 1.0;

    [RelayCommand]
    private void ToggleMinimap() => ShowMinimap = !ShowMinimap;

    [RelayCommand]
    private void DeleteSelected()
    {
        // 删除所有选中节点及其相关连接
        var selectedNodes = Nodes.Where(n => n.IsSelected).ToList();
        foreach (var node in selectedNodes)
        {
            RemoveNode(node);
        }
    }

    /// <summary>
    /// <para>清空画布上的全部节点与连接线。</para>
    /// Clears all nodes and connections on the canvas.
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        foreach (var node in Nodes)
        {
            node.NodeConnections.Clear();
        }

        Connections.Clear();
        Nodes.Clear();
        SelectedNode = null;
        _pendingSource = null;
        ExecutionLog.Clear();
        ExecutionStatus = LanguageService.Instance.GetString("Node_StatusReady");
    }

    private CancellationTokenSource? _runCts;

    /// <summary>
    /// <para>获取或设置节点流程是否正在运行。</para>
    /// Gets or sets whether the node flow is currently running.
    /// </summary>
    public bool IsRunning
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>异步运行节点流程。按拓扑顺序依次执行各节点；节点失败时暂停后续执行，
    /// If 分支节点按输入值选择继续执行的分支，结果写入执行日志。</para>
    /// Asynchronously runs the node flow, executing each node in topological order; pauses on
    /// node failure, follows the branch selected by an If node's input value, and writes results
    /// to the execution log.
    /// </summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        IsRunning = true;
        ExecutionStatus = LanguageService.Instance.GetString("Node_StatusRunning");

        try
        {
            // 拓扑排序：按连接的 Source -> Target 顺序执行
            var ordered = TopologicalSort(Nodes, Connections);
            LogService.Instance.Write(LogLevel.Info, "NodeRunner",
                $"Flow run started: {ordered.Count} node(s) scheduled.");

            var failed = false;
            var skippedNodes = new HashSet<FlowNodeViewModel>();

            foreach (var node in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // 条件分支中未选中的下游节点直接跳过
                if (skippedNodes.Contains(node))
                {
                    AddExecutionLog($"[SKIP] {node.Title}");
                    LogService.Instance.Write(LogLevel.Debug, "NodeRunner", $"Skipped node '{node.Title}' (inactive branch).");
                    continue;
                }

                // 在执行前，把上游 Output 端口的 Value 同步到本节点 Input 端口的 Value
                SyncUpstreamValues(node);
                ExecutionStatus = $"{node.Title}...";
                LogService.Instance.Write(LogLevel.Debug, "NodeRunner", $"Executing node '{node.Title}'...");

                string status;
                try
                {
                    status = await ExecuteNodeAsync(node, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status = $"[{node.Title}] FAIL: {ex.Message}";
                }

                ExecutionStatus = status;
                AddExecutionLog(status);

                // 按节点结果摘要写入统一日志（失败视为错误级别）
                if (!string.IsNullOrEmpty(status))
                {
                    if (IsFailureStatus(status))
                    {
                        LogService.Instance.Write(LogLevel.Error, "NodeRunner", status);
                    }
                    else
                    {
                        LogService.Instance.Write(LogLevel.Info, "NodeRunner", status);
                    }
                }

                // FlowControl 条件分支：If 节点执行后，只继续执行选中输出分支的下游节点
                if (node is FlowControlNodeViewModel flowControl && flowControl.Key == NodeKeys.FlowIf)
                {
                    foreach (var skipped in ComputeSkippedBranches(flowControl, Connections))
                    {
                        skippedNodes.Add(skipped);
                    }
                }

                // 失败暂停：节点失败立即停止后续节点执行
                if (IsFailureStatus(status))
                {
                    failed = true;
                    break;
                }

                // 节点执行完毕后，将其 Output 端口的 Value 同步到下游节点（通过连接传播）
                PropagateOutputsToDownstream(node);
            }

            if (failed)
            {
                AddExecutionLog(LanguageService.Instance.GetString("Node_FlowRunFailed"));
            }
            else
            {
                ExecutionStatus = LanguageService.Instance.GetString("Node_StatusCompleted");
                LogService.Instance.Write(LogLevel.Info, "NodeRunner", "Flow run completed.");
            }
        }
        catch (OperationCanceledException)
        {
            ExecutionStatus = LanguageService.Instance.GetString("Node_StatusStopped");
            AddExecutionLog(ExecutionStatus);
            LogService.Instance.Write(LogLevel.Warn, "NodeRunner", "Flow run cancelled.");
        }
        catch (Exception ex)
        {
            ExecutionStatus = ex.Message;
            AddExecutionLog(ex.Message);
            LogService.Instance.Write(LogLevel.Error, "NodeRunner", $"Flow run failed: {ex.Message}");
            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// <para>按节点类型分派执行，返回执行结果摘要文本。</para>
    /// Dispatches execution by node type and returns the execution result summary.
    /// </summary>
    private async Task<string> ExecuteNodeAsync(FlowNodeViewModel node, CancellationToken ct)
    {
        // 固件节点接入 FirmwareService 真实后端执行
        if (node is FirmwareNodeViewModel fwNode)
        {
            return await fwNode.ExecuteAsync(GetInputValue(node, 0), GetInputValue(node, 1), GetInputValue(node, 2), ct).ConfigureAwait(false);
        }

        // 烧录节点接入 FirmwareKit.Comm.Fastboot 真实刷写/擦除/重启
        if (node is FlashNodeViewModel flashNode)
        {
            return await flashNode.ExecuteAsync(GetInputValue(node, 0), GetInputValue(node, 1), GetInputValue(node, 2), ct).ConfigureAwait(false);
        }

        // 数据处理节点：Read File / Format Convert / Checksum / Compare
        if (node is DataNodeViewModel dataNode)
        {
            // Compare 节点的 Data A=Input[0]、Data B=Input[1]，统一按位置取，ExecuteAsync 内部按固定键解释
            return await dataNode.ExecuteAsync(GetInputValue(node, 0), GetInputValue(node, 1), GetInputValue(node, 1), ct).ConfigureAwait(false);
        }

        // 流程控制节点：If Branch / Loop / Wait
        if (node is FlowControlNodeViewModel flowNode)
        {
            return await flowNode.ExecuteAsync(GetInputValue(node, 0), ct).ConfigureAwait(false);
        }

        // 输出节点：Log Output / Progress Report / Save Result
        if (node is OutputNodeViewModel outputNode)
        {
            return await outputNode.ExecuteAsync(GetInputValue(node, 0), GetInputValue(node, 1), ct).ConfigureAwait(false);
        }

        await Task.Delay(300, ct).ConfigureAwait(false);
        return string.Empty;
    }

    /// <summary>
    /// <para>判断节点结果摘要是否为失败（含 FAIL 或 Error: 前缀）。</para>
    /// Determines whether a node result summary indicates failure (contains FAIL or an Error: prefix).
    /// </summary>
    private static bool IsFailureStatus(string status) =>
        status.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>计算 If 分支节点未选中输出分支的下游节点集合（同时被选中分支覆盖的节点除外）。</para>
    /// Computes the set of downstream nodes of an If node's inactive output branches
    /// (excluding nodes also reachable through the active branch).
    /// </summary>
    private static HashSet<FlowNodeViewModel> ComputeSkippedBranches(FlowNodeViewModel ifNode, IEnumerable<NodeConnectionViewModel> connections)
    {
        var active = new HashSet<FlowNodeViewModel>();
        var inactive = new HashSet<FlowNodeViewModel>();
        foreach (var output in ifNode.Output)
        {
            var reachable = CollectReachableDownstream(output, connections);
            if (string.Equals(output.Value, "1", StringComparison.Ordinal))
            {
                active.UnionWith(reachable);
            }
            else
            {
                inactive.UnionWith(reachable);
            }
        }

        inactive.ExceptWith(active);
        return inactive;
    }

    /// <summary>
    /// <para>从指定输出端口沿连接方向收集所有可达的下游节点。</para>
    /// Collects all downstream nodes reachable from the specified output connector following connection direction.
    /// </summary>
    private static HashSet<FlowNodeViewModel> CollectReachableDownstream(NodeConnectorViewModel start, IEnumerable<NodeConnectionViewModel> connections)
    {
        var result = new HashSet<FlowNodeViewModel>();
        var visited = new HashSet<NodeConnectorViewModel>();
        var queue = new Queue<NodeConnectorViewModel>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var connection in connections)
            {
                if (connection.Source != current)
                {
                    continue;
                }

                var targetNode = connection.Target?.Node;
                if (targetNode is null)
                {
                    continue;
                }

                result.Add(targetNode);
                foreach (var output in targetNode.Output)
                {
                    queue.Enqueue(output);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <para>获取节点指定索引输入端口的 Value，越界或空集合返回空字符串。</para>
    /// Gets the Value of the input connector at the given index, or empty string if out of range.
    /// </summary>
    private static string GetInputValue(FlowNodeViewModel node, int index) => index < 0 || index >= node.Input.Count ? string.Empty : node.Input[index].Value ?? string.Empty;

    /// <summary>
    /// <para>在执行某节点前，将该节点各 Input 端口的 Value 从上游 Output 端口同步过来。</para>
    /// Before executing a node, syncs each Input connector's Value from the upstream Output connector.
    /// </summary>
    private void SyncUpstreamValues(FlowNodeViewModel node)
    {
        // 找出所有以本节点 Input 端口为 Target 的连接
        foreach (var conn in Connections)
        {
            if (conn.Target?.Node != node)
            {
                continue;
            }

            var src = conn.Source;
            var tgt = conn.Target;
            if (src is null || tgt is null)
            {
                continue;
            }
            // 上游 Output 的 Value 同步到本 Input
            tgt.Value = src.Value;
        }
    }

    /// <summary>
    /// <para>节点执行完毕后，将其 Output 端口的当前 Value 通过连接推送到下游节点的 Input 端口。</para>
    /// After a node executes, pushes its Output connector Values through connections to downstream Input connectors.
    /// </summary>
    private void PropagateOutputsToDownstream(FlowNodeViewModel node)
    {
        foreach (var conn in Connections)
        {
            if (conn.Source?.Node != node)
            {
                continue;
            }

            var src = conn.Source;
            var tgt = conn.Target;
            if (src is null || tgt is null)
            {
                continue;
            }

            tgt.Value = src.Value;
        }
    }

    /// <summary>
    /// <para>停止正在运行的节点流程。</para>
    /// Stops the currently running node flow.
    /// </summary>
    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    /// <summary>
    /// <para>将当前画布保存为 .uotflow 流程文件（保存到当前工程目录，未打开工程时提示）。</para>
    /// Saves the current canvas as a .uotflow flow file (saved to the current project
    /// directory; prompts when no project is open).
    /// </summary>
    [RelayCommand]
    private async Task SaveFlowAsync()
    {
        if (!TryGetFlowPath(out var path))
        {
            return;
        }

        try
        {
            var json = FlowSerializer.Serialize(BuildFlowDocument());
            await File.WriteAllTextAsync(path, json);
            var message = LanguageService.Instance.GetString("Node_FlowSaved", path);
            ExecutionStatus = message;
            AddExecutionLog(message);
            LogService.Instance.Write(LogLevel.Info, "NodeEditor", message);
        }
        catch (Exception ex)
        {
            var message = $"{LanguageService.Instance.GetString("Node_FlowSaveFailed")}: {ex.Message}";
            ExecutionStatus = message;
            AddExecutionLog(message);
            LogService.Instance.Write(LogLevel.Error, "NodeEditor", message);
        }
    }

    /// <summary>
    /// <para>从当前工程目录加载 .uotflow 流程文件并重建画布（未打开工程或文件缺失时提示）。</para>
    /// Loads a .uotflow flow file from the current project directory and rebuilds the canvas
    /// (prompts when no project is open or the file is missing).
    /// </summary>
    [RelayCommand]
    private async Task LoadFlowAsync()
    {
        if (!TryGetFlowPath(out var path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            var notFound = LanguageService.Instance.GetString("Node_FlowNotFound", path);
            ExecutionStatus = notFound;
            LogService.Instance.Write(LogLevel.Warn, "NodeEditor", notFound);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var document = FlowSerializer.Deserialize(json);
            if (document is null)
            {
                var failed = LanguageService.Instance.GetString("Node_FlowLoadFailed");
                ExecutionStatus = failed;
                LogService.Instance.Write(LogLevel.Error, "NodeEditor", failed);
                return;
            }

            LoadFlowDocument(document);
            var message = LanguageService.Instance.GetString("Node_FlowLoaded", path);
            ExecutionStatus = message;
            AddExecutionLog(message);
            LogService.Instance.Write(LogLevel.Info, "NodeEditor", message);
        }
        catch (Exception ex)
        {
            var message = $"{LanguageService.Instance.GetString("Node_FlowLoadFailed")}: {ex.Message}";
            ExecutionStatus = message;
            AddExecutionLog(message);
            LogService.Instance.Write(LogLevel.Error, "NodeEditor", message);
        }
    }

    /// <summary>
    /// <para>从当前打开的工程路径推导 .uotflow 文件路径；未打开工程时提示并返回 false。</para>
    /// Derives the .uotflow file path from the currently open project path; prompts and
    /// returns false when no project is open.
    /// </summary>
    private bool TryGetFlowPath(out string path)
    {
        var openPath = FirmwareProjectService.OpenPath;
        var directory = string.IsNullOrEmpty(openPath) ? null : Path.GetDirectoryName(openPath);
        if (string.IsNullOrEmpty(openPath) || string.IsNullOrEmpty(directory))
        {
            var message = LanguageService.Instance.GetString("Node_FlowNoProject");
            ExecutionStatus = message;
            LogService.Instance.Write(LogLevel.Warn, "NodeEditor", message);
            path = string.Empty;
            return false;
        }

        path = Path.Combine(directory, Path.GetFileNameWithoutExtension(openPath) + FlowExtension);
        return true;
    }

    /// <summary>
    /// <para>将当前画布节点与连接转换为可序列化的流程文档。</para>
    /// Converts the current canvas nodes and connections into a serializable flow document.
    /// </summary>
    private FlowDocumentDto BuildFlowDocument()
    {
        var nodeDtos = new List<FlowNodeDto>(Nodes.Count);
        var idMap = new Dictionary<FlowNodeViewModel, string>(Nodes.Count);
        foreach (var node in Nodes)
        {
            var id = node.Id.ToString("N");
            idMap[node] = id;
            nodeDtos.Add(new FlowNodeDto
            {
                Id = id,
                Title = node.Title,
                NodeCategory = node.Category,
                X = node.Location.X,
                Y = node.Location.Y,
                InputValues = [.. node.Input.Select(connector => connector.Value ?? string.Empty)]
            });
        }

        var connectionDtos = new List<FlowConnectionDto>(Connections.Count);
        foreach (var connection in Connections)
        {
            var source = connection.Source;
            var target = connection.Target;
            if (source?.Node is null || target?.Node is null
                || !idMap.TryGetValue(source.Node, out var sourceId)
                || !idMap.TryGetValue(target.Node, out var targetId))
            {
                continue;
            }

            connectionDtos.Add(new FlowConnectionDto
            {
                SourceNodeId = sourceId,
                SourcePort = source.Node.Output.IndexOf(source),
                TargetNodeId = targetId,
                TargetPort = target.Node.Input.IndexOf(target)
            });
        }

        return new FlowDocumentDto { Nodes = nodeDtos, Connections = connectionDtos };
    }

    /// <summary>
    /// <para>用流程文档重建画布：清空现有内容，按 NodeCategory+标题映射到节点工厂方法创建节点，再重建连接。</para>
    /// Rebuilds the canvas from a flow document: clears the current content, maps each node back
    /// to its factory method by NodeCategory + title, and recreates the connections.
    /// </summary>
    private void LoadFlowDocument(FlowDocumentDto document)
    {
        foreach (var node in Nodes)
        {
            node.NodeConnections.Clear();
        }

        Connections.Clear();
        Nodes.Clear();
        SelectedNode = null;
        _pendingSource = null;
        ExecutionLog.Clear();

        var created = new Dictionary<string, FlowNodeViewModel>();
        foreach (var dto in document.Nodes)
        {
            if (string.IsNullOrEmpty(dto.Id))
            {
                LogService.Instance.Write(LogLevel.Warn, "NodeEditor", "Flow load: node skipped (missing id).");
                continue;
            }

            var node = CreateNodeFromDto(dto);
            if (node is null)
            {
                LogService.Instance.Write(LogLevel.Warn, "NodeEditor",
                    $"Flow load: unknown node skipped ({dto.NodeCategory}: {dto.Title}).");
                continue;
            }

            if (Guid.TryParse(dto.Id, out var id))
            {
                node.Id = id;
            }

            node.Location = new Point(dto.X, dto.Y);
            var inputs = dto.InputValues;
            if (inputs is not null)
            {
                for (var i = 0; i < inputs.Count && i < node.Input.Count; i++)
                {
                    node.Input[i].Value = inputs[i];
                }
            }

            created[dto.Id] = node;
            Nodes.Add(node);
        }

        foreach (var dto in document.Connections)
        {
            if (string.IsNullOrEmpty(dto.SourceNodeId) || string.IsNullOrEmpty(dto.TargetNodeId))
            {
                continue;
            }

            if (!created.TryGetValue(dto.SourceNodeId, out var sourceNode)
                || !created.TryGetValue(dto.TargetNodeId, out var targetNode))
            {
                continue;
            }

            if (dto.SourcePort < 0 || dto.SourcePort >= sourceNode.Output.Count
                || dto.TargetPort < 0 || dto.TargetPort >= targetNode.Input.Count)
            {
                continue;
            }

            CreateConnection(sourceNode.Output[dto.SourcePort], targetNode.Input[dto.TargetPort]);
        }
    }

    /// <summary>
    /// <para>按节点类别与标题在工具箱中查找对应模板，并调用工厂方法创建节点；未匹配时返回 null。</para>
    /// Looks up the toolbox template matching the node category and title, and creates the node
    /// via its factory method; returns null when nothing matches.
    /// </summary>
    private FlowNodeViewModel? CreateNodeFromDto(FlowNodeDto dto)
    {
        foreach (var category in ToolboxCategories)
        {
            if (category.Category != dto.NodeCategory)
            {
                continue;
            }

            foreach (var item in category.Items)
            {
                if (string.Equals(item.Name, dto.Title, StringComparison.Ordinal))
                {
                    return item.CreateNode();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <para>对节点进行拓扑排序，基于连接的依赖关系。</para>
    /// Performs a topological sort on the nodes, based on connection dependencies.
    /// </summary>
    private static List<FlowNodeViewModel> TopologicalSort(
        IEnumerable<FlowNodeViewModel> nodes,
        IEnumerable<NodeConnectionViewModel> connections)
    {
        var nodeList = nodes.ToList();
        var result = new List<FlowNodeViewModel>();
        var visited = new HashSet<FlowNodeViewModel>();
        var inProgress = new HashSet<FlowNodeViewModel>();

        // 构建依赖图：targetNode 依赖于 sourceNode
        var deps = connections
            .Where(c => c.Source?.Node != null && c.Target?.Node != null)
            .GroupBy(c => c.Target!.Node!)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Source!.Node!).ToList());

        void Visit(FlowNodeViewModel node)
        {
            if (visited.Contains(node))
            {
                return;
            }

            if (inProgress.Contains(node))
            {
                return; // 循环依赖，跳过
            }

            inProgress.Add(node);
            if (deps.TryGetValue(node, out var sources))
            {
                foreach (var s in sources)
                {
                    Visit(s);
                }
            }
            inProgress.Remove(node);
            visited.Add(node);
            result.Add(node);
        }

        foreach (var n in nodeList)
        {
            Visit(n);
        }

        return result;
    }
}

/// <summary>
/// <para>节点工具箱分类，包含同类别的节点项列表。</para>
/// Node toolbox category, containing a list of node items in the same category.
/// </summary>
/// <remarks>
/// <para>创建工具箱分类。</para>
/// Creates a toolbox category.
/// </remarks>
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
public class NodeToolboxCategory(string name, NodeCategory category, ObservableCollection<NodeToolboxItem> items)
{
    /// <summary>
    /// <para>获取分类名称。</para>
    /// Gets the category name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// <para>获取节点类别。</para>
    /// Gets the node category.
    /// </summary>
    public NodeCategory Category { get; } = category;

    /// <summary>
    /// <para>获取分类下的节点项列表。</para>
    /// Gets the list of node items in this category.
    /// </summary>
    public ObservableCollection<NodeToolboxItem> Items { get; } = items;
}

/// <summary>
/// <para>节点工具箱项，表示工具箱中可添加到画布的节点模板。</para>
/// Node toolbox item, representing a node template in the toolbox that can be added to the canvas.
/// </summary>
/// <remarks>
/// <para>使用固定键创建工具箱项，显示名称按键本地化。</para>
/// Creates a toolbox item from a fixed key; the display name is localized by the key.
/// </remarks>
/// <param name="key">
/// <para>节点固定键（本地化资源键）。</para>
/// The fixed key of the node (localization resource key).
/// </param>
/// <param name="createNode">
/// <para>创建节点实例的工厂方法。</para>
/// The factory method to create a node instance.
/// </param>
public class NodeToolboxItem(string key, Func<FlowNodeViewModel> createNode) : ViewModelBase
{

    /// <summary>
    /// <para>获取节点固定键（本地化资源键）。</para>
    /// Gets the fixed key of the node (localization resource key).
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// <para>获取节点项名称（按键本地化）。</para>
    /// Gets the node item name (localized by the key).
    /// </summary>
    public string Name { get; } = LanguageService.Instance.GetString(key);

    /// <summary>
    /// <para>获取创建节点实例的工厂方法。</para>
    /// Gets the factory method to create a node instance.
    /// </summary>
    public Func<FlowNodeViewModel> CreateNode { get; } = createNode;

    /// <summary>
    /// <para>获取或设置该工具箱项是否可见（用于搜索过滤）。</para>
    /// Gets or sets whether this toolbox item is visible (used for search filtering).
    /// </summary>
    public bool IsVisible
    {
        get;
        set => SetProperty(ref field, value);
    } = true;
}