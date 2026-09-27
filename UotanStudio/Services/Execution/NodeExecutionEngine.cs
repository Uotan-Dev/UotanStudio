using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Execution;

/// <summary>
/// <para>节点工作流执行引擎。基于拓扑排序（Kahn 算法）调度节点，支持异步执行、取消、状态机。</para>
/// Node workflow execution engine. Schedules nodes via topological sort (Kahn's algorithm); supports async execution, cancellation, and a state machine.
/// </summary>
public sealed class NodeExecutionEngine
{
    /// <summary>
    /// <para>循环节点单次执行的最大迭代数上限，防止错误配置的死循环工作流无限执行。</para>
    /// Maximum iterations per Loop node execution, guarding misconfigured looping workflows from running forever.
    /// </summary>
    public const int MaxLoopIterations = 10_000;

    private readonly IUnifiedLog _log;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private PatchRun? _patches;
    private VisualPluginPackage? _package;
    private IWorkflowInteractionService _interactions = UnsupportedWorkflowInteractionService.Instance;
    private readonly System.Collections.Concurrent.ConcurrentStack<IAsyncDisposable> _resources = new();

    /// <summary>
    /// <para>创建执行引擎。</para>
    /// Creates the execution engine.
    /// </summary>
    /// <param name="log">
    /// <para>统一日志服务。</para>
    /// Unified log service.
    /// </param>
    public NodeExecutionEngine(IUnifiedLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// <para>当前是否正在执行。</para>
    /// Whether execution is in progress.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// <para>执行进度变更事件（0.0 ~ 1.0）。</para>
    /// Progress change event (0.0 ~ 1.0).
    /// </summary>
    public event EventHandler<double>? ProgressChanged;

    /// <summary>
    /// <para>节点状态变更事件。</para>
    /// Node status change event.
    /// </summary>
    public event EventHandler<NodeStatusChangedEventArgs>? NodeStatusChanged;

    /// <summary>
    /// <para>执行完成事件（成功或失败）。</para>
    /// Execution completed event (success or failure).
    /// </summary>
    public event EventHandler<ExecutionCompletedEventArgs>? ExecutionCompleted;

    /// <summary>
    /// <para>异步执行给定节点和连接构成的工作流。</para>
    /// Asynchronously executes the workflow formed by the given nodes and connections.
    /// </summary>
    /// <param name="nodes">
    /// <para>节点视图模型列表。</para>
    /// List of node view models.
    /// </param>
    /// <param name="connections">
    /// <para>连接列表（源→目标），用于确定执行顺序与数据流。</para>
    /// List of connections (source → target), used to determine execution order and data flow.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// <para>执行结果。</para>
    /// Execution result.
    /// </returns>
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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _isRunning = true;

        try
        {
            // Execution data belongs to one run; edited preconditions must not reuse an earlier result.
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
            var interactionError = WorkflowInteractionPreflight.Validate(nodes, _interactions);
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

            // 拓扑排序
            var order = TopologicalSort(nodes, connections);
            if (order == null)
            {
                _log.Write(LogLevel.Error, "Engine", "Cycle detected in workflow, cannot execute");
                return RejectPreflight(nodes, "Cycle detected in workflow");
            }

            _log.Write(LogLevel.Debug, "Engine", $"Topological order computed: {order.Count} nodes");

            var completed = 0;
            var total = order.Count;
            var failedNodes = new List<string>();
            var failLock = new object();
            var aborted = false;
            var byId = nodes.ToDictionary(n => n.InstanceId);
            var flowOut = BuildFlowEdges(nodes, connections);
            var gateError = PatchGatePolicy.Validate(nodes, connections, flowOut);
            if (gateError is not null)
            {
                return RejectPreflight(nodes, gateError);
            }

            // 已被 Loop / Parallel 子图调度执行过的节点（主循环跳过），与 Branch 未选中分支涉及的节点（标记 Skipped）。
            // Nodes already executed by Loop / Parallel sub-graph scheduling (skipped by the main loop),
            // and nodes belonging to the untaken Branch sub-graph (marked Skipped).
            var handledByFlow = new HashSet<string>();
            var skippedByBranch = GetInactiveEntryNodes(options.EntryActivation, connections);
            var skippedByFailure = new HashSet<string>();

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
                }
            }

            var result = aborted && failedNodes.Count == 0
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
    /// <para>提取控制流边：源连接器端口类型为 <see cref="NodePortType.Flow"/> 的连接。返回节点实例 ID 到 (输出端口标题， 目标节点) 列表的映射。</para>
    /// Extracts control-flow edges: connections whose source connector port type is <see cref="NodePortType.Flow"/>.
    /// Returns a mapping from node instance ID to the list of (output connector title, target node).
    /// </summary>
    private static Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> BuildFlowEdges(
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnection>? connections)
    {
        var map = new Dictionary<string, List<(string, FlowNodeViewModel)>>();
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
    /// <para>执行单个节点：注入数据流、更新状态机并记录失败原因。返回是否执行成功。</para>
    /// Executes a single node: injects data flow, updates the state machine, and records failures. Returns success.
    /// </summary>
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
                }, _resources.Push);
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
    /// <para>按节点类型键应用真实控制流语义：Flow.Branch 把未选中分支的下游控制流子图标记为 Skipped，
    /// Flow.Loop 迭代执行 Body 子图（上限 <see cref="MaxLoopIterations"/>），Flow.Parallel 并发执行各分支子图。</para>
    /// Applies real flow-control semantics by node type key: Flow.Branch marks the untaken branch's downstream
    /// control-flow sub-graph as Skipped, Flow.Loop iterates the Body sub-graph (bounded by
    /// <see cref="MaxLoopIterations"/>), and Flow.Parallel runs the branch sub-graphs concurrently.
    /// </summary>
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
        if (node.TypeKey is not ("Flow.Condition.Branch" or "Flow.Applicability.Gate" or "Flow.Collection.Loop" or "Flow.Execution.Parallel"))
        {
            var inactive = new HashSet<FlowNodeViewModel>();
            var active = new HashSet<FlowNodeViewModel>();
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
        switch (node.TypeKey)
        {
            case "Flow.Condition.Branch":
            case "Flow.Applicability.Gate":
                {
                    var isGate = node.TypeKey == "Flow.Applicability.Gate";
                    var trueTitle = isGate ? "Apply" : "True";
                    var falseTitle = isGate ? "Skip" : "False";
                    var trueConnector = node.Output.FirstOrDefault(c => string.Equals(c.Title, trueTitle, StringComparison.Ordinal));
                    var condition = NodeDataUtil.ToBool(trueConnector?.Data, false);
                    var untakenTitle = condition ? falseTitle : trueTitle;
                    var untaken = CollectFlowReachable(node, untakenTitle, flowOut);
                    var takenTitle = condition ? trueTitle : falseTitle;
                    var taken = CollectFlowReachable(node, takenTitle, flowOut);
                    // A branch join is reachable from both sides. It must remain executable
                    // when the untaken side is skipped, so only the untaken-only set is marked.
                    untaken.ExceptWith(taken);
                    foreach (var target in untaken) skippedByBranch.Add(target.InstanceId);
                    if (untaken.Count > 0)
                    {
                        _log.Write(LogLevel.Info, "Engine",
                            $"Branch '{node.InstanceId}' took '{(condition ? "True" : "False")}', skipping {untaken.Count} downstream node(s)");
                    }
                    break;
                }
            case "Flow.Collection.Loop":
                {
                    var requested = NodeDataUtil.ToInt(node.ReadInput("Count"), 0);
                    var iterations = Math.Clamp(requested, 0, MaxLoopIterations);
                    if (requested > MaxLoopIterations)
                    {
                        _log.Write(LogLevel.Warn, "Engine",
                            $"Loop '{node.InstanceId}' requested {requested} iterations, clamped to {MaxLoopIterations}");
                    }
                    var body = CollectFlowReachable(node, "Body", flowOut);
                    var completed = CollectFlowReachable(node, "Completed", flowOut);
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
            case "Flow.Execution.Parallel":
                {
                    var branchConnectors = node.Output.Where(c => c.PortType == NodePortType.Flow).ToList();
                    if (branchConnectors.Count == 0) break;

                    var branches = new List<(string Connector, HashSet<FlowNodeViewModel> Nodes)>();
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
                    var tasks = new List<Task<ScopeExecutionResult>>();
                    var stop = 0;
                    foreach (var (connectorTitle, allBranchNodes) in branches)
                    {
                        // Shared merge points remain in the parent scope and execute once.
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
        var handled = new HashSet<string>();
        var skipped = new HashSet<string>();
        var failures = new HashSet<string>(inheritedFailures);
        var failed = new List<string>();
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
            var result = new HashSet<string>();
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
    /// <para>从指定节点的某个 Flow 输出端口出发，沿控制流边广度优先收集可达的下游节点集合（不含起始节点本身）。</para>
    /// Collects the downstream nodes reachable via control-flow edges from the given Flow output port of the start
    /// node (breadth-first, excluding the start node itself).
    /// </summary>
    private static HashSet<FlowNodeViewModel> CollectFlowReachable(
        FlowNodeViewModel start, string outputConnector,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flowOut)
    {
        var visited = new HashSet<FlowNodeViewModel>();
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
    /// <para>请求停止执行。</para>
    /// Requests execution stop.
    /// </summary>
    public void Stop()
    {
        _log.Write(LogLevel.Warn, "Engine", "Stop requested");
        _cts?.Cancel();
    }

    /// <summary>
    /// <para>把连接到 <paramref name="target"/> 节点输入端口的上游输出端口值复制到对应输入端口，建立节点间数据流。</para>
    /// Copies the upstream output port values connected to the target node's inputs into the corresponding input ports,
    /// establishing data flow between nodes.
    /// </summary>
    /// <param name="target">
    /// <para>将要执行的目标节点。</para>
    /// The target node about to execute.
    /// </param>
    /// <param name="byId">
    /// <para>节点实例 ID 到节点视图模型的映射。</para>
    /// Mapping from node instance ID to node view model.
    /// </param>
    /// <param name="connections">
    /// <para>连接列表（源→目标）。</para>
    /// Connection list (source → target).
    /// </param>
    private static void FlowInputs(FlowNodeViewModel target, IReadOnlyDictionary<string, FlowNodeViewModel> byId, IReadOnlyList<NodeConnection>? connections)
    {
        // 先把用户为未连接输入端口填写的初始值注入 Data，再让上游连接值覆盖之（若有）。
        // First inject user-set initial values for unconnected inputs, then let connected upstream values override them (when present).
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
            // A connected data output is authoritative even when absent (cancelled or hidden form fields).
            // Flow signals may intentionally have no payload.
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
    /// <para>把节点上报的局部进度换算为整体进度并安全触发 <see cref="ProgressChanged"/>。</para>
    /// Converts the node-reported local progress into overall progress and safely raises <see cref="ProgressChanged"/>.
    /// </summary>
    private void SafeReportProgress(int completedBefore, int total, double localProgress)
    {
        var clamped = Math.Clamp(localProgress, 0.0, 1.0);
        // 本节点进度占 1/total，之前已完成节点占 completedBefore/total
        var overall = (completedBefore + clamped) / Math.Max(1, total);
        ProgressChanged?.Invoke(this, Math.Clamp(overall, 0.0, 1.0));
    }

    /// <summary>
    /// <para>Kahn 拓扑排序。返回 <c>null</c> 表示存在环。</para>
    /// Kahn's topological sort. Returns <c>null</c> if a cycle exists.
    /// </summary>
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
/// <para>节点连接表示（源实例+端口 → 目标实例+端口）。</para>
/// Node connection representation (source instance+port → target instance+port).
/// </summary>
public sealed class NodeConnection
{
    /// <summary>
    /// <para>源节点实例 ID。</para>
    /// Source node instance ID.
    /// </summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>
    /// <para>源连接器标题。</para>
    /// Source connector title.
    /// </summary>
    public required string SourceConnector { get; init; }

    /// <summary>Stable source port identity; title resolution is used only when absent.</summary>
    public string? SourcePortId { get; init; }

    /// <summary>
    /// <para>目标节点实例 ID。</para>
    /// Target node instance ID.
    /// </summary>
    public required string TargetInstanceId { get; init; }

    /// <summary>
    /// <para>目标连接器标题。</para>
    /// Target connector title.
    /// </summary>
    public required string TargetConnector { get; init; }

    /// <summary>Stable target port identity; title resolution is used only when absent.</summary>
    public string? TargetPortId { get; init; }
}

/// <summary>
/// <para>执行结果。</para>
/// Execution result.
/// </summary>
public sealed class ExecutionResult
{
    /// <summary>Composite invocation evidence; success here does not imply file publication.</summary>
    public IReadOnlyList<PatchCompositeExecution> CompositeExecutions { get; internal set; } = [];

    /// <summary>Whether skipped composite rows were omitted due to the report capacity.</summary>
    public bool CompositeReportTruncated { get; internal set; }

    /// <summary>Patch-owned file effects, including previewed or discarded changes.</summary>
    public IReadOnlyList<PatchChange> PatchChanges { get; internal set; } = [];

    /// <summary>Publication outcome, independent of non-patch workflow effects.</summary>
    public PatchRunOutcome PatchOutcome { get; internal set; }
    /// <summary>
    /// <para>执行状态。</para>
    /// Execution status.
    /// </summary>
    public ExecutionStatus Status { get; init; }

    /// <summary>
    /// <para>执行节点数。</para>
    /// Number of executed nodes.
    /// </summary>
    public int ExecutedCount { get; init; }

    /// <summary>
    /// <para>失败节点实例 ID 列表。</para>
    /// List of failed node instance IDs.
    /// </summary>
    public IReadOnlyList<string> FailedNodes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// <para>错误信息。</para>
    /// Error message.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// <para>创建成功结果。</para>
    /// Creates a succeeded result.
    /// </summary>
    public static ExecutionResult Succeeded(int count) => new() { Status = ExecutionStatus.Succeeded, ExecutedCount = count };

    /// <summary>
    /// <para>创建失败结果。</para>
    /// Creates a failed result.
    /// </summary>
    public static ExecutionResult Failed(string error, IReadOnlyList<string>? failed = null) => new()
    {
        Status = ExecutionStatus.Failed,
        ErrorMessage = error,
        FailedNodes = failed ?? Array.Empty<string>()
    };

    /// <summary>
    /// <para>创建部分成功结果。</para>
    /// Creates a partially-succeeded result.
    /// </summary>
    public static ExecutionResult PartiallySucceeded(IReadOnlyList<string> failed) => new() { Status = ExecutionStatus.PartiallySucceeded, FailedNodes = failed };

    /// <summary>
    /// <para>创建取消结果。</para>
    /// Creates a cancelled result.
    /// </summary>
    public static ExecutionResult Cancelled() => new() { Status = ExecutionStatus.Cancelled };
}

/// <summary>
/// <para>执行状态枚举。</para>
/// Execution status enum.
/// </summary>
public enum ExecutionStatus
{
    /// <summary><para>未执行。</para>Not executed.</summary>
    Idle,

    /// <summary><para>成功。</para>Succeeded.</summary>
    Succeeded,

    /// <summary><para>部分成功。</para>Partially succeeded.</summary>
    PartiallySucceeded,

    /// <summary><para>失败。</para>Failed.</summary>
    Failed,

    /// <summary><para>取消。</para>Cancelled.</summary>
    Cancelled
}

/// <summary>
/// <para>节点状态变更事件参数。</para>
/// Node status change event args.
/// </summary>
public sealed class NodeStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// <para>状态变更的节点。</para>
    /// Node whose status changed.
    /// </summary>
    public FlowNodeViewModel Node { get; }

    public NodeStatusChangedEventArgs(FlowNodeViewModel node) => Node = node;
}

/// <summary>
/// <para>执行完成事件参数。</para>
/// Execution completed event args.
/// </summary>
public sealed class ExecutionCompletedEventArgs : EventArgs
{
    /// <summary>
    /// <para>执行结果。</para>
    /// Execution result.
    /// </summary>
    public ExecutionResult Result { get; }

    public ExecutionCompletedEventArgs(ExecutionResult result) => Result = result;
}

/// <summary>
/// <para>执行上下文实现。</para>
/// Execution context implementation.
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
        Action<IAsyncDisposable>? registerResource = null)
    {
        _log = log;
        _token = token;
        _progress = progress;
        Patches = patches;
        Package = package;
        Interactions = interactions ?? UnsupportedWorkflowInteractionService.Instance;
        _userWait = userWait;
        _registerResource = registerResource;
    }

    public IUnifiedLog Log => _log;
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