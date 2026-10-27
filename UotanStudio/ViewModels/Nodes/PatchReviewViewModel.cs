using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// View model for the patch review state, carrying the change rows, composite execution results and publication
/// outcome of the most recent execution or preview.
/// <para>补丁评审状态视图模型，承载最近一次执行或预览的变更行、复合节点执行结果与发布结论。</para>
/// </summary>
public sealed class PatchReviewViewModel : ViewModelBase
{
    /// <summary>Individual invocation results; publication remains a root-run decision.
    /// <para>单次调用结果；是否发布仍由根运行决定。</para>
    /// </summary>
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

    /// <summary>Bounded review rows from the last execution or preview.
    /// <para>最近一次执行或预览产生的有界复核行。</para>
    /// </summary>
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

    /// <summary>Whether the bounded report omitted additional skipped composites.
    /// <para>有界报告是否省略了其它被跳过的组合节点。</para>
    /// </summary>
    public bool CompositeReportTruncated
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    /// <summary>Whether a patch review panel should be visible.
    /// <para>是否应显示补丁复核面板。</para>
    /// </summary>
    public bool HasPatchChanges => PatchChanges.Count > 0;

    /// <summary>Includes a meaningful preflight result even when no file changes were staged.
    /// <para>即使没有暂存任何文件变更，也包含有意义的预检结果。</para>
    /// </summary>
    public bool HasPatchReview => HasPatchChanges || CompositeExecutions.Count > 0 || Outcome is PatchRunOutcome.NotApplicable
        or PatchRunOutcome.NoChanges or PatchRunOutcome.AlreadyApplied or PatchRunOutcome.Conflict;

    /// <summary>Localized publication outcome, distinct from node execution success.
    /// <para>本地化的发布结果，与节点执行是否成功无关。</para>
    /// </summary>
    public string PatchOutcomeText => LanguageService.Instance.GetString("Node_PatchOutcome_" + Outcome);

    /// <summary>Clears staged review rows while keeping the last publication outcome.
    /// <para>清除已暂存的复核行，同时保留最近一次发布结果。</para>
    /// </summary>
    public void Clear()
    {
        PatchChanges = [];
        CompositeExecutions = [];
    }

    /// <summary>Resets the whole review state before a new execution.
    /// <para>在新执行前重置整个复核状态。</para>
    /// </summary>
    public void Reset()
    {
        Clear();
        CompositeReportTruncated = false;
        Outcome = PatchRunOutcome.None;
    }

    /// <summary>Applies one execution result to the review state.
    /// <para>把一次执行结果应用到复核状态。</para>
    /// </summary>
    public void Publish(ExecutionResult result)
    {
        PatchChanges = [.. result.PatchChanges.Select(change => new PatchChangeViewModel(change))];
        CompositeExecutions = [.. result.CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item))];
        CompositeReportTruncated = result.CompositeReportTruncated;
        Outcome = result.PatchOutcome;
    }

    /// <summary>Rebuilds localized review rows after a language switch.
    /// <para>切换语言后重建本地化复核行。</para>
    /// </summary>
    public void RefreshLocalization()
    {
        PatchChanges = [.. PatchChanges.Select(item => new PatchChangeViewModel(item.Change))];
        CompositeExecutions = [.. CompositeExecutions.Select(item => new PatchCompositeExecutionViewModel(item.Execution))];
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