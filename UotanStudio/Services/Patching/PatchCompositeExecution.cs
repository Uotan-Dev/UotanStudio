using UotanStudio.Firmware.Patching;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Patching;

/// <summary>One composite invocation, independent of the root transaction's publication result.</summary>
public sealed record PatchCompositeExecution(int Invocation, string InstanceId, string PackageId,
    string Version, string Name, NodeExecutionStatus Status, string? Error)
{
    /// <summary>Enclosing invocation, or null for a root-level composite.</summary>
    public int? ParentInvocation { get; init; }

    /// <summary>Observed checks in this invocation and its descendants; not proof of publication.</summary>
    public CompositeApplicability Applicability { get; init; }

    /// <summary>Why this composite was not invoked, when its status is Skipped.</summary>
    public CompositeSkipReason? SkipReason { get; init; }
}

/// <summary>Scheduler evidence for a skipped composite, distinct from applicability checks.</summary>
public enum CompositeSkipReason
{
    Condition,
    UpstreamFailure,
    PreflightRejected,
    NotReached
}

/// <summary>Bounded presence flags retain mixed preflight results without inventing a single verdict.</summary>
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
    // A value local to the async call chain prevents parallel branches from sharing a mutable stack.
    private readonly AsyncLocal<int?> _currentComposite = new();

    /// <summary>Whether skipped rows exceeded the bounded report capacity.</summary>
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

    /// <summary>Bounded snapshots in invocation-start order, including nested and repeated calls.</summary>
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