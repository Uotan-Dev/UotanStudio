using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Lossless placeholder for a workflow node whose implementation is unavailable.</summary>
public sealed class MissingNodeViewModel : FlowNodeViewModel
{
    private readonly string _reason;
    private readonly bool _hasNodeValue;
    private readonly bool _hasSelectedImage;

    /// <summary>Creates a placeholder while preserving the serialized port surface.</summary>
    public MissingNodeViewModel(NodeDto dto, string reason) : base(
        string.IsNullOrWhiteSpace(dto.Title) ? dto.TypeKey : dto.Title,
        Enum.TryParse<NodeCategory>(dto.Category, true, out var category) ? category : NodeCategory.Plugin)
    {
        TypeKey = dto.TypeKey;
        NodeValueType = dto.NodeValueType ?? "Text";
        NodeValue = dto.NodeValue ?? string.Empty;
        SelectedImage = dto.SelectedImage ?? string.Empty;
        _hasNodeValue = dto.NodeValueType is not null || dto.NodeValue is not null;
        _hasSelectedImage = dto.SelectedImage is not null;
        _reason = reason;
        AddPorts(Input, dto.InputPorts, true);
        AddPorts(Output, dto.OutputPorts, false);
    }

    /// <inheritdoc />
    public override bool IsUnavailable => true;

    /// <inheritdoc />
    public override bool IsConstantNode => _hasNodeValue;

    /// <inheritdoc />
    public override bool IsProjectImageNode => _hasSelectedImage;

    /// <inheritdoc />
    public override Task ExecuteAsync(INodeExecutionContext context)
        => Task.FromException(new InvalidOperationException(_reason));

    private static void AddPorts(ICollection<NodeConnectorViewModel> destination,
        IEnumerable<NodePortDto> ports, bool input)
    {
        foreach (var port in ports)
        {
            destination.Add(new NodeConnectorViewModel
            {
                PortId = port.Id,
                Title = port.Title,
                IsInput = input,
                PortType = Enum.TryParse<NodePortType>(port.PortType, true, out var type) ? type : NodePortType.Any,
                MaxConnections = port.MaxConnections
            });
        }
    }
}