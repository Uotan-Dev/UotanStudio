using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private bool _showAdvancedParameters;
    private bool _compactNodes;
    private IReadOnlyList<NodeConnectorViewModel> _selectedParameterInputs = [];

    public IReadOnlyList<NodePortChoice> ValueTypeChoices { get; } =
        FlowNodeViewModel.ValueTypeOptions.Select(value => new NodePortChoice(value, "Node_PortType_")).ToArray();

    /// <summary>Hides unconnected advanced and irrelevant inputs on canvas nodes.</summary>
    public bool CompactNodes
    {
        get => _compactNodes;
        set
        {
            if (SetProperty(ref _compactNodes, value))
            {
                RefreshParameterPresentation();
            }
        }
    }

    /// <summary>Includes advanced parameters in the selected node's inspector.</summary>
    public bool ShowAdvancedParameters
    {
        get => _showAdvancedParameters;
        set
        {
            if (SetProperty(ref _showAdvancedParameters, value))
            {
                RefreshParameterPresentation();
            }
        }
    }

    /// <summary>Relevant data and configuration inputs; flow belongs on the canvas.</summary>
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