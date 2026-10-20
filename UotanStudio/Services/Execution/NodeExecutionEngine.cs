using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Execution;

/// <summary>
/// Node workflow execution engine. Schedules nodes via topological sort (Kahn's algorithm); supports async execution, cancellation, and a state machine.
/// <para>节点工作流执行引擎。基于拓扑排序（Kahn 算法）调度节点，支持异步执行、取消、状态机。</para>
/// </summary>
public sealed class NodeExecutionEngine
{
    /// <summary>
    /// Maximum iterations per Loop node execution, guarding misconfigured looping workflows from running forever.
    /// <para>循环节点单次执行的最大迭代数上限，防止错误配置的死循环工作流无限执行。</para>
    /// </summary>
    public const int MaxLoopIterations = 10_000;

    private readonly IUnifiedLog _log;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private PatchRun? _patches;
    private VisualPluginPackage? _package;
    private IWorkflowInteractionService _interactions = UnsupportedWorkflowInteractionService.Instance;
    private Workspace.IWorkflowDocuments? _documents;
    private readonly System.Collections.Concurrent.ConcurrentStack<IAsyncDisposable> _resources = new();

    /// <summary>
    /// Creates the execution engine.
    /// <para>创建执行引擎。</para>
    /// </summary>
    /// <param name="log">
    /// Unified log service.
    /// <para>统一日志服务。</para>
    /// </param>
    public NodeExecutionEngine(IUnifiedLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Whether execution is in progress.
    /// <para>当前是否正在执行。</para>
    /// </summary>
    /// <value><c>true</c> while a workflow run is in progress.<para>工作流运行期间为 <c>true</c>。</para></value>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Progress change event carrying the fraction of the run completed.
    /// <para>执行进度变更事件，携带整次运行已完成的占比。</para>
    /// </summary>
    public event EventHandler<double>? ProgressChanged;

    /// <summary>
    /// Node status change event.
    /// <para>节点状态变更事件。</para>
    /// </summary>
    public event EventHandler<NodeStatusChangedEventArgs>? NodeStatusChanged;

    /// <summary>
    /// Execution completed event (success or failure).
    /// <para>执行完成事件（成功或失败）。</para>
    /// </summary>
    public event EventHandler<ExecutionCompletedEventArgs>? ExecutionCompleted;

    /// <summary>Asynchronously executes the workflow formed by the given nodes and connections.
    /// <para>异步执行给定节点和连接构成的工作流。</para></summary>
    /// <param name="nodes">The nodes of the workflow.<para>工作流包含的节点。</para></param>
    /// <param name="connections">List of connections (source → target), determining execution order and data flow.
    /// <para>连接列表（源→目标），用于确定执行顺序与数据流。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <param name="options">Shared staging, resource scope and interaction options for the run.
    /// <para>本次运行的共享暂存、资源作用域与交互选项。</para></param>
    /// <returns>Execution result.<para>执行结果。</para></returns>
    public async Task<ExecutionResult> ExecuteAsync(
        IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnection> connections,
        CancellationToken cancellationToken = default,
        NodeExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        options ??= new NodeExecutionOptions();
        if (_isRunning)
        {
            throw new InvalidOperationException("The workflow engine is already running.");
        }
        var ownsPatches = options.SharedPatches is null;
        _patches = options.SharedPatches ?? new PatchRun(options.Preview);
        _package = options.Package;
        _interactions = options.Interactions;
        _documents = options.Documents;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _isRunning = true;

        try
        {
            foreach (var node in nodes)
            {
                node.ExecutionStatus = NodeExecutionStatus.Idle;
                node.ExecutionError = string.Empty;
                foreach (var input in node.Input)
                {
                    input.IsRuntimeSensitive = false;
                    input.Data = input.InitialValue;
                }
                foreach (var output in node.Output)
                {
                    output.IsRuntimeSensitive = false;
                    output.Data = null;
                }
            }
            if (options.ValidatePackage is not null)
            {
                var errors = options.ValidatePackage();
                if (errors.Count > 0)
                {
                    return RejectPreflight(nodes, string.Join(Environment.NewLine, errors));
                }
            }
            var interactionError = WorkflowInteractionPreflight.Validate(nodes, _interactions, _documents is not null);
            if (interactionError is not null)
            {
                return RejectPreflight(nodes, interactionError);
            }
            var graphIssues = WorkflowGraphValidation.ValidateRuntime(nodes, connections);
            if (graphIssues.Count > 0)
            {
                RecordUnreachedComposites(nodes, CompositeSkipReason.PreflightRejected);
                foreach (var node in nodes)
                {
                    node.ExecutionError = string.Join(Environment.NewLine,
                        graphIssues.Where(issue => ReferenceEquals(issue.Node, node)).Select(issue => issue.Message));
                    node.ExecutionStatus = node.ExecutionError.Length > 0 ? NodeExecutionStatus.Failed : NodeExecutionStatus.Skipped;
                    NodeStatusChanged?.Invoke(this, new(node));
                }
                return RejectPreflight(nodes, string.Join(Environment.NewLine, graphIssues.Select(issue => issue.DisplayText)));
            }
            var destructiveError = await WorkflowInteractionPreflight.ConfirmDestructiveAsync(nodes, _interactions, token).ConfigureAwait(false);
            if (destructiveError is not null)
            {
                return RejectPreflight(nodes, destructiveError);
            }
            var patchPolicy = PatchWorkflowPolicy.Inspect(nodes);
            if ((_patches.Preview || patchPolicy.HasPatches) && !patchPolicy.Safe)
            {
                return RejectPreflight(nodes, LanguageService.Instance.GetString("Node_Patch_UnsupportedGraph"));
            }
            _log.Write(LogLevel.Info, "Engine", $"Starting execution with {nodes.Count} nodes, {connections?.Count ?? 0} connections");

            var order = TopologicalSort(nodes, connections);
            if (order == null)
            {
                _log.Write(LogLevel.Error, "Engine", "Cycle detected in workflow, cannot execute");
                return RejectPreflight(nodes, "Cycle detected in workflow");
            }

            _log.Write(LogLevel.Debug, "Engine", $"Topological order computed: {order.Count} nodes");

            var completed = 0;
            var total = order.Count;
            List<string> failedNodes = [];
            var failLock = new object();
            var aborted = false;
            var terminated = false;
            string? terminationReason = null;
            var byId = nodes.ToDictionary(n => n.InstanceId);
            var flowOut = BuildFlowEdges(nodes, connections);
            var gateError = PatchGatePolicy.Validate(nodes, connections, flowOut);
            if (gateError is not null)
            {
                return RejectPreflight(nodes, gateError);
            }

            HashSet<string> handledByFlow = [];
            var skippedByBranch = GetInactiveEntryNodes(options.EntryActivation, connections);
            HashSet<string> skippedByFailure = [];

            foreach (var node in order)
            {
                if (token.IsCancellationRequested)
                {
                    _log.Write(LogLevel.Warn, "Engine", "Execution cancelled by user");
                    aborted = true;
                    break;
                }

                if (handledByFlow.Contains(node.InstanceId))
                {
                    completed++;
                    continue;
                }

                if (skippedByBranch.Contains(node.InstanceId))
                {
                    RecordSkippedComposite(node, CompositeSkipReason.Condition);
                    node.ExecutionStatus = NodeExecutionStatus.Skipped;
                    NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                    _log.Write(LogLevel.Debug, "Engine", $"Node '{node.InstanceId}' skipped by branch condition");
                    completed++;
                    continue;
                }

                if (skippedByFailure.Contains(node.InstanceId))
                {
                    RecordSkippedComposite(node, CompositeSkipReason.UpstreamFailure);
                    node.ExecutionStatus = NodeExecutionStatus.Skipped;
                    NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                    _log.Write(LogLevel.Debug, "Engine", $"Node '{node.InstanceId}' skipped because an upstream node failed");
                    completed++;
                    continue;
                }

                bool succeeded;
                try
                {
                    succeeded = await ExecuteSingleNodeAsync(node, byId, connections, token,
                        p => SafeReportProgress(completed, total, p)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log.Write(LogLevel.Warn, "Engine", "Execution cancelled by user");
                    aborted = true;
                    break;
                }
                catch (WorkflowTerminatedException termination)
                {
                    _log.Write(LogLevel.Warn, "Engine", $"Execution terminated: {termination.Message}");
                    terminated = true;
                    terminationReason = termination.Reason;
                    break;
                }
                if (!succeeded)
                {
                    lock (failLock) { failedNodes.Add(node.InstanceId); }

                    switch (options.FailurePolicy)
                    {
                        case ExecutionFailurePolicy.FailFast:
                            aborted = true;
                            break;
                        case ExecutionFailurePolicy.SkipDependents:
                            MarkDownstreamSkipped(node, byId, connections, skippedByFailure);
                            break;
                    }

                    if (aborted)
                        break;
                }

                completed++;
                var progress = (double)completed / Math.Max(1, total);
                ProgressChanged?.Invoke(this, progress);

                if (succeeded)
                {
                    try
                    {
                        if (await ApplyFlowControlAsync(node, byId, connections, order, flowOut, token,
                            handledByFlow, skippedByBranch, skippedByFailure, failedNodes, failLock, options).ConfigureAwait(false))
                        {
                            aborted = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Write(LogLevel.Warn, "Engine", "Execution cancelled during flow control dispatch");
                        aborted = true;
                        break;
                    }
                    catch (WorkflowTerminatedException termination)
                    {
                        _log.Write(LogLevel.Warn, "Engine", $"Execution terminated during flow dispatch: {termination.Message}");
                        terminated = true;
                        terminationReason = termination.Reason;
                        break;
                    }
                }
            }

            var result = terminated
                ? ExecutionResult.Terminated(terminationReason)
                : aborted && failedNodes.Count == 0
                ? ExecutionResult.Cancelled()
                : aborted
                    ? ExecutionResult.Failed("Execution stopped after a node failure", failedNodes)
                : failedNodes.Count > 0
                    ? nodes.Any(node => node.ExecutionStatus == NodeExecutionStatus.Succeeded)
                        ? ExecutionResult.PartiallySucceeded(failedNodes)
                        : ExecutionResult.Failed("No nodes completed successfully", failedNodes)
                    : ExecutionResult.Succeeded(total);

            RecordUnreachedComposites(nodes, CompositeSkipReason.NotReached);
            if (ownsPatches)
            {
                try
                {
                    await _patches.CompleteAsync(result.Status == ExecutionStatus.Succeeded, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = ExecutionResult.Cancelled();
                }
                catch (Exception error)
                {
                    result = ExecutionResult.Failed(error.Message, failedNodes);
                    _log.Write(LogLevel.Error, "Engine", error.Message);
                }
            }
            result.PatchChanges = _patches.Changes;
            result.PatchOutcome = _patches.Outcome;
            result.CompositeExecutions = _patches.CompositeExecutions;
            result.CompositeReportTruncated = _patches.CompositeReportTruncated;
            _log.Write(aborted ? LogLevel.Warn : LogLevel.Info, "Engine", aborted ? "Execution aborted" : $"Execution completed: {result.Status}");
            ExecutionCompleted?.Invoke(this, new ExecutionCompletedEventArgs(result));
            return result;
        }
        finally
        {
            while (_resources.TryPop(out var resource))
            {
                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _log.Write(LogLevel.Error, "Engine", $"Runtime resource cleanup failed: {error.Message}");
                }
            }
            if (ownsPatches && _patches is not null)
            {
                try { await _patches.DisposeAsync().ConfigureAwait(false); }
                catch (IOException error) { _log.Write(LogLevel.Warn, "Engine", error.Message); }
                catch (UnauthorizedAccessException error) { _log.Write(LogLevel.Warn, "Engine", error.Message); }
            }
            _patches = null;
            _package = null;
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Extracts the control-flow edges whose source connector port type is flow, mapping a node instance ID to its
    /// output connector titles and target nodes.
    /// <para>提取源连接器端口类型为控制流的边，把节点实例 ID 映射到输出端口标题与目标节点。</para>
    /// </summary>
    /// <param name="nodes">Nodes used to resolve the endpoints of each connection.<para>用于解析每条连接两端的节点。</para></param>
    /// <param name="connections">Connections to filter for flow ports.<para>需要按控制流端口筛选的连接。</para></param>
    /// <returns>A mapping from source instance ID to its flow edges.<para>源实例 ID 到其控制流边的映射。</para></returns>
    private static Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> BuildFlowEdges(
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnection>? connections)
    {
        Dictionary<string, List<(string, FlowNodeViewModel)>> map = [];
        if (connections == null || connections.Count == 0) return map;

        var byId = nodes.ToDictionary(n => n.InstanceId);
        foreach (var conn in connections)
        {
            if (conn.SourceInstanceId == null || conn.TargetInstanceId == null) continue;
            if (!byId.TryGetValue(conn.SourceInstanceId, out var sourceNode)) continue;
            if (!byId.TryGetValue(conn.TargetInstanceId, out var targetNode)) continue;
            var sourceConnector = WorkflowGraphValidation.ResolvePort(sourceNode.Output, conn.SourcePortId, conn.SourceConnector);
            if (sourceConnector is not { PortType: NodePortType.Flow }) continue;

            if (!map.TryGetValue(sourceNode.InstanceId, out var list))
            {
                list = [];
                map[sourceNode.InstanceId] = list;
            }
            list.Add((sourceConnector.Title, targetNode));
        }
        return map;
    }

    /// <summary>
    /// Executes a single node: injects data flow, updates the state machine, and records failures.
    /// <para>执行单个节点：注入数据流、更新状态机并记录失败原因。</para>
    /// </summary>
    /// <param name="node">The node to execute.<para>要执行的节点。</para></param>
    /// <param name="byId">Nodes indexed by instance ID for flow lookup.<para>按实例 ID 索引、用于查找数据流的节点。</para></param>
    /// <param name="connections">Connections supplying the node's input data.<para>为节点提供输入数据的连接。</para></param>
    /// <param name="token">Cancellation token for the run.<para>本次运行的取消令牌。</para></param>
    /// <param name="progress">Receives the node's local progress reports.<para>接收该节点上报的局部进度。</para></param>
    /// <returns><c>true</c> when the node completed without an error.<para>节点无错误完成时为 <c>true</c>。</para></returns>
    private async Task<bool> ExecuteSingleNodeAsync(
        FlowNodeViewModel node,
        IReadOnlyDictionary<string, FlowNodeViewModel> byId,
        IReadOnlyList<NodeConnection>? connections,
        CancellationToken token,
        Action<double> progress)
    {
        node.ExecutionStatus = NodeExecutionStatus.Running;
        NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));

        int? compositeInvocation = null;
        string? compositeError = null;

        try
        {
            if (node is CompositeNodeViewModel composite && _patches is not null)
            {
                compositeInvocation = _patches.BeginComposite(composite);
            }
            FlowInputs(node, byId, connections);
            foreach (var input in node.Input)
            {
                if (input.IsRequired && input.PortType != NodePortType.Flow
                    && input.IsVisibleWhen?.Invoke(node) != false
                    && (input.Data is null || input.Data is string value && string.IsNullOrWhiteSpace(value)))
                {
                    throw new InvalidDataException(LanguageService.Instance.GetString("Node_Graph_Required", input.LocalizedTitle));
                }
            }

            var context = new ExecutionContextImpl(_log, token, progress, _patches, _package, _interactions,
                waiting =>
                {
                    node.ExecutionStatus = waiting ? NodeExecutionStatus.WaitingForInput : NodeExecutionStatus.Running;
                    NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                }, _resources.Push, _documents);
            _log.Write(LogLevel.Info, "Engine", $"Executing node '{node.Title}' (id={node.InstanceId})");
            await node.ExecuteAsync(context).ConfigureAwait(false);

            node.ExecutionStatus = NodeExecutionStatus.Succeeded;
            NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
            _log.Write(LogLevel.Debug, "Engine", $"Node '{node.InstanceId}' completed");
            return true;
        }
        catch (OperationCanceledException)
        {
            node.ExecutionStatus = NodeExecutionStatus.Cancelled;
            NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
            throw;
        }
        catch (WorkflowTerminatedException)
        {
            compositeError = null;
            node.ExecutionStatus = NodeExecutionStatus.Succeeded;
            NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
            throw;
        }
        catch (Exception ex)
        {
            compositeError = ex.Message;
            node.ExecutionError = ex.Message;
            _patches?.RecordFailure(ex);
            node.ExecutionStatus = NodeExecutionStatus.Failed;
            NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
            _log.Write(LogLevel.Error, "Engine", $"Node '{node.InstanceId}' failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (compositeInvocation is { } invocation)
            {
                _patches!.EndComposite(invocation, node.ExecutionStatus, compositeError);
            }
        }
    }

    /// <summary>
    /// Applies control-flow semantics for branch, gate, loop and parallel nodes, skipping untaken sub-graphs and
    /// running the rest. Other nodes only mark disabled output ports as skipped.
    /// <para>按分支、门控、循环与并行节点应用控制流语义：未选中的子图标记为跳过，其余子图继续执行。其他节点仅把被关闭的输出端口标记为跳过。</para>
    /// </summary>
    /// <param name="node">The node whose control flow is applied.<para>需要应用控制流语义的节点。</para></param>
    /// <param name="byId">Nodes indexed by instance ID.<para>按实例 ID 索引的节点。</para></param>
    /// <param name="connections">Connections defining the workflow graph.<para>定义工作流图的连接。</para></param>
    /// <param name="topologicalOrder">Execution order used to build each sub-graph scope.<para>用于构造各子图执行顺序的拓扑序。</para></param>
    /// <param name="flowOut">Control-flow edges keyed by source instance ID.<para>以源实例 ID 为键的控制流边。</para></param>
    /// <param name="token">Cancellation token for the run.<para>本次运行的取消令牌。</para></param>
    /// <param name="handledByFlow">Receives instance IDs covered by a flow sub-graph.<para>接收被子图覆盖的实例 ID。</para></param>
    /// <param name="skippedByBranch">Receives instance IDs skipped as untaken branches.<para>接收因分支未选中而跳过的实例 ID。</para></param>
    /// <param name="skippedByFailure">Receives instance IDs skipped after a failure.<para>接收失败后跳过的实例 ID。</para></param>
    /// <param name="failedNodes">Receives instance IDs of nodes that failed.<para>接收失败节点的实例 ID。</para></param>
    /// <param name="failLock">Serialises writes to the failure collections.<para>为失败集合的写入加锁。</para></param>
    /// <param name="options">Options shared by the scopes this node executes.<para>本节点执行的各作用域共用的选项。</para></param>
    /// <returns><c>true</c> when the downstream scopes were executed here.<para>下游作用域在本方法内执行完毕时为 <c>true</c>。</para></returns>
    private async Task<bool> ApplyFlowControlAsync(
        FlowNodeViewModel node,
        IReadOnlyDictionary<string, FlowNodeViewModel> byId,
        IReadOnlyList<NodeConnection>? connections,
        List<FlowNodeViewModel> topologicalOrder,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flowOut,
        CancellationToken token,
        HashSet<string> handledByFlow,
        HashSet<string> skippedByBranch,
        HashSet<string> skippedByFailure,
        List<string> failedNodes,
        object failLock,
        NodeExecutionOptions options)
    {
        var flowKind = node.FlowKind;
        if (flowKind == NodeFlowKind.None)
        {
            HashSet<FlowNodeViewModel> inactive = [];
            HashSet<FlowNodeViewModel> active = [];
            foreach (var output in node.Output.Where(port => port.PortType == NodePortType.Flow))
            {
                var reachable = CollectFlowReachable(node, output.Title, flowOut);
                if (output.Data is false)
                {
                    inactive.UnionWith(reachable);
                }
                else
                {
                    active.UnionWith(reachable);
                }
            }
            inactive.ExceptWith(active);
            foreach (var target in inactive)
            {
                skippedByBranch.Add(target.InstanceId);
            }
        }
        switch (flowKind)
        {
            case NodeFlowKind.Branch:
                {
                    var trueConnector = FlowOutput(node, NodeFlowRole.True);
                    var falseConnector = FlowOutput(node, NodeFlowRole.False);
                    var trueTitle = trueConnector?.Title ?? string.Empty;
                    var falseTitle = falseConnector?.Title ?? string.Empty;
                    var condition = NodeDataUtil.ToBool(trueConnector?.Data, false);
                    var untakenTitle = condition ? falseTitle : trueTitle;
                    var untaken = CollectFlowReachable(node, untakenTitle, flowOut);
                    var takenTitle = condition ? trueTitle : falseTitle;
                    var taken = CollectFlowReachable(node, takenTitle, flowOut);
                    untaken.ExceptWith(taken);
                    foreach (var target in untaken) skippedByBranch.Add(target.InstanceId);
                    if (untaken.Count > 0)
                    {
                        _log.Write(LogLevel.Info, "Engine",
                            $"Branch '{node.InstanceId}' took '{(condition ? trueTitle : falseTitle)}', skipping {untaken.Count} downstream node(s)");
                    }
                    break;
                }
            case NodeFlowKind.Loop:
                {
                    var requested = NodeDataUtil.ToInt(node.ReadInput("Count"), 0);
                    var iterations = Math.Clamp(requested, 0, MaxLoopIterations);
                    if (requested > MaxLoopIterations)
                    {
                        _log.Write(LogLevel.Warn, "Engine",
                            $"Loop '{node.InstanceId}' requested {requested} iterations, clamped to {MaxLoopIterations}");
                    }
                    var bodyTitle = FlowOutput(node, NodeFlowRole.Body)?.Title ?? string.Empty;
                    var completedTitle = FlowOutput(node, NodeFlowRole.Completed)?.Title ?? string.Empty;
                    var body = CollectFlowReachable(node, bodyTitle, flowOut);
                    var completed = CollectFlowReachable(node, completedTitle, flowOut);
                    body.ExceptWith(completed);
                    if (body.Count == 0) break;
                    if (iterations == 0)
                    {
                        foreach (var bodyNode in body)
                        {
                            skippedByBranch.Add(bodyNode.InstanceId);
                        }
                        break;
                    }

                    var bodyOrder = topologicalOrder.Where(body.Contains).ToList();
                    _log.Write(LogLevel.Info, "Engine",
                        $"Loop '{node.InstanceId}' iterating body of {bodyOrder.Count} node(s) for {iterations} iteration(s)");

                    for (var i = 0; i < iterations; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        node.WriteOutput("Index", i);
                        _log.Write(LogLevel.Debug, "Engine", $"Loop '{node.InstanceId}' iteration {i + 1}/{iterations}");
                        var scope = await ExecuteScopeAsync(bodyOrder, byId, connections, flowOut,
                            token, skippedByFailure, options).ConfigureAwait(false);
                        failedNodes.AddRange(scope.FailedNodes);
                        skippedByFailure.UnionWith(scope.SkippedByFailure);
                        if (scope.Stop)
                        {
                            return true;
                        }
                    }

                    foreach (var bodyNode in body) handledByFlow.Add(bodyNode.InstanceId);
                    break;
                }
            case NodeFlowKind.Parallel:
                {
                    var branchConnectors = node.Output.Where(c => c.PortType == NodePortType.Flow).ToList();
                    if (branchConnectors.Count == 0) break;

                    List<(string Connector, HashSet<FlowNodeViewModel> Nodes)> branches = [];
                    foreach (var connector in branchConnectors)
                    {
                        var branch = CollectFlowReachable(node, connector.Title, flowOut);
                        if (branch.Count > 0)
                            branches.Add((connector.Title, branch));
                    }

                    var ownership = branches
                        .SelectMany(branch => branch.Nodes.Select(branchNode => (branchNode, branch.Connector)))
                        .GroupBy(item => item.branchNode.InstanceId)
                        .ToDictionary(group => group.Key, group => group.Count());
                    List<Task<ScopeExecutionResult>> tasks = [];
                    var stop = 0;
                    foreach (var (connectorTitle, allBranchNodes) in branches)
                    {
                        var branch = allBranchNodes.Where(branchNode => ownership[branchNode.InstanceId] == 1).ToHashSet();
                        var branchOrder = topologicalOrder.Where(branch.Contains).ToList();
                        foreach (var branchNode in branch)
                        {
                            handledByFlow.Add(branchNode.InstanceId);
                        }
                        _log.Write(LogLevel.Info, "Engine",
                            $"Parallel '{node.InstanceId}' dispatching branch '{connectorTitle}' with {branchOrder.Count} node(s)");
                        var inheritedFailures = new HashSet<string>(skippedByFailure);
                        tasks.Add(Task.Run(() => ExecuteScopeAsync(branchOrder, byId, connections, flowOut,
                            token, inheritedFailures, options, () => Volatile.Read(ref stop) != 0,
                            () => Interlocked.Exchange(ref stop, 1)), token));
                    }
                    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                    foreach (var result in results)
                    {
                        failedNodes.AddRange(result.FailedNodes);
                        skippedByFailure.UnionWith(result.SkippedByFailure);
                    }
                    if (results.Any(result => result.Stop))
                    {
                        return true;
                    }
                    break;
                }
        }

        return false;
    }

    private sealed record ScopeExecutionResult(List<string> FailedNodes, HashSet<string> SkippedByFailure, bool Stop);

    private async Task<ScopeExecutionResult> ExecuteScopeAsync(List<FlowNodeViewModel> order,
        IReadOnlyDictionary<string, FlowNodeViewModel> byId, IReadOnlyList<NodeConnection>? connections,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flowOut,
        CancellationToken token, HashSet<string> inheritedFailures, NodeExecutionOptions options,
        Func<bool>? shouldStop = null, Action? stopOthers = null)
    {
        var members = order.ToHashSet();
        var scopedFlow = flowOut.ToDictionary(pair => pair.Key,
            pair => pair.Value.Where(edge => members.Contains(edge.Target)).ToList());
        HashSet<string> handled = [];
        HashSet<string> skipped = [];
        var failures = new HashSet<string>(inheritedFailures);
        List<string> failed = [];
        var scopeLock = new object();
        foreach (var node in order)
        {
            foreach (var input in node.Input)
            {
                input.IsRuntimeSensitive = false;
                input.Data = input.InitialValue;
            }
            foreach (var output in node.Output)
            {
                output.IsRuntimeSensitive = false;
                output.Data = null;
            }
        }
        foreach (var node in order)
        {
            token.ThrowIfCancellationRequested();
            if (shouldStop?.Invoke() == true)
            {
                return new ScopeExecutionResult(failed, failures, true);
            }
            if (handled.Contains(node.InstanceId))
            {
                continue;
            }
            if (skipped.Contains(node.InstanceId) || failures.Contains(node.InstanceId))
            {
                node.ExecutionStatus = NodeExecutionStatus.Skipped;
                NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                RecordSkippedComposite(node, skipped.Contains(node.InstanceId)
                    ? CompositeSkipReason.Condition : CompositeSkipReason.UpstreamFailure);
                continue;
            }
            var succeeded = await ExecuteSingleNodeAsync(node, byId, connections, token,
                progress => SafeReportProgress(0, 1, progress)).ConfigureAwait(false);
            if (!succeeded)
            {
                failed.Add(node.InstanceId);
                if (options.FailurePolicy == ExecutionFailurePolicy.SkipDependents)
                {
                    MarkDownstreamSkipped(node, byId, connections, failures);
                }
                if (options.FailurePolicy == ExecutionFailurePolicy.FailFast)
                {
                    stopOthers?.Invoke();
                    return new ScopeExecutionResult(failed, failures, true);
                }
            }
            else if (await ApplyFlowControlAsync(node, byId, connections, order, scopedFlow, token,
                         handled, skipped, failures, failed, scopeLock, options).ConfigureAwait(false))
            {
                stopOthers?.Invoke();
                return new ScopeExecutionResult(failed, failures, true);
            }
        }
        return new ScopeExecutionResult(failed, failures, false);
    }

    private static HashSet<string> GetInactiveEntryNodes(IReadOnlyDictionary<string, bool>? entries,
        IReadOnlyList<NodeConnection>? connections)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }
        var outgoing = (connections ?? []).GroupBy(edge => edge.SourceInstanceId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetInstanceId).ToArray());
        HashSet<string> Reach(bool active)
        {
            HashSet<string> result = [];
            var pending = new Queue<string>(entries.Where(entry => entry.Value == active).Select(entry => entry.Key));
            while (pending.TryDequeue(out var current))
            {
                if (!result.Add(current))
                {
                    continue;
                }
                if (outgoing.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets)
                    {
                        pending.Enqueue(target);
                    }
                }
            }
            return result;
        }
        var skipped = Reach(false);
        skipped.ExceptWith(Reach(true));
        return skipped;
    }

    private void MarkNodeSkipped(FlowNodeViewModel node, string reason)
    {
        RecordSkippedComposite(node, CompositeSkipReason.UpstreamFailure);
        node.ExecutionStatus = NodeExecutionStatus.Skipped;
        NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
        _log.Write(LogLevel.Debug, "Engine", $"Node '{node.InstanceId}' skipped because {reason}");
    }

    private void RecordSkippedComposite(FlowNodeViewModel node, CompositeSkipReason reason)
    {
        if (node is CompositeNodeViewModel composite)
        {
            _patches?.RecordSkippedComposite(composite, reason);
        }
    }

    private void RecordUnreachedComposites(IReadOnlyList<FlowNodeViewModel> nodes, CompositeSkipReason reason)
    {
        foreach (var node in nodes)
        {
            if (node is CompositeNodeViewModel && node.ExecutionStatus is NodeExecutionStatus.Idle or NodeExecutionStatus.Pending)
            {
                RecordSkippedComposite(node, reason);
            }
        }
    }

    private ExecutionResult RejectPreflight(IReadOnlyList<FlowNodeViewModel> nodes, string error)
    {
        RecordUnreachedComposites(nodes, CompositeSkipReason.PreflightRejected);
        var result = ExecutionResult.Failed(error);
        result.CompositeExecutions = _patches!.CompositeExecutions;
        result.CompositeReportTruncated = _patches.CompositeReportTruncated;
        return result;
    }

    /// <summary>
    /// Collects the downstream nodes reachable via control-flow edges from the given Flow output port of the start
    /// node (breadth-first, excluding the start node itself).
    /// <para>从指定节点的某个 Flow 输出端口出发，沿控制流边广度优先收集可达的下游节点集合（不含起始节点本身）。</para>
    /// </summary>
    /// <param name="start">The node the traversal begins from.<para>遍历的起始节点。</para></param>
    /// <param name="outputConnector">Title of the output port the edges start at.<para>边所出自的输出端口标题。</para></param>
    /// <param name="flowOut">Control-flow edges keyed by source instance ID.<para>以源实例 ID 为键的控制流边。</para></param>
    /// <returns>The reachable downstream nodes.<para>可达的下游节点集合。</para></returns>
    /// <summary>
    /// Finds the flow output carrying the given role, or <see langword="null"/> when the node declares none.
    /// <para>按角色查找流输出端口；节点未声明该角色时为 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="node">Node whose outputs are inspected.<para>要检查输出的节点。</para></param>
    /// <param name="role">Requested role.<para>请求的角色。</para></param>
    /// <returns>The matching output port.<para>匹配的输出端口。</para></returns>
    private static NodeConnectorViewModel? FlowOutput(FlowNodeViewModel node, NodeFlowRole role)
        => node.Output.FirstOrDefault(port => port.PortType == NodePortType.Flow && port.FlowRole == role);

    private static HashSet<FlowNodeViewModel> CollectFlowReachable(
        FlowNodeViewModel start, string outputConnector,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flowOut)
    {
        HashSet<FlowNodeViewModel> visited = [];
        if (!flowOut.TryGetValue(start.InstanceId, out var edges)) return visited;

        var queue = new Queue<FlowNodeViewModel>();
        foreach (var (connector, target) in edges)
        {
            if (!string.Equals(connector, outputConnector, StringComparison.Ordinal)) continue;
            if (ReferenceEquals(target, start) || visited.Add(target)) queue.Enqueue(target);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!flowOut.TryGetValue(current.InstanceId, out var outgoing)) continue;
            foreach (var (_, target) in outgoing)
            {
                if (!ReferenceEquals(target, start) && visited.Add(target)) queue.Enqueue(target);
            }
        }
        return visited;
    }

    private static void MarkDownstreamSkipped(
        FlowNodeViewModel failed,
        IReadOnlyDictionary<string, FlowNodeViewModel> byId,
        IReadOnlyList<NodeConnection>? connections,
        HashSet<string> skipped)
    {
        if (connections is null) return;

        var outgoing = connections
            .Where(connection => connection.SourceInstanceId == failed.InstanceId)
            .Select(connection => connection.TargetInstanceId)
            .Where(byId.ContainsKey)
            .ToArray();
        var queue = new Queue<string>(outgoing);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!skipped.Add(current)) continue;
            foreach (var next in connections
                .Where(connection => connection.SourceInstanceId == current)
                .Select(connection => connection.TargetInstanceId)
                .Where(byId.ContainsKey))
            {
                queue.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// Requests execution stop.
    /// <para>请求停止执行。</para>
    /// </summary>
    public void Stop()
    {
        _log.Write(LogLevel.Warn, "Engine", "Stop requested");
        _cts?.Cancel();
    }

    /// <summary>
    /// Copies the upstream output port values connected to the target node's inputs into the corresponding input ports,
    /// establishing data flow between nodes.
    /// <para>把连接到 <paramref name="target"/> 节点输入端口的上游输出端口值复制到对应输入端口，建立节点间数据流。</para>
    /// </summary>
    /// <param name="target">The target node about to execute.<para>将要执行的目标节点。</para></param>
    /// <param name="byId">Mapping from node instance ID to the node it identifies.<para>节点实例 ID 到对应节点的映射。</para></param>
    /// <param name="connections">Connection list (source → target).<para>连接列表（源→目标）。</para></param>
    private static void FlowInputs(FlowNodeViewModel target, IReadOnlyDictionary<string, FlowNodeViewModel> byId, IReadOnlyList<NodeConnection>? connections)
    {
        foreach (var input in target.Input)
        {
            input.HasRuntimeSource = false;
            if (input is { Data: null } && input.InitialValue is not null)
            {
                input.Data = input.InitialValue;
            }
        }

        if (connections == null) return;

        foreach (var conn in connections)
        {
            if (conn.TargetInstanceId != target.InstanceId) continue;
            if (!byId.TryGetValue(conn.SourceInstanceId, out var sourceNode)) continue;
            var sourceConnector = WorkflowGraphValidation.ResolvePort(sourceNode.Output, conn.SourcePortId, conn.SourceConnector);
            var targetConnector = WorkflowGraphValidation.ResolvePort(target.Input, conn.TargetPortId, conn.TargetConnector);
            if (sourceConnector != null && targetConnector != null
                && (sourceConnector.Data is not null || targetConnector.PortType != NodePortType.Flow))
            {
                targetConnector.IsRuntimeSensitive |= sourceConnector.IsSensitive || sourceConnector.IsRuntimeSensitive;
                targetConnector.HasRuntimeSource = true;
                targetConnector.Data = sourceConnector.Data;
            }
        }
    }

    /// <summary>
    /// Converts the node-reported local progress into overall progress and safely raises <see cref="ProgressChanged"/>.
    /// <para>把节点上报的局部进度换算为整体进度并安全触发 <see cref="ProgressChanged"/>。</para>
    /// </summary>
    /// <param name="completedBefore">Nodes already finished before the current one.<para>当前节点之前已完成的节点数。</para></param>
    /// <param name="total">Total nodes in the run.<para>本次运行的节点总数。</para></param>
    /// <param name="localProgress">Fraction of the current node completed.<para>当前节点已完成的占比。</para></param>
    private void SafeReportProgress(int completedBefore, int total, double localProgress)
    {
        var clamped = Math.Clamp(localProgress, 0.0, 1.0);
        var overall = (completedBefore + clamped) / Math.Max(1, total);
        ProgressChanged?.Invoke(this, Math.Clamp(overall, 0.0, 1.0));
    }

    /// <summary>
    /// Kahn's topological sort of the workflow graph.
    /// <para>对工作流图做 Kahn 拓扑排序。</para>
    /// </summary>
    /// <param name="nodes">The nodes to order.<para>需要排序的节点。</para></param>
    /// <param name="connections">Edges contributing to each node's in-degree.<para>计入各节点入度的连接。</para></param>
    /// <returns>The execution order, or <c>null</c> when the graph contains a cycle.<para>执行顺序；图中存在环时为 <c>null</c>。</para></returns>
    private static List<FlowNodeViewModel>? TopologicalSort(IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnection>? connections)
    {
        var inDegree = nodes.ToDictionary(n => n.InstanceId, _ => 0);
        var adj = nodes.ToDictionary(n => n.InstanceId, _ => new List<FlowNodeViewModel>());
        var byId = nodes.ToDictionary(n => n.InstanceId);

        if (connections != null)
        {
            foreach (var conn in connections)
            {
                if (conn.SourceInstanceId == null || conn.TargetInstanceId == null) continue;
                if (!byId.ContainsKey(conn.SourceInstanceId) || !byId.ContainsKey(conn.TargetInstanceId)) continue;

                adj[conn.SourceInstanceId].Add(byId[conn.TargetInstanceId]);
                inDegree[conn.TargetInstanceId]++;
            }
        }

        var queue = new Queue<FlowNodeViewModel>();
        foreach (var n in nodes)
        {
            if (inDegree[n.InstanceId] == 0) queue.Enqueue(n);
        }

        var result = new List<FlowNodeViewModel>(nodes.Count);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            result.Add(cur);
            foreach (var next in adj[cur.InstanceId])
            {
                inDegree[next.InstanceId]--;
                if (inDegree[next.InstanceId] == 0) queue.Enqueue(next);
            }
        }

        return result.Count == nodes.Count ? result : null;
    }
}

/// <summary>
/// Node connection representation (source instance+port → target instance+port).
/// <para>节点连接表示（源实例+端口 → 目标实例+端口）。</para>
/// </summary>
public sealed class NodeConnection
{
    /// <summary>
    /// Source node instance ID.
    /// <para>源节点实例 ID。</para>
    /// </summary>
    /// <value>The instance ID of the connecting source node.<para>发起连接的源节点实例 ID。</para></value>
    public required string SourceInstanceId { get; init; }

    /// <summary>
    /// Source connector title.
    /// <para>源连接器标题。</para>
    /// </summary>
    /// <value>The title of the source connector.<para>源连接器的标题。</para></value>
    public required string SourceConnector { get; init; }

    /// <summary>
    /// Stable source port identity; title resolution applies only when it is absent.
    /// <para>稳定的源端口标识；缺失时才按标题解析。</para>
    /// </summary>
    /// <value>The source port identity, or <c>null</c> when only a title is known.<para>源端口标识；仅知道标题时为 <c>null</c>。</para></value>
    public string? SourcePortId { get; init; }

    /// <summary>
    /// Target node instance ID.
    /// <para>目标节点实例 ID。</para>
    /// </summary>
    /// <value>The instance ID of the connecting target node.<para>被连接的目标节点实例 ID。</para></value>
    public required string TargetInstanceId { get; init; }

    /// <summary>
    /// Target connector title.
    /// <para>目标连接器标题。</para>
    /// </summary>
    /// <value>The title of the target connector.<para>目标连接器的标题。</para></value>
    public required string TargetConnector { get; init; }

    /// <summary>
    /// Stable target port identity; title resolution applies only when it is absent.
    /// <para>稳定的目标端口标识；缺失时才按标题解析。</para>
    /// </summary>
    /// <value>The target port identity, or <c>null</c> when only a title is known.<para>目标端口标识；仅知道标题时为 <c>null</c>。</para></value>
    public string? TargetPortId { get; init; }
}

/// <summary>
/// Execution result.
/// <para>执行结果。</para>
/// </summary>
public sealed class ExecutionResult
{
    /// <summary>
    /// Composite invocation evidence; recorded success does not imply file publication.
    /// <para>组合节点调用记录；其中记录的成功并不表示文件已经发布。</para>
    /// </summary>
    /// <value>One entry per composite invocation of the run.<para>本次运行中每次组合节点调用的一条记录。</para></value>
    public IReadOnlyList<PatchCompositeExecution> CompositeExecutions { get; internal set; } = [];

    /// <summary>
    /// Whether skipped composite rows were left out because of the report capacity.
    /// <para>是否因报告容量上限而省略了被跳过的组合记录行。</para>
    /// </summary>
    /// <value><c>true</c> when composite rows were left out.<para>存在被省略的组合记录行时为 <c>true</c>。</para></value>
    public bool CompositeReportTruncated { get; internal set; }

    /// <summary>
    /// Patch-owned file effects, including previewed or discarded changes.
    /// <para>由补丁持有的文件影响，包含仅预览或已丢弃的改动。</para>
    /// </summary>
    /// <value>The file effects attributed to the patch run.<para>归属本次补丁运行的文件影响。</para></value>
    public IReadOnlyList<PatchChange> PatchChanges { get; internal set; } = [];

    /// <summary>
    /// Publication outcome, independent of the effects made outside the patch run.
    /// <para>发布结果，与补丁运行之外的影响相互独立。</para>
    /// </summary>
    /// <value>The outcome of the publication step.<para>发布步骤的结果。</para></value>
    public PatchRunOutcome PatchOutcome { get; internal set; }
    /// <summary>
    /// Execution status.
    /// <para>执行状态。</para>
    /// </summary>
    /// <value>The terminal status of the run.<para>整次运行的最终状态。</para></value>
    public ExecutionStatus Status { get; init; }

    /// <summary>
    /// Number of executed nodes.
    /// <para>执行节点数。</para>
    /// </summary>
    /// <value>How many nodes were executed.<para>已执行的节点数量。</para></value>
    public int ExecutedCount { get; init; }

    /// <summary>
    /// List of failed node instance IDs.
    /// <para>失败节点实例 ID 列表。</para>
    /// </summary>
    /// <value>The instance IDs of the nodes that failed.<para>执行失败节点的实例 ID。</para></value>
    public IReadOnlyList<string> FailedNodes { get; init; } = [];

    /// <summary>
    /// Error message.
    /// <para>错误信息。</para>
    /// </summary>
    /// <value>The failure reason, or <c>null</c> when the run did not fail.<para>失败原因；运行未失败时为 <c>null</c>。</para></value>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a succeeded result.
    /// <para>创建成功结果。</para>
    /// </summary>
    /// <param name="count">Number of nodes that were executed.<para>已执行的节点数量。</para></param>
    /// <returns>A result carrying the succeeded status and the executed count.<para>带有成功状态与已执行数量的结果。</para></returns>
    public static ExecutionResult Succeeded(int count) => new() { Status = ExecutionStatus.Succeeded, ExecutedCount = count };

    /// <summary>
    /// Creates a failed result.
    /// <para>创建失败结果。</para>
    /// </summary>
    /// <param name="error">The failure reason.<para>失败原因。</para></param>
    /// <param name="failed">Instance IDs of the nodes that failed.<para>执行失败节点的实例 ID。</para></param>
    /// <returns>A result carrying the failed status and the supplied details.<para>带有失败状态与所给详情的结果。</para></returns>
    public static ExecutionResult Failed(string error, IReadOnlyList<string>? failed = null) => new()
    {
        Status = ExecutionStatus.Failed,
        ErrorMessage = error,
        FailedNodes = failed ?? []
    };

    /// <summary>
    /// Creates a partially-succeeded result.
    /// <para>创建部分成功结果。</para>
    /// </summary>
    /// <param name="failed">Instance IDs of the nodes that failed.<para>执行失败节点的实例 ID。</para></param>
    /// <returns>A result carrying the partially-succeeded status.<para>带有部分成功状态的结果。</para></returns>
    public static ExecutionResult PartiallySucceeded(IReadOnlyList<string> failed) => new() { Status = ExecutionStatus.PartiallySucceeded, FailedNodes = failed };

    /// <summary>
    /// Creates a cancelled result.
    /// <para>创建取消结果。</para>
    /// </summary>
    /// <returns>A result carrying the cancelled status.<para>带有取消状态的结果。</para></returns>
    public static ExecutionResult Cancelled() => new() { Status = ExecutionStatus.Cancelled };

    /// <summary>
    /// Creates a terminated result.
    /// <para>创建终止结果。</para>
    /// </summary>
    /// <param name="reason">The reason reported by the Terminate node.<para>终止节点上报的原因。</para></param>
    /// <returns>A result carrying the terminated status and the reason.<para>带有终止状态与原因的结果。</para></returns>
    public static ExecutionResult Terminated(string? reason) => new()
    {
        Status = ExecutionStatus.Terminated,
        ErrorMessage = reason
    };
}

/// <summary>
/// Execution status enum.
/// <para>执行状态枚举。</para>
/// </summary>
public enum ExecutionStatus
{
    /// <summary>Not executed. <para>未执行。</para></summary>
    Idle,

    /// <summary>Succeeded. <para>成功。</para></summary>
    Succeeded,

    /// <summary>Partially succeeded. <para>部分成功。</para></summary>
    PartiallySucceeded,

    /// <summary>Failed. <para>失败。</para></summary>
    Failed,

    /// <summary>The workflow stopped because a Terminate node requested it, which is distinct from user cancellation. <para>工作流因终止节点请求而停止，与用户取消相区分。</para></summary>
    Terminated,

    /// <summary>Cancelled by the caller. <para>被调用方取消。</para></summary>
    Cancelled
}

/// <summary>
/// Node status change event args.
/// <para>节点状态变更事件参数。</para>
/// </summary>
public sealed class NodeStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// Node whose status changed.
    /// <para>状态变更的节点。</para>
    /// </summary>
    /// <value>The node the status change refers to.<para>状态变化所指的节点。</para></value>
    public FlowNodeViewModel Node { get; }

    public NodeStatusChangedEventArgs(FlowNodeViewModel node) => Node = node;
}

/// <summary>
/// Execution completed event args.
/// <para>执行完成事件参数。</para>
/// </summary>
public sealed class ExecutionCompletedEventArgs : EventArgs
{
    /// <summary>
    /// Execution result.
    /// <para>执行结果。</para>
    /// </summary>
    /// <value>The result of the completed run.<para>已完成运行的结果。</para></value>
    public ExecutionResult Result { get; }

    public ExecutionCompletedEventArgs(ExecutionResult result) => Result = result;
}

/// <summary>
/// Execution context implementation.
/// <para>执行上下文实现。</para>
/// </summary>
internal sealed class ExecutionContextImpl : INodeExecutionContext
{
    private readonly Action<IAsyncDisposable>? _registerResource;
    private readonly Action<bool>? _userWait;
    private readonly IUnifiedLog _log;
    private readonly CancellationToken _token;
    private readonly Action<double> _progress;

    public ExecutionContextImpl(IUnifiedLog log, CancellationToken token, Action<double> progress, PatchRun? patches = null,
        VisualPluginPackage? package = null, IWorkflowInteractionService? interactions = null, Action<bool>? userWait = null,
        Action<IAsyncDisposable>? registerResource = null,
        Workspace.IWorkflowDocuments? documents = null)
    {
        _log = log;
        _token = token;
        _progress = progress;
        Patches = patches;
        Package = package;
        Interactions = interactions ?? UnsupportedWorkflowInteractionService.Instance;
        _userWait = userWait;
        _registerResource = registerResource;
        Documents = documents;
    }

    public IUnifiedLog Log => _log;
    public Workspace.IWorkflowDocuments? Documents { get; }
    public CancellationToken CancellationToken => _token;
    public PatchRun? Patches { get; }
    public VisualPluginPackage? Package { get; }
    public IWorkflowInteractionService Interactions { get; }

    public void RegisterResource(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_registerResource is null)
        {
            throw new NotSupportedException("The execution context does not own runtime resources.");
        }
        _registerResource(resource);
    }

    public IDisposable BeginUserInteractionWait()
    {
        _userWait?.Invoke(true);
        return new InteractionWaitScope(() => _userWait?.Invoke(false));
    }

    public void ReportProgress(double progress)
    {
        if (progress < 0) progress = 0;
        else if (progress > 1) progress = 1;
        _progress?.Invoke(progress);
    }
}