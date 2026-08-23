using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels;

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
                new NodeToolboxItem("Unpack Firmware", FirmwareNodeViewModel.CreateUnpackNode),
                new NodeToolboxItem("Pack Firmware", FirmwareNodeViewModel.CreatePackNode),
                new NodeToolboxItem("Verify Signature", FirmwareNodeViewModel.CreateVerifyNode),
                new NodeToolboxItem("Extract Partition", FirmwareNodeViewModel.CreateExtractNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatFlash"),
            NodeCategory.Flash,
            [
                new NodeToolboxItem("Connect Device", FlashNodeViewModel.CreateConnectNode),
                new NodeToolboxItem("Flash Partition", FlashNodeViewModel.CreateFlashNode),
                new NodeToolboxItem("Erase Partition", FlashNodeViewModel.CreateEraseNode),
                new NodeToolboxItem("Reboot Device", FlashNodeViewModel.CreateRebootNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatData"),
            NodeCategory.Data,
            [
                new NodeToolboxItem("Read File", DataNodeViewModel.CreateFileReadNode),
                new NodeToolboxItem("Format Convert", DataNodeViewModel.CreateConvertNode),
                new NodeToolboxItem("Checksum", DataNodeViewModel.CreateChecksumNode),
                new NodeToolboxItem("Compare", DataNodeViewModel.CreateCompareNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatFlow"),
            NodeCategory.Flow,
            [
                new NodeToolboxItem("If Branch", FlowControlNodeViewModel.CreateIfNode),
                new NodeToolboxItem("Loop", FlowControlNodeViewModel.CreateLoopNode),
                new NodeToolboxItem("Wait", FlowControlNodeViewModel.CreateWaitNode),
            ]
        ));

        ToolboxCategories.Add(new NodeToolboxCategory(
            LanguageService.Instance.GetString("Node_CatOutput"),
            NodeCategory.Output,
            [
                new NodeToolboxItem("Log Output", OutputNodeViewModel.CreateLogNode),
                new NodeToolboxItem("Progress Report", OutputNodeViewModel.CreateProgressNode),
                new NodeToolboxItem("Save Result", OutputNodeViewModel.CreateSaveNode),
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
    /// <para>异步运行节点流程。按拓扑顺序依次执行各节点，并更新执行状态。</para>
    /// Asynchronously runs the node flow, executing each node in topological order and updating the execution status.
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

            foreach (var node in ordered)
            {
                ct.ThrowIfCancellationRequested();
                // 在执行前，把上游 Output 端口的 Value 同步到本节点 Input 端口的 Value
                SyncUpstreamValues(node);
                ExecutionStatus = $"{node.Title}...";
                LogService.Instance.Write(LogLevel.Debug, "NodeRunner", $"Executing node '{node.Title}'...");

                // 固件节点接入 FirmwareService 真实后端执行
                if (node is FirmwareNodeViewModel fwNode)
                {
                    var firmwarePath = GetInputValue(node, 0);
                    var outputDir = GetInputValue(node, 1);
                    var partitionName = GetInputValue(node, 2);
                    var status = await fwNode.ExecuteAsync(firmwarePath, outputDir, partitionName, ct).ConfigureAwait(false);
                    ExecutionStatus = status;
                }
                // 烧录节点接入 FirmwareKit.Comm.Fastboot 真实刷写/擦除/重启
                else if (node is FlashNodeViewModel flashNode)
                {
                    var devicePath = GetInputValue(node, 0);
                    var partition = GetInputValue(node, 1);
                    var imagePath = GetInputValue(node, 2);
                    var status = await flashNode.ExecuteAsync(devicePath, partition, imagePath, ct).ConfigureAwait(false);
                    ExecutionStatus = status;
                }
                // 数据处理节点：Read File / Format Convert / Checksum / Compare
                else if (node is DataNodeViewModel dataNode)
                {
                    var pathOrInput = GetInputValue(node, 0);
                    var formatOrAlgorithm = GetInputValue(node, 1);
                    // Compare 节点的 Data B 通常在 Input[1]，但工具箱中 Data A=Input[0], Data B=Input[1]
                    // 这里统一按位置取，ExecuteAsync 内部按 Title 解释
                    var dataB = GetInputValue(node, 1);
                    var status = await dataNode.ExecuteAsync(pathOrInput, formatOrAlgorithm, dataB, ct).ConfigureAwait(false);
                    ExecutionStatus = status;
                }
                // 流程控制节点：If Branch / Loop / Wait
                else if (node is FlowControlNodeViewModel flowNode)
                {
                    var conditionOrCountOrDelay = GetInputValue(node, 0);
                    var status = await flowNode.ExecuteAsync(conditionOrCountOrDelay, ct).ConfigureAwait(false);
                    ExecutionStatus = status;
                }
                // 输出节点：Log Output / Progress Report / Save Result
                else if (node is OutputNodeViewModel outputNode)
                {
                    var messageOrData = GetInputValue(node, 0);
                    var levelOrStatusOrPath = GetInputValue(node, 1);
                    var status = await outputNode.ExecuteAsync(messageOrData, levelOrStatusOrPath, ct).ConfigureAwait(false);
                    ExecutionStatus = status;
                }
                else
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }

                // 节点执行完毕后，将其 Output 端口的 Value 同步到下游节点（通过连接传播）
                PropagateOutputsToDownstream(node);

                // 按节点结果摘要写入统一日志（FAIL 视为错误级别）
                if (!string.IsNullOrEmpty(ExecutionStatus))
                {
                    if (ExecutionStatus.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Instance.Write(LogLevel.Error, "NodeRunner", ExecutionStatus);
                    }
                    else
                    {
                        LogService.Instance.Write(LogLevel.Info, "NodeRunner", ExecutionStatus);
                    }
                }
            }

            ExecutionStatus = LanguageService.Instance.GetString("Node_StatusCompleted");
            LogService.Instance.Write(LogLevel.Info, "NodeRunner", "Flow run completed.");
        }
        catch (OperationCanceledException)
        {
            ExecutionStatus = LanguageService.Instance.GetString("Node_StatusStopped");
            LogService.Instance.Write(LogLevel.Warn, "NodeRunner", "Flow run cancelled.");
        }
        catch (Exception ex)
        {
            ExecutionStatus = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "NodeRunner", $"Flow run failed: {ex.Message}");
            throw;
        }
        finally
        {
            IsRunning = false;
        }
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
/// <para>创建工具箱项。</para>
/// Creates a toolbox item.
/// </remarks>
/// <param name="name">
/// <para>节点项名称。</para>
/// The node item name.
/// </param>
/// <param name="createNode">
/// <para>创建节点实例的工厂方法。</para>
/// The factory method to create a node instance.
/// </param>
public class NodeToolboxItem(string name, Func<FlowNodeViewModel> createNode) : ViewModelBase
{

    /// <summary>
    /// <para>获取节点项名称。</para>
    /// Gets the node item name.
    /// </summary>
    public string Name { get; } = name;

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