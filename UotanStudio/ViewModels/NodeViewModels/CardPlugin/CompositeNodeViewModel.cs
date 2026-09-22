using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Host-owned node that executes a portable visual-plugin graph.</summary>
public sealed class CompositeNodeViewModel : FlowNodeViewModel
{
    private readonly VisualPluginPackage _package;
    private readonly Func<IReadOnlyList<string>>? _validateExecution;

    internal VisualPluginPackage Package => _package;

    /// <summary>Creates the public node surface declared by the package.</summary>
    public CompositeNodeViewModel(VisualPluginPackage package, Func<IReadOnlyList<string>>? validateExecution = null)
        : base(package.Manifest.Name, NodeCategory.Plugin)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _validateExecution = validateExecution;
        TypeKey = package.NodeTypeKey;
        AddPorts(Input, package.Inputs, true);
        AddPorts(Output, package.Outputs, false);
        if (!Input.Any(port => port.PortType == NodePortType.Flow))
        {
            var portId = "execution-flow";
            for (var suffix = 1; Input.Any(port => port.StableId == portId); suffix++)
            {
                portId = "execution-flow-" + suffix;
            }
            Input.Add(new NodeConnectorViewModel
            {
                PortId = portId,
                Title = Input.Any(port => port.Title == "Flow") ? "Execution Flow" : "Flow",
                IsInput = true,
                PortType = NodePortType.Flow,
                MaxConnections = 1
            });
        }
    }

    /// <summary>Installed package identity.</summary>
    public string PluginId => _package.Manifest.Id;

    /// <summary>Installed package version.</summary>
    public string PluginVersion => _package.Manifest.Version;

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (_validateExecution is not null)
        {
            var errors = _validateExecution();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }
        }
        var runtime = WorkflowRuntimeBuilder.Build(_package.Graph, preserveUnknown: false);
        if (runtime.Errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, runtime.Errors));

        ApplyPublicInputs(runtime.NodesBySerializedId);
        var engine = new NodeExecutionEngine(context.Log);
        engine.ProgressChanged += (_, progress) => context.ReportProgress(progress);
        var result = await engine.ExecuteAsync(runtime.Nodes, runtime.Connections,
            context.CancellationToken, new NodeExecutionOptions { SharedPatches = context.Patches, Package = _package }).ConfigureAwait(false);
        if (result.Status == Services.Execution.ExecutionStatus.Cancelled)
        {
            throw new OperationCanceledException(context.CancellationToken);
        }
        if (result.Status != Services.Execution.ExecutionStatus.Succeeded)
            throw new InvalidOperationException(result.ErrorMessage ?? $"Composite workflow ended with {result.Status}.");
        CopyPublicOutputs(runtime.NodesBySerializedId);
    }

    private void ApplyPublicInputs(IReadOnlyDictionary<string, FlowNodeViewModel> nodes)
    {
        foreach (var definition in _package.Inputs)
        {
            if (!nodes.TryGetValue(definition.NodeInstanceId, out var node))
                throw new InvalidOperationException($"Composite input '{definition.Id}' references a missing internal node.");
            var target = node.Input.FirstOrDefault(port => string.Equals(port.StableId, definition.PortId, StringComparison.Ordinal));
            var source = Input.First(port => string.Equals(port.StableId, definition.Id, StringComparison.Ordinal));
            var value = source.Data ?? source.InitialValue;
            if (definition.Required && (value is null || value is string text && string.IsNullOrWhiteSpace(text)))
                throw new InvalidOperationException($"Composite input '{definition.Name}' is required.");
            if (target is null)
                throw new InvalidOperationException($"Composite input '{definition.Id}' references a missing internal port.");
            target.InitialValue = value;
            target.Data = value;
        }
    }

    private void CopyPublicOutputs(IReadOnlyDictionary<string, FlowNodeViewModel> nodes)
    {
        foreach (var definition in _package.Outputs)
        {
            if (!nodes.TryGetValue(definition.NodeInstanceId, out var node))
                throw new InvalidOperationException($"Composite output '{definition.Id}' references a missing internal node.");
            var source = node.Output.FirstOrDefault(port => string.Equals(port.StableId, definition.PortId, StringComparison.Ordinal));
            var target = Output.First(port => string.Equals(port.StableId, definition.Id, StringComparison.Ordinal));
            if (source is null)
                throw new InvalidOperationException($"Composite output '{definition.Id}' references a missing internal port.");
            target.Data = source.Data;
        }
    }

    private static void AddPorts(ICollection<NodeConnectorViewModel> destination,
        IEnumerable<VisualPluginPortDefinition> definitions, bool input)
    {
        foreach (var definition in definitions)
        {
            var type = Enum.TryParse<NodePortType>(definition.PortType, true, out var parsed)
                ? parsed : NodePortType.Any;
            destination.Add(new NodeConnectorViewModel
            {
                PortId = definition.Id,
                Title = definition.Name,
                IsInput = input,
                PortType = type,
                MaxConnections = input ? 1 : 0,
                InitialValue = input && definition.DefaultValue is not null
                    ? WorkflowReconstruction.ConvertInputValue(type, definition.DefaultValue)
                    : null
            });
        }
    }
}