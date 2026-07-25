using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Abstractions.Nodes;

/// <summary>
/// <para>节点执行状态。</para>
/// Execution status of a node.
/// </summary>
public enum NodeExecutionStatus
{
    /// <summary><para>未执行。</para>Not executed.</summary>
    Idle,

    /// <summary><para>排队中。</para>Queued.</summary>
    Pending,

    /// <summary><para>正在执行。</para>Running.</summary>
    Running,

    /// <summary><para>执行成功。</para>Succeeded.</summary>
    Succeeded,

    /// <summary><para>执行失败。</para>Failed.</summary>
    Failed,

    /// <summary><para>被跳过（如分支未选中）。</para>Skipped.</summary>
    Skipped,

    /// <summary><para>被取消。</para>Cancelled.</summary>
    Cancelled
}

/// <summary>
/// <para>节点实例，承载运行时状态与执行逻辑。</para>
/// Node instance, carrying runtime state and execution logic.
/// </summary>
public interface INodeInstance
{
    /// <summary>
    /// <para>节点定义。</para>
    /// The node definition.
    /// </summary>
    INodeDefinition Definition { get; }

    /// <summary>
    /// <para>节点实例唯一标识。</para>
    /// Unique identifier of the node instance.
    /// </summary>
    string InstanceId { get; }

    /// <summary>
    /// <para>当前执行状态。</para>
    /// Current execution status.
    /// </summary>
    NodeExecutionStatus Status { get; set; }

    /// <summary>
    /// <para>执行错误信息（<see cref="Status"/> 为 Failed 时有效）。</para>
    /// Execution error message (valid when <see cref="Status"/> is Failed).
    /// </summary>
    string? ErrorMessage { get; set; }

    /// <summary>
    /// <para>获取此节点输入端口对应的运行时数据（由执行引擎在调度前注入）。</para>
    /// Gets the runtime data corresponding to this node's input ports (injected by the execution engine before scheduling).
    /// </summary>
    IReadOnlyDictionary<string, object?> InputValues { get; }

    /// <summary>
    /// <para>获取此节点输出端口对应的运行时数据（由节点执行完毕后填充）。</para>
    /// Gets the runtime data corresponding to this node's output ports (populated by the node after execution).
    /// </summary>
    IReadOnlyDictionary<string, object?> OutputValues { get; }

    /// <summary>
    /// <para>设置指定输入端口的运行时值。</para>
    /// Sets the runtime value of the specified input port.
    /// </summary>
    void SetInput(string connectorTitle, object? value);

    /// <summary>
    /// <para>设置指定输出端口的运行时值。</para>
    /// </summary>
    void SetOutput(string connectorTitle, object? value);

    /// <summary>
    /// <para>校验节点输入是否满足执行条件。</para>
    /// Validates whether the node's inputs satisfy execution conditions.
    /// </summary>
    /// <returns>
    /// <para>错误信息；返回 <c>null</c> 表示校验通过。</para>
    /// Error message; returns <c>null</c> when validation passes.
    /// </returns>
    string? ValidateInputs();

    /// <summary>
    /// <para>异步执行节点逻辑。</para>
    /// Asynchronously executes the node logic.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文，提供日志、设备管理器等服务。</para>
    /// Execution context, providing logging, device manager, etc.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    ValueTask ExecuteAsync(INodeExecutionContext context, CancellationToken cancellationToken = default);
}
