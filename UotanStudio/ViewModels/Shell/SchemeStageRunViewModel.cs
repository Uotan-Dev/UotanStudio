using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects.Schemes;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Read-only operator view of one scheme stage: declared identity and kind plus the latest run status,
/// executed-node count, failure text and published artifacts.
/// <para>单个 Scheme 阶段的只读操作员视图：声明的身份与类型，以及最近一次运行的状态、执行节点数、失败文本与已发布产物。</para>
/// </summary>
public sealed partial class SchemeStageRunViewModel : ObservableObject
{
    /// <summary>
    /// Creates the stage view from its declaration.
    /// <para>按阶段声明创建阶段视图。</para>
    /// </summary>
    /// <param name="stage">The declared stage; must not be <see langword="null"/>.
    /// <para>声明的阶段；不能为 <see langword="null"/>。</para></param>
    public SchemeStageRunViewModel(SchemeStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        StageId = stage.Id;
        StageName = stage.Name;
        Kind = stage.Kind;
    }

    /// <summary>
    /// Stable stage identifier.
    /// <para>稳定的阶段标识符。</para>
    /// </summary>
    public string StageId { get; }

    /// <summary>
    /// Optional declared stage name.
    /// <para>可选声明的阶段名称。</para>
    /// </summary>
    public string? StageName { get; }

    /// <summary>
    /// Declared stage kind.
    /// <para>声明的阶段类型。</para>
    /// </summary>
    public SchemeStageKind Kind { get; }

    /// <summary>
    /// Caption used by the stage list, falling back to <see cref="StageId"/> when the stage has no name.
    /// <para>阶段列表使用的标题；阶段没有名称时回退到 <see cref="StageId"/>。</para>
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(StageName) ? StageId : StageName;

    /// <summary>
    /// Terminal status of the latest run.
    /// <para>最近一次运行的最终状态。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusKey))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private SchemeStageStatus _status = SchemeStageStatus.NotRun;

    /// <summary>
    /// Number of nodes the stage executed in the latest run.
    /// <para>阶段在最近一次运行中执行的节点数。</para>
    /// </summary>
    [ObservableProperty]
    private int _executedNodes;

    /// <summary>
    /// Failure or contract error reported for the latest run.
    /// <para>最近一次运行报告的失败或契约错误。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>
    /// Paths published by the stage in the latest run.
    /// <para>阶段在最近一次运行中发布的路径。</para>
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _artifactPaths = [];

    /// <summary>
    /// Localization key describing <see cref="Status"/>.
    /// <para>描述 <see cref="Status"/> 的本地化键。</para>
    /// </summary>
    public string StatusKey => Status switch
    {
        SchemeStageStatus.Executed => "Node_Status_Succeeded",
        SchemeStageStatus.Reused => "Node_Status_Skipped",
        SchemeStageStatus.Failed => "Node_Status_Failed",
        SchemeStageStatus.Cancelled => "Node_Status_Cancelled",
        SchemeStageStatus.Terminated => "Node_Status_Failed",
        _ => "Node_Status_Idle"
    };

    /// <summary>
    /// Localized status caption.
    /// <para>本地化的状态标题。</para>
    /// </summary>
    public string StatusText => LanguageService.Instance.GetString(StatusKey);

    /// <summary>
    /// Whether the latest status represents a failed or terminated stage.
    /// <para>最近状态是否表示失败或终止的阶段。</para>
    /// </summary>
    public bool IsFailed => Status is SchemeStageStatus.Failed or SchemeStageStatus.Terminated;

    /// <summary>
    /// Whether the stage carries failure text.
    /// <para>阶段是否携带失败文本。</para>
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Applies one stage result reported by <c>SchemeRunner</c>.
    /// <para>应用 <c>SchemeRunner</c> 报告的单个阶段结果。</para>
    /// </summary>
    /// <param name="result">The result to apply; must not be <see langword="null"/>.
    /// <para>要应用的结果；不能为 <see langword="null"/>。</para></param>
    public void ApplyResult(SchemeStageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Status = result.Status;
        ExecutedNodes = result.ExecutedNodes;
        ErrorMessage = result.ErrorMessage;
        ArtifactPaths = result.ArtifactPaths;
    }

    /// <summary>
    /// Restores the pre-run state.
    /// <para>还原运行前状态。</para>
    /// </summary>
    public void Reset()
    {
        Status = SchemeStageStatus.NotRun;
        ExecutedNodes = 0;
        ErrorMessage = null;
        ArtifactPaths = [];
    }
}