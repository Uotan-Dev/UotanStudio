namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Execution status of a node.
/// <para>节点执行状态。</para>
/// </summary>
public enum NodeExecutionStatus
{
    /// <summary>Not executed. <para>未执行。</para></summary>
    Idle,

    /// <summary>Queued. <para>排队中。</para></summary>
    Pending,

    /// <summary>Running. <para>正在执行。</para></summary>
    Running,

    /// <summary>Awaiting user input from the host.</summary>
    WaitingForInput,

    /// <summary>Succeeded. <para>执行成功。</para></summary>
    Succeeded,

    /// <summary>Failed. <para>执行失败。</para></summary>
    Failed,

    /// <summary>Skipped. <para>被跳过（如分支未选中）。</para></summary>
    Skipped,

    /// <summary>Cancelled. <para>被取消。</para></summary>
    Cancelled
}