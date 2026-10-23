namespace UotanStudio.Workflow.Execution;

/// <summary>
/// Signals that a workflow stopped because a Terminate node requested it, without being a user cancellation.
/// The engine separates this from <see cref="OperationCanceledException"/> so the run reports
/// <see cref="ExecutionStatus.Terminated"/> instead of <see cref="ExecutionStatus.Cancelled"/>.
/// <para>表示工作流因终止节点请求而停止，而不是用户取消。引擎将其与
/// <see cref="OperationCanceledException"/> 区分，使运行报告 <see cref="ExecutionStatus.Terminated"/> 而非
/// <see cref="ExecutionStatus.Cancelled"/>。</para>
/// </summary>
public sealed class WorkflowTerminatedException : Exception
{
    /// <summary>
    /// Creates a termination signal with an optional reason.
    /// <para>以可选原因创建终止信号。</para>
    /// </summary>
    /// <param name="reason">Optional reason reported by the Terminate node. <para>终止节点上报的可选原因。</para></param>
    public WorkflowTerminatedException(string? reason)
        : base(string.IsNullOrEmpty(reason) ? "Workflow terminated by Terminate node" : reason)
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets the reason reported by the Terminate node.
    /// <para>获取终止节点上报的原因。</para>
    /// </summary>
    /// <value>The reason, or <see langword="null"/> when the node supplied none.<para>原因；节点未提供时为 <see langword="null"/>。</para></value>
    public string? Reason { get; }
}