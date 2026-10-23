using UotanStudio.Firmware.Patching;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Services.Patching;

/// <summary>One composite invocation, independent of the root transaction's publication result.<para>单次组合调用，与根事务的发布结果相互独立。</para></summary>
/// <param name="Invocation">One-based invocation number.<para>从一开始的调用序号。</para></param>
/// <param name="InstanceId">Instance identifier of the node.<para>节点的实例标识。</para></param>
/// <param name="PackageId">Identifier of the plugin package.<para>插件包的标识。</para></param>
/// <param name="Version">Version of the plugin package.<para>插件包的版本。</para></param>
/// <param name="Name">Display name of the package.<para>包的显示名称。</para></param>
/// <param name="Status">Execution status of the invocation.<para>本次调用的执行状态。</para></param>
/// <param name="Error">Truncated failure message, when failed.<para>失败时截断后的错误消息。</para></param>
public sealed record PatchCompositeExecution(int Invocation, string InstanceId, string PackageId,
    string Version, string Name, NodeExecutionStatus Status, string? Error)
{
    /// <summary>Enclosing invocation, or null for a root-level composite.<para>外层调用序号，根级组合时为空。</para></summary>
    /// <value>Invocation number of the enclosing composite.<para>外层组合的调用序号。</para></value>
    public int? ParentInvocation { get; init; }

    /// <summary>Observed checks in this invocation and its descendants; not proof of publication.<para>本调用及其后代观察到的检查结果，不证明已发布。</para></summary>
    /// <value>Combined applicability flags.<para>合并后的适用性标志。</para></value>
    public CompositeApplicability Applicability { get; init; }

    /// <summary>Why this composite was not invoked, when its status is Skipped.<para>状态为跳过时未调用该组合的原因。</para></summary>
    /// <value>Skip reason, present when the status is Skipped.<para>状态为跳过时给出的原因。</para></value>
    public CompositeSkipReason? SkipReason { get; init; }
}

/// <summary>Scheduler evidence for a skipped composite, distinct from applicability checks.<para>跳过组合的调度依据，区别于适用性检查。</para></summary>
public enum CompositeSkipReason
{
    Condition,
    UpstreamFailure,
    PreflightRejected,
    NotReached
}

/// <summary>Bounded presence flags retain mixed preflight results without inventing a single verdict.<para>用有界的出现标志保留混合预检结果，不强行给出单一结论。</para></summary>
[Flags]
public enum CompositeApplicability
{
    None = 0,
    Applicable = 1,
    AlreadyApplied = 2,
    NotApplicable = 4,
    Conflict = 8
}

public sealed partial class PatchRun
{
    private readonly Lock _compositeLock = new();
    private readonly List<PatchCompositeExecution> _compositeExecutions = [];
    private readonly AsyncLocal<int?> _currentComposite = new();

    /// <summary>Whether skipped rows exceeded the bounded report capacity.<para>跳过的条目是否超出有界报告容量。</para></summary>
    /// <value>True when rows were dropped from the report.<para>报告有条目被丢弃时为 true。</para></value>
    public bool CompositeReportTruncated { get; private set; }

    internal void RecordSkippedComposite(CompositeNodeViewModel node, CompositeSkipReason reason)
    {
        lock (_compositeLock)
        {
            CheckOpen();
            if (_compositeExecutions.Count >= 4096)
            {
                CompositeReportTruncated = true;
                return;
            }
            _compositeExecutions.Add(new PatchCompositeExecution(_compositeExecutions.Count + 1,
                node.InstanceId, node.PluginId, node.PluginVersion, node.Title, NodeExecutionStatus.Skipped, null)
            {
                ParentInvocation = _currentComposite.Value,
                SkipReason = reason
            });
        }
    }

    private void RecordCompositeApplicability(PatchApplicability state)
    {
        var evidence = state switch
        {
            PatchApplicability.Applicable => CompositeApplicability.Applicable,
            PatchApplicability.AlreadyApplied => CompositeApplicability.AlreadyApplied,
            PatchApplicability.NotApplicable => CompositeApplicability.NotApplicable,
            PatchApplicability.Conflict => CompositeApplicability.Conflict,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        lock (_compositeLock)
        {
            var current = _currentComposite.Value;
            while (current is { } invocation)
            {
                var row = _compositeExecutions[invocation - 1];
                _compositeExecutions[invocation - 1] = row with { Applicability = row.Applicability | evidence };
                current = row.ParentInvocation;
            }
        }
    }

    /// <summary>Bounded snapshots in invocation-start order, including nested and repeated calls.<para>按调用开始顺序排列的有界快照，含嵌套与重复调用。</para></summary>
    /// <value>Current snapshot of the recorded executions.<para>已记录执行的当前快照。</para></value>
    public IReadOnlyList<PatchCompositeExecution> CompositeExecutions
    {
        get
        {
            lock (_compositeLock)
            {
                return [.. _compositeExecutions];
            }
        }
    }

    internal int BeginComposite(CompositeNodeViewModel node)
    {
        lock (_compositeLock)
        {
            CheckOpen();
            if (_compositeExecutions.Count >= 4096)
            {
                throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_CompositeLimit"));
            }
            var index = _compositeExecutions.Count;
            _compositeExecutions.Add(new PatchCompositeExecution(index + 1, node.InstanceId, node.PluginId,
                node.PluginVersion, node.Title, NodeExecutionStatus.Running, null)
            {
                ParentInvocation = _currentComposite.Value
            });
            _currentComposite.Value = index + 1;
            return index;
        }
    }

    internal void EndComposite(int index, NodeExecutionStatus status, string? error)
    {
        lock (_compositeLock)
        {
            _compositeExecutions[index] = _compositeExecutions[index] with
            {
                Status = status,
                Error = error is { Length: > 4096 } ? error[..4096] : error
            };
            _currentComposite.Value = _compositeExecutions[index].ParentInvocation;
        }
    }
}