using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Editable view of one scheme stage: identity, kind, failure policy and either an embedded workflow
/// graph or a project-workflow reference.
/// <para>单个 Scheme 阶段的可编辑视图：身份、类型、失败策略，以及内嵌工作流图或项目工作流引用。</para>
/// </summary>
public sealed partial class SchemeStageEditorViewModel : ObservableObject
{
    /// <summary>
    /// Creates an editor row from a declared stage.
    /// <para>按声明阶段创建编辑行。</para>
    /// </summary>
    /// <param name="stage">The stage to edit; must not be <see langword="null"/>.
    /// <para>要编辑的阶段；不能为 <see langword="null"/>。</para></param>
    public SchemeStageEditorViewModel(SchemeStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _id = stage.Id;
        _name = stage.Name ?? string.Empty;
        _kind = stage.Kind;
        _failurePolicy = stage.FailurePolicy;
        _projectWorkflowName = stage.ProjectWorkflowName ?? string.Empty;
        _embeddedWorkflowName = string.Empty;
        Graph = stage.Graph ?? new WorkflowSnapshot();
    }

    /// <summary>
    /// Stable stage identifier.
    /// <para>稳定的阶段标识符。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _id;

    /// <summary>
    /// Optional display name of the stage.
    /// <para>可选的阶段显示名称。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    /// <summary>
    /// Declared execution kind of the stage.
    /// <para>阶段声明的执行类型。</para>
    /// </summary>
    [ObservableProperty]
    private SchemeStageKind _kind;

    /// <summary>
    /// Reaction when the stage does not succeed.
    /// <para>阶段未成功时的反应方式。</para>
    /// </summary>
    [ObservableProperty]
    private SchemeStageFailurePolicy _failurePolicy;

    /// <summary>
    /// Name of a project workflow resolved at run time; empty when the stage embeds its graph.
    /// <para>运行时解析的项目工作流名称；阶段内嵌图时为空。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReference))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _projectWorkflowName;

    /// <summary>
    /// Name of the workflow captured into the embedded graph; informational only.
    /// <para>捕获进内嵌图的工作流名称；仅作说明。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _embeddedWorkflowName;

    /// <summary>
    /// Embedded workflow graph executed by the stage when it does not reference a project workflow.
    /// <para>阶段不引用项目工作流时执行的内嵌工作流图。</para>
    /// </summary>
    public WorkflowSnapshot Graph { get; private set; } = new();

    /// <summary>
    /// Selectable stage kinds.
    /// <para>可选阶段类型。</para>
    /// </summary>
    public IReadOnlyList<SchemeStageKind> KindOptions { get; } = Enum.GetValues<SchemeStageKind>();

    /// <summary>
    /// Selectable failure policies.
    /// <para>可选失败策略。</para>
    /// </summary>
    public IReadOnlyList<SchemeStageFailurePolicy> FailurePolicyOptions { get; } =
        Enum.GetValues<SchemeStageFailurePolicy>();

    /// <summary>
    /// Caption used by the stage list, falling back to <see cref="Id"/> when the stage has no name.
    /// <para>阶段列表使用的标题；阶段没有名称时回退到 <see cref="Id"/>。</para>
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>
    /// Whether the stage references a project workflow instead of its embedded graph.
    /// <para>阶段是否引用项目工作流而非内嵌图。</para>
    /// </summary>
    public bool IsReference => !string.IsNullOrWhiteSpace(ProjectWorkflowName);

    /// <summary>
    /// Number of nodes in the embedded graph.
    /// <para>内嵌图中的节点数。</para>
    /// </summary>
    public int NodeCount => Graph.Nodes.Count;

    /// <summary>
    /// Whether the stage carries any embedded graph content.
    /// <para>阶段是否携带内嵌图内容。</para>
    /// </summary>
    public bool HasEmbeddedGraph => Graph.Nodes.Count > 0 || Graph.Connections.Count > 0;

    /// <summary>
    /// Whether the stage carries either a reference or an embedded graph.
    /// <para>阶段是否携带引用或内嵌图。</para>
    /// </summary>
    public bool HasSource => IsReference || HasEmbeddedGraph;

    /// <summary>
    /// Source caption shown in the stage list: the referenced name or the captured workflow name.
    /// <para>阶段列表显示的来源标题：引用名称或捕获的工作流名称。</para>
    /// </summary>
    public string SourceText => IsReference ? ProjectWorkflowName
        : string.IsNullOrWhiteSpace(EmbeddedWorkflowName)
            ? Graph.Nodes.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)
                + LanguageService.Instance.GetString("Node_NodesLabel")
            : EmbeddedWorkflowName;

    /// <summary>
    /// Replaces the embedded graph with a captured workflow and clears the project reference.
    /// <para>用捕获的工作流替换内嵌图并清除项目引用。</para>
    /// </summary>
    /// <param name="graph">The captured graph; must not be <see langword="null"/>.
    /// <para>捕获的图；不能为 <see langword="null"/>。</para></param>
    /// <param name="workflowName">The source workflow name, or an empty string when unknown.
    /// <para>来源工作流名称；未知时为空字符串。</para></param>
    public void Embed(WorkflowSnapshot graph, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ProjectWorkflowName = string.Empty;
        EmbeddedWorkflowName = workflowName;
        Graph = graph;
        NotifyGraphChanged();
    }

    /// <summary>
    /// Switches the stage to a project-workflow reference and discards the embedded graph.
    /// <para>把阶段切换为项目工作流引用并丢弃内嵌图。</para>
    /// </summary>
    /// <param name="workflowName">The project workflow name to reference; empty clears the reference.
    /// <para>要引用的项目工作流名称；为空时清除引用。</para></param>
    public void Reference(string workflowName)
    {
        EmbeddedWorkflowName = string.Empty;
        Graph = new WorkflowSnapshot();
        ProjectWorkflowName = workflowName;
        NotifyGraphChanged();
    }

    /// <summary>
    /// Projects the row back to a persistence stage.
    /// <para>把编辑行投影回持久化阶段。</para>
    /// </summary>
    /// <returns>The edited stage. <para>编辑后的阶段。</para></returns>
    public SchemeStage ToStage() => new()
    {
        Id = Id.Trim(),
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Kind = Kind,
        FailurePolicy = FailurePolicy,
        Graph = Graph,
        ProjectWorkflowName = IsReference ? ProjectWorkflowName.Trim() : null
    };

    private void NotifyGraphChanged()
    {
        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(HasEmbeddedGraph));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(SourceText));
    }
}