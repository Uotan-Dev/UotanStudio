using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Host-owned node that executes a portable visual-plugin graph.</summary>
public sealed class CompositeNodeViewModel : FlowNodeViewModel
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> InvocationChain = new();
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
    }

    /// <summary>Installed package identity.</summary>
    public string PluginId => _package.Manifest.Id;

    /// <summary>Installed package version.</summary>
    public string PluginVersion => _package.Manifest.Version;

    /// <inheritdoc />
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        foreach (var output in Output)
        {
            output.Data = null;
        }
        var ancestors = InvocationChain.Value ?? [];
        if (ancestors.Contains(PluginId, StringComparer.OrdinalIgnoreCase) || ancestors.Count >= 32)
        {
            throw new InvalidOperationException($"Recursive or excessively nested composite invocation: {PluginId}.");
        }
        InvocationChain.Value = [.. ancestors, PluginId];
        try
        {
            await ExecuteCoreAsync(context).ConfigureAwait(false);
        }
        finally
        {
            InvocationChain.Value = ancestors;
        }
    }

    private async Task ExecuteCoreAsync(INodeExecutionContext context)
    {
        if (_package.Assets.Count > 0 && _package.BundlePath is null)
        {
            if (!NodeFactoryRegistrar.TryGetVisualPluginPackage(TypeKey, out var installed)
                || installed.Manifest.Version != PluginVersion || installed.BundlePath is null
                || _package.Assets.Count != installed.Assets.Count
                || _package.Assets.Any(asset => !installed.Assets.Any(candidate => candidate.Id == asset.Id
                    && candidate.Length == asset.Length && candidate.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException($"Composite '{PluginId}' requires its exact installed resource bundle version {PluginVersion}.");
            }
            // Only a validated registration may supply a trusted archive path after JSON reload.
            _package.BundlePath = installed.BundlePath;
            _package.BundleRoot = installed.BundleRoot;
        }
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
        using var userWait = new ChildInteractionWait(engine, context);
        engine.ProgressChanged += (_, progress) => context.ReportProgress(progress);
        var result = await engine.ExecuteAsync(runtime.Nodes, runtime.Connections,
            context.CancellationToken, new NodeExecutionOptions
            {
                SharedPatches = context.Patches,
                Documents = context.Documents,
                Package = _package,
                Interactions = context.Interactions,
                EntryActivation = _package.Inputs.Where(port => port.PortType == nameof(NodePortType.Flow))
                    .SelectMany(definition => definition.GetBindings().Select(binding =>
                        (binding.NodeInstanceId, Active: Input.Single(port => port.StableId == definition.Id).Data is not false)))
                    .GroupBy(entry => entry.NodeInstanceId)
                    .ToDictionary(group => group.Key, group => group.Any(entry => entry.Active))
            }).ConfigureAwait(false);
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
            var source = Input.First(port => string.Equals(port.StableId, definition.Id, StringComparison.Ordinal));
            var value = source.HasRuntimeSource ? source.Data : source.Data ?? source.InitialValue;
            if (definition.Required && (value is null || value is string text && string.IsNullOrWhiteSpace(text)))
                throw new InvalidOperationException($"Composite input '{definition.Name}' is required.");
            foreach (var binding in definition.GetBindings())
            {
                if (!nodes.TryGetValue(binding.NodeInstanceId, out var node))
                    throw new InvalidOperationException($"Composite input '{definition.Id}' references a missing internal node.");
                var target = node.Input.FirstOrDefault(port => string.Equals(port.StableId, binding.PortId, StringComparison.Ordinal));
                if (target is null)
                    throw new InvalidOperationException($"Composite input '{definition.Id}' references a missing internal port.");
                target.InitialValue = value;
                target.Data = value;
                target.IsSensitive |= source.IsSensitive || source.IsRuntimeSensitive;
            }
        }
    }

    /// <summary>One parent wait spans all concurrently waiting children in this invocation.</summary>
    private sealed class ChildInteractionWait : IDisposable
    {
        private readonly NodeExecutionEngine _engine;
        private readonly INodeExecutionContext _context;
        private readonly object _gate = new();
        private readonly HashSet<FlowNodeViewModel> _waiting = [];
        private IDisposable? _scope;
        private bool _disposed;

        public ChildInteractionWait(NodeExecutionEngine engine, INodeExecutionContext context)
        {
            _engine = engine;
            _context = context;
            engine.NodeStatusChanged += OnStatusChanged;
        }

        private void OnStatusChanged(object? sender, NodeStatusChangedEventArgs args)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (args.Node.ExecutionStatus == NodeExecutionStatus.WaitingForInput)
                {
                    if (_waiting.Add(args.Node) && _waiting.Count == 1)
                    {
                        _scope = _context.BeginUserInteractionWait();
                    }
                }
                else if (_waiting.Remove(args.Node) && _waiting.Count == 0)
                {
                    var scope = _scope;
                    _scope = null;
                    scope?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            _engine.NodeStatusChanged -= OnStatusChanged;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _waiting.Clear();
                var scope = _scope;
                _scope = null;
                scope?.Dispose();
            }
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
            target.Data = node.ExecutionStatus == NodeExecutionStatus.Succeeded
                ? source.PortType == NodePortType.Flow ? source.Data ?? true : source.Data
                : source.PortType == NodePortType.Flow ? false : null;
            target.IsRuntimeSensitive |= source.IsSensitive || source.IsRuntimeSensitive;
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
                Title = definition.Id,
                DisplayName = definition.Name,
                IsInput = input,
                PortType = type,
                IsRequired = definition.Required,
                IsSensitive = definition.IsSensitive,
                MaxConnections = input ? 1 : 0,
                InitialValue = input && definition.DefaultValue is not null
                    ? WorkflowReconstruction.ConvertInputValue(type, definition.DefaultValue)
                    : null
            });
        }
    }
}