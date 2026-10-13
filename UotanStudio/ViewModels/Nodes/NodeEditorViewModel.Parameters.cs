using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    public Services.Workspace.IWorkflowDocuments? Documents { get; set; }
    private IReadOnlyList<NodeConnectorViewModel> _selectedParameterInputs = [];

    public IReadOnlyList<NodePortChoice> ValueTypeChoices { get; } =
        [.. FlowNodeViewModel.ValueTypeOptions.Select(value => new NodePortChoice(value, "Node_PortType_"))];

    /// <summary>Hides unconnected advanced and irrelevant inputs on canvas nodes.<para>在画布节点上隐藏未连接的高级输入与无关输入。</para></summary>
    /// <value>True when compact presentation is enabled.<para>启用紧凑呈现时为 True。</para></value>
    public bool CompactNodes
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RefreshParameterPresentation();
            }
        }
    }

    /// <summary>Includes advanced parameters in the selected node's inspector.<para>在所选节点的检查器中包含高级参数。</para></summary>
    /// <value>True when advanced parameters are shown.<para>显示高级参数时为 True。</para></value>
    public bool ShowAdvancedParameters
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RefreshParameterPresentation();
            }
        }
    }

    /// <summary>Relevant data and configuration inputs; flow belongs on the canvas.<para>相关的数据与配置输入；流端口仍归属画布。</para></summary>
    /// <value>The parameter inputs of the current selection.<para>当前选区的参数输入。</para></value>
    public IReadOnlyList<NodeConnectorViewModel> SelectedParameterInputs => _selectedParameterInputs;

    private void RefreshParameterPresentation()
    {
        var sources = Connections.Where(edge => edge.Target is not null)
            .GroupBy(edge => edge.Target!).ToDictionary(group => group.Key, group => group.First().Source);
        var owners = Nodes.SelectMany(node => node.Output.Select(port => (port, node)))
            .GroupBy(pair => pair.port).ToDictionary(group => group.Key, group => group.First().node);
        foreach (var node in Nodes)
        {
            foreach (var input in node.Input)
            {
                AttachPropertyTracking(input);
                var source = sources.GetValueOrDefault(input);
                var owner = source is null ? null : owners.GetValueOrDefault(source);
                input.SourceDescription = source is null ? string.Empty : (owner?.DisplayHeader ?? string.Empty) + " → " + source.LocalizedTitle;
            }
            node.RefreshCanvasInputs(CompactNodes);
        }
        var visible = SelectedNode is not { } selected ? [] : selected.Input.Where(port =>
            port.PortType != NodePortType.Flow
            && (!port.IsAdvanced || ShowAdvancedParameters || port.HasConnectedSource)
            && (port.IsVisibleWhen?.Invoke(selected) != false || port.HasConnectedSource)).ToArray();
        if (!_selectedParameterInputs.SequenceEqual(visible))
        {
            _selectedParameterInputs = visible;
            OnPropertyChanged(nameof(SelectedParameterInputs));
        }
    }
}