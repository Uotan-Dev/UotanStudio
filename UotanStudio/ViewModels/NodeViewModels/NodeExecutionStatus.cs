namespace UotanStudio.ViewModels.NodeViewModels;

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

    /// <summary>Awaiting user input from the host.</summary>
    WaitingForInput,

    /// <summary><para>执行成功。</para>Succeeded.</summary>
    Succeeded,

    /// <summary><para>执行失败。</para>Failed.</summary>
    Failed,

    /// <summary><para>被跳过（如分支未选中）。</para>Skipped.</summary>
    Skipped,

    /// <summary><para>被取消。</para>Cancelled.</summary>
    Cancelled
}