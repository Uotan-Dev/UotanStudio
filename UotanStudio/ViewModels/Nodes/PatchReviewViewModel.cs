using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>补丁评审状态视图模型，承载最近一次执行或预览的变更行、复合节点执行结果与发布结论。</para>
/// View model for the patch review state, carrying the change rows, composite execution results and publication
/// outcome of the most recent execution or preview.
/// </summary>
public sealed class PatchReviewViewModel : ViewModelBase
{
    /// <summary>Individual invocation results; publication remains a root-run decision.</summary>
    public IReadOnlyList<PatchCompositeExecutionViewModel> CompositeExecutions
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasPatchReview));
            }
        }
    } = [];

    /// <summary>Bounded review rows from the last execution or preview.</summary>
    public IReadOnlyList<PatchChangeViewModel> PatchChanges
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasPatchChanges));
                OnPropertyChanged(nameof(HasPatchReview));
            }
        }
    } = [];

    /// <summary>Whether the bounded report omitted additional skipped composites.</summary>
    public bool CompositeReportTruncated
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Whether a patch review panel should be visible.</summary>
    public bool HasPatchChanges => PatchChanges.Count > 0;

    /// <summary>Includes a meaningful preflight result even when no file changes were staged.</summary>
    public bool HasPatchReview => HasPatchChanges || CompositeExecutions.Count > 0 || Outcome is PatchRunOutcome.NotApplicable
        or PatchRunOutcome.NoChanges or PatchRunOutcome.AlreadyApplied or PatchRunOutcome.Conflict;

    /// <summary>Localized publication outcome, distinct from node execution success.</summary>
    public string PatchOutcomeText => LanguageService.Instance.GetString("Node_PatchOutcome_" + Outcome);

    /// <summary>Clears staged review rows while keeping the last publication outcome.</summary>
    public void Clear()
    {
        PatchChanges = [];
        CompositeExecutions = [];
    }

    /// <summary>Resets the whole review state before a new execution.</summary>
    public void Reset()
    {
        Clear();
        CompositeReportTruncated = false;
        Outcome = PatchRunOutcome.None;
    }

    /// <summary>Applies one execution result to the review state.</summary>
    public void Publish(ExecutionResult result)
    {
        PatchChanges = result.PatchChanges.Select(change => new PatchChangeViewModel(change)).ToArray();
        CompositeExecutions = result.CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item)).ToArray();
        CompositeReportTruncated = result.CompositeReportTruncated;
        Outcome = result.PatchOutcome;
    }

    /// <summary>Rebuilds localized review rows after a language switch.</summary>
    public void RefreshLocalization()
    {
        PatchChanges = PatchChanges.Select(item => new PatchChangeViewModel(item.Change)).ToArray();
        CompositeExecutions = CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item.Execution)).ToArray();
        OnPropertyChanged(nameof(PatchOutcomeText));
    }

    private PatchRunOutcome Outcome
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(PatchOutcomeText));
                OnPropertyChanged(nameof(HasPatchReview));
            }
        }
    }
}