using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Logging;
using UotanStudio.Abstractions.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Execution
{
    /// <summary>
    /// <para>节点工作流执行引擎。基于拓扑排序（Kahn 算法）调度节点，支持异步执行、取消、状态机。</para>
    /// Node workflow execution engine. Schedules nodes via topological sort (Kahn's algorithm); supports async execution, cancellation, and a state machine.
    /// </summary>
    public sealed class NodeExecutionEngine
    {
        private readonly IUnifiedLog _log;
        private CancellationTokenSource? _cts;
        private volatile bool _isRunning;

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
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(nodes);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _isRunning = true;

            try
            {
                _log.Write(LogLevel.Info, "Engine", $"Starting execution with {nodes.Count} nodes, {connections?.Count ?? 0} connections");

                // 拓扑排序
                var order = TopologicalSort(nodes, connections);
                if (order == null)
                {
                    _log.Write(LogLevel.Error, "Engine", "Cycle detected in workflow, cannot execute");
                    return ExecutionResult.Failed("Cycle detected in workflow");
                }

                _log.Write(LogLevel.Debug, "Engine", $"Topological order computed: {order.Count} nodes");

                var completed = 0;
                var total = order.Count;
                var failedNodes = new List<string>();
                var aborted = false;

                foreach (var node in order)
                {
                    if (token.IsCancellationRequested)
                    {
                        _log.Write(LogLevel.Warn, "Engine", "Execution cancelled by user");
                        aborted = true;
                        break;
                    }

                    // 设置 Running
                    node.ExecutionStatus = NodeExecutionStatus.Running;
                    NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));

                    try
                    {
                        // 简单执行：记录节点标题，模拟执行延迟
                        _log.Write(LogLevel.Info, "Engine", $"Executing node '{node.Title}' (id={node.InstanceId})");
                        await Task.Delay(50, token).ConfigureAwait(false);

                        if (token.IsCancellationRequested)
                        {
                            node.ExecutionStatus = NodeExecutionStatus.Cancelled;
                            NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                            aborted = true;
                            break;
                        }

                        node.ExecutionStatus = NodeExecutionStatus.Succeeded;
                        NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                        _log.Write(LogLevel.Debug, "Engine", $"Node '{node.InstanceId}' completed");
                    }
                    catch (OperationCanceledException)
                    {
                        node.ExecutionStatus = NodeExecutionStatus.Cancelled;
                        NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                        aborted = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        node.ExecutionStatus = NodeExecutionStatus.Failed;
                        NodeStatusChanged?.Invoke(this, new NodeStatusChangedEventArgs(node));
                        _log.Write(LogLevel.Error, "Engine", $"Node '{node.InstanceId}' failed: {ex.Message}");
                        failedNodes.Add(node.InstanceId);
                    }

                    completed++;
                    var progress = (double)completed / Math.Max(1, total);
                    ProgressChanged?.Invoke(this, progress);
                }

                var result = aborted
                    ? ExecutionResult.Cancelled()
                    : failedNodes.Count > 0
                        ? ExecutionResult.PartiallySucceeded(failedNodes)
                        : ExecutionResult.Succeeded(total);

                _log.Write(aborted ? LogLevel.Warn : LogLevel.Info, "Engine", aborted ? "Execution aborted" : $"Execution completed: {result.Status}");
                ExecutionCompleted?.Invoke(this, new ExecutionCompletedEventArgs(result));
                return result;
            }
            finally
            {
                _isRunning = false;
                _cts?.Dispose();
                _cts = null;
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
    }

    /// <summary>
    /// <para>执行结果。</para>
    /// Execution result.
    /// </summary>
    public sealed class ExecutionResult
    {
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
        public static ExecutionResult Failed(string error) => new() { Status = ExecutionStatus.Failed, ErrorMessage = error };

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

        /// <summary>
        /// <para>创建事件参数。</para>
        /// Creates event args.
        /// </summary>
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

        /// <summary>
        /// <para>创建事件参数。</para>
        /// Creates event args.
        /// </summary>
        public ExecutionCompletedEventArgs(ExecutionResult result) => Result = result;
    }

    /// <summary>
    /// <para>执行上下文实现。</para>
    /// Execution context implementation.
    /// </summary>
    internal sealed class ExecutionContextImpl : INodeExecutionContext
    {
        private readonly IUnifiedLog _log;
        private readonly CancellationToken _token;
        private readonly Action<double> _progress;

        public ExecutionContextImpl(IUnifiedLog log, CancellationToken token, Action<double> progress)
        {
            _log = log;
            _token = token;
            _progress = progress;
        }

        public IUnifiedLog Log => _log;
        public CancellationToken CancellationToken => _token;

        public void ReportProgress(double progress)
        {
            if (progress < 0) progress = 0;
            else if (progress > 1) progress = 1;
            _progress?.Invoke(progress);
        }
    }
}
