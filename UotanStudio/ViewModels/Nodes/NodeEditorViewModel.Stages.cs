using System.Globalization;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private const int MinimumExecutionStageMaximum = 7;
    private IReadOnlyList<string>? _executionStageLabels;
    private int _executionStageLabelsMaximum = -1;

    /// <summary>
    /// Largest stage index offered by the stage editors; it is always at least seven and grows to cover any existing node.
    /// <para>阶段编辑器提供的最大阶段索引；至少为 7，并会扩展以覆盖现有节点。</para>
    /// </summary>
    /// <value>The highest zero-based stage available for editing.<para>可供编辑的最高从零开始阶段。</para></value>
    public int MaximumExecutionStage => Math.Max(MinimumExecutionStageMaximum,
        Nodes.Count == 0 ? FlowNodeViewModel.DefaultExecutionStage : Nodes.Max(node => node.ExecutionStage));

    /// <summary>
    /// Language-neutral stage labels shown by the stage editors, for example <c>S0</c> through <c>S7</c>.
    /// <para>阶段编辑器显示的阶段标签（语言中立），例如 <c>S0</c> 到 <c>S7</c>。</para>
    /// </summary>
    /// <value>One label per stage from zero through <see cref="MaximumExecutionStage"/>.<para>从 0 到 <see cref="MaximumExecutionStage"/> 的每阶段一个标签。</para></value>
    public IReadOnlyList<string> ExecutionStageLabels
    {
        get
        {
            var maximum = MaximumExecutionStage;
            if (_executionStageLabels is null || _executionStageLabelsMaximum != maximum)
            {
                _executionStageLabelsMaximum = maximum;
                _executionStageLabels = Enumerable.Range(0, maximum + 1).Select(FormatExecutionStage).ToArray();
            }
            return _executionStageLabels;
        }
    }

    /// <summary>
    /// Gets or sets the common execution stage of the current selection; setting it applies the stage to every selected node as one undoable edit.
    /// <para>获取或设置当前选区的共同执行阶段；赋值会把该阶段作为一次可撤销编辑应用到所有选中节点。</para>
    /// </summary>
    /// <value>The first selected node's stage, or <see cref="FlowNodeViewModel.DefaultExecutionStage"/> when nothing is selected.
    /// <para>首个选中节点的阶段；无选中节点时为 <see cref="FlowNodeViewModel.DefaultExecutionStage"/>。</para></value>
    public int SelectedExecutionStage
    {
        get => _selectedNodes.Count == 0 ? FlowNodeViewModel.DefaultExecutionStage : _selectedNodes[0].ExecutionStage;
        set
        {
            if (value < 0 || IsExecuting)
            {
                return;
            }
            ApplySelectionExecutionStage(value);
        }
    }

    /// <summary>
    /// Gets or sets the stage label of the current selection, for example <c>S2</c>; a malformed label is ignored.
    /// <para>获取或设置当前选区的阶段标签，例如 <c>S2</c>；格式无效的标签会被忽略。</para>
    /// </summary>
    /// <value>The label of <see cref="SelectedExecutionStage"/>.<para><see cref="SelectedExecutionStage"/> 对应的标签。</para></value>
    public string SelectedExecutionStageLabel
    {
        get => FormatExecutionStage(SelectedExecutionStage);
        set
        {
            if (value is { Length: > 1 } && value[0] == 'S'
                && int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var stage))
            {
                SelectedExecutionStage = stage;
            }
        }
    }

    /// <summary>
    /// Gets whether the selected nodes declare more than one execution stage.
    /// <para>获取选中节点是否声明了多个执行阶段。</para>
    /// </summary>
    /// <value><see langword="true"/> when the selection spans several stages.<para>选区跨越多个阶段时为 <see langword="true"/>。</para></value>
    public bool IsExecutionStageMixed => _selectedNodes.Select(node => node.ExecutionStage).Distinct().Skip(1).Any();

    private static string FormatExecutionStage(int stage) => "S" + stage.ToString(CultureInfo.InvariantCulture);

    private void ApplySelectionExecutionStage(int stage)
    {
        var targets = _selectedNodes.Where(node => node.ExecutionStage != stage).ToList();
        if (targets.Count == 0)
        {
            RefreshExecutionStagePresentation();
            return;
        }

        var commands = targets
            .Select(node => (IEditorCommand)new PropertyChangeCommand(
                "Edit execution stage",
                value => node.ExecutionStage = value is int stageValue ? stageValue : stage,
                node.ExecutionStage,
                stage))
            .ToList();
        _suppressPropertyTracking = true;
        try
        {
            ExecuteCommand(new CompoundCommand(commands));
        }
        finally
        {
            _suppressPropertyTracking = false;
        }
        foreach (var node in Nodes)
        {
            ClearNodeValidation(node);
        }
        RefreshExecutionStagePresentation();
    }

    private void RefreshExecutionStagePresentation()
    {
        OnPropertyChanged(nameof(MaximumExecutionStage));
        OnPropertyChanged(nameof(ExecutionStageLabels));
        OnPropertyChanged(nameof(SelectedExecutionStage));
        OnPropertyChanged(nameof(SelectedExecutionStageLabel));
        OnPropertyChanged(nameof(IsExecutionStageMixed));
    }
}