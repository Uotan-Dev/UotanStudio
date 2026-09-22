using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private bool _showAdvancedParameters;

    /// <summary>Includes advanced parameters in the selected node's inspector.</summary>
    public bool ShowAdvancedParameters
    {
        get => _showAdvancedParameters;
        set
        {
            if (SetProperty(ref _showAdvancedParameters, value))
            {
                OnPropertyChanged(nameof(SelectedParameterInputs));
            }
        }
    }

    /// <summary>Relevant data and configuration inputs; flow belongs on the canvas.</summary>
    public IReadOnlyList<NodeConnectorViewModel> SelectedParameterInputs => SelectedNode is not { } node ? []
        : node.Input.Where(port => port.PortType != NodePortType.Flow
            && (!port.IsAdvanced || ShowAdvancedParameters || port.HasConnectedSource)
            && (port.IsVisibleWhen?.Invoke(node) != false || port.HasConnectedSource)).ToArray();

    private void RefreshParameterPresentation()
    {
        foreach (var node in Nodes)
        {
            foreach (var input in node.Input)
            {
                var edge = Connections.FirstOrDefault(connection => ReferenceEquals(connection.Target, input));
                var source = edge?.Source;
                var owner = source is null ? null : Nodes.FirstOrDefault(candidate => candidate.Output.Contains(source));
                input.SourceDescription = source is null ? string.Empty : (owner?.LocalizedTitle ?? string.Empty) + " → " + source.LocalizedTitle;
            }
        }
        OnPropertyChanged(nameof(SelectedParameterInputs));
    }
}