using Avalonia;
using UotanStudio.Projects;
using UotanStudio.Services.Projects;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>A prepared graph replacement applied as one undoable operation.
/// <para>一份已准备好的图替换结果，作为单步可撤销操作应用。</para></summary>
/// <param name="Nodes">Nodes of the resulting graph.<para>结果图中的节点。</para></param>
/// <param name="Connections">Connections of the resulting graph.<para>结果图中的连线。</para></param>
/// <param name="Composite">Created composite node, or null for an expansion.
/// <para>新建的复合节点；展开操作为 null。</para></param>
public sealed record CompositeGraphEditPlan(IReadOnlyList<FlowNodeViewModel> Nodes,
    IReadOnlyList<NodeConnectionViewModel> Connections, CompositeNodeViewModel? Composite);

/// <summary>
/// Prepares reversible encapsulation and expansion without mutating the source graph.
/// <para>准备可撤销的封装与展开，不修改来源图。</para>
/// </summary>
public static class CompositeGraphService
{
    /// <summary>Replaces a selected subgraph and reconnects all boundary edges.
    /// <para>替换选中的子图，并重新连接全部边界连线。</para></summary>
    /// <param name="name">Display name of the new composite.<para>新复合节点的显示名称。</para></param>
    /// <param name="id">Identifier of the new composite package.<para>新复合包的标识。</para></param>
    /// <param name="allNodes">Nodes of the whole graph.<para>整个图的节点。</para></param>
    /// <param name="allConnections">Connections of the whole graph.<para>整个图的连线。</para></param>
    /// <param name="selectedNodes">Nodes to encapsulate.<para>要封装的节点。</para></param>
    /// <param name="boundary">Precomputed boundary interfaces, analyzed when null.
    /// <para>预先算出的边界接口；为 null 时现算。</para></param>
    /// <returns>The replacement graph with the composite node.<para>含复合节点的替换图。</para></returns>
    public static CompositeGraphEditPlan CreateEncapsulation(string name, string id,
        IReadOnlyList<FlowNodeViewModel> allNodes, IReadOnlyList<NodeConnectionViewModel> allConnections,
        IReadOnlyList<FlowNodeViewModel> selectedNodes, CompositeBoundaryAnalysis? boundary = null)
    {
        if (selectedNodes.Count == 0 || selectedNodes.Any(node => !allNodes.Contains(node)))
        {
            throw new InvalidOperationException("Select nodes belonging to the current graph.");
        }
        boundary ??= CompositeBoundaryAnalyzer.Analyze(selectedNodes, allConnections);
        var package = VisualPluginPackageBuilder.Create(name, id, VisualPluginKind.CompositeNode,
            selectedNodes, allConnections);
        package.Inputs = [.. boundary.Inputs];
        package.Outputs = [.. boundary.Outputs];
        package.Graph = new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(selectedNodes),
            Connections = [.. WorkflowPersistenceService.BuildConnectionDtos(selectedNodes, allConnections).Where(edge => edge.SourceInstanceId is not null && edge.TargetInstanceId is not null)]
        };
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        var composite = new CompositeNodeViewModel(package)
        {
            Location = new Point(selectedNodes.Average(node => node.Location.X), selectedNodes.Average(node => node.Location.Y))
        };
        var inputMap = MapPorts(selectedNodes, package.Inputs, composite.Input, true);
        var outputMap = MapPorts(selectedNodes, package.Outputs, composite.Output, false);
        var selectedInputs = selectedNodes.SelectMany(node => node.Input).ToHashSet();
        var selectedOutputs = selectedNodes.SelectMany(node => node.Output).ToHashSet();
        List<NodeConnectionViewModel> connections = [];
        foreach (var edge in allConnections)
        {
            if (edge.Source is null || edge.Target is null)
            {
                throw new InvalidOperationException("A connection has a missing endpoint.");
            }
            var sourceInside = selectedOutputs.Contains(edge.Source);
            var targetInside = selectedInputs.Contains(edge.Target);
            if (sourceInside && targetInside)
            {
                continue;
            }
            var source = sourceInside ? outputMap[edge.Source] : edge.Source;
            var target = targetInside ? inputMap[edge.Target] : edge.Target;
            if (!connections.Any(existing => ReferenceEquals(existing.Source, source) && ReferenceEquals(existing.Target, target)))
            {
                connections.Add(new NodeConnectionViewModel(source, target));
            }
        }
        return new CompositeGraphEditPlan([.. allNodes.Except(selectedNodes), composite], connections, composite);
    }

    /// <summary>Expands a composite with fresh instance identities and restores its boundary connections.
    /// <para>以全新实例标识展开复合节点，并恢复其边界连线。</para></summary>
    /// <param name="allNodes">Nodes of the whole graph.<para>整个图的节点。</para></param>
    /// <param name="allConnections">Connections of the whole graph.<para>整个图的连线。</para></param>
    /// <param name="composite">Composite node to expand.<para>要展开的复合节点。</para></param>
    /// <returns>The replacement graph holding the expanded nodes.<para>含展开后节点的替换图。</para></returns>
    public static CompositeGraphEditPlan CreateExpansion(IReadOnlyList<FlowNodeViewModel> allNodes,
        IReadOnlyList<NodeConnectionViewModel> allConnections, CompositeNodeViewModel composite)
    {
        if (!allNodes.Contains(composite))
        {
            throw new InvalidOperationException("The composite does not belong to this graph.");
        }
        var runtime = WorkflowRuntimeBuilder.Build(composite.Package.Graph, preserveUnknown: false);
        if (runtime.Errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, runtime.Errors));
        }
        var connections = composite.Package.Graph.Connections.Select(edge => new NodeConnectionViewModel(
            Resolve(runtime.NodesBySerializedId, edge.SourceInstanceId!, edge.SourcePortId!, false),
            Resolve(runtime.NodesBySerializedId, edge.TargetInstanceId!, edge.TargetPortId!, true))).ToList();
        var inputMap = composite.Package.Inputs.ToDictionary(definition => composite.Input.Single(port => port.StableId == definition.Id),
            definition => definition.GetBindings().Select(binding => Resolve(runtime.NodesBySerializedId,
                binding.NodeInstanceId, binding.PortId, true)).ToArray());
        var outputMap = composite.Package.Outputs.ToDictionary(definition => composite.Output.Single(port => port.StableId == definition.Id),
            definition => Resolve(runtime.NodesBySerializedId, definition.NodeInstanceId, definition.PortId, false));
        foreach (var (source, targets) in inputMap)
        {
            foreach (var target in targets)
            {
                target.InitialValue = source.InitialValue;
            }
        }
        foreach (var edge in allConnections)
        {
            if (edge.Source is null || edge.Target is null)
            {
                throw new InvalidOperationException("A connection has a missing endpoint.");
            }
            var source = outputMap.GetValueOrDefault(edge.Source, edge.Source);
            if (inputMap.TryGetValue(edge.Target, out var targets))
            {
                connections.AddRange(targets.Select(target => new NodeConnectionViewModel(source, target)));
            }
            else if (!composite.Input.Contains(edge.Target) && !composite.Output.Contains(edge.Source))
            {
                connections.Add(new NodeConnectionViewModel(source, edge.Target));
            }
            else if (outputMap.ContainsKey(edge.Source))
            {
                connections.Add(new NodeConnectionViewModel(source, edge.Target));
            }
        }
        var center = new Point(runtime.Nodes.Average(node => node.Location.X), runtime.Nodes.Average(node => node.Location.Y));
        foreach (var node in runtime.Nodes)
        {
            node.InstanceId = Guid.NewGuid().ToString("N");
            node.Location = new Point(node.Location.X - center.X + composite.Location.X,
                node.Location.Y - center.Y + composite.Location.Y);
        }
        return new CompositeGraphEditPlan([.. allNodes.Where(node => !ReferenceEquals(node, composite)), .. runtime.Nodes], connections, null);
    }

    private static Dictionary<NodeConnectorViewModel, NodeConnectorViewModel> MapPorts(
        IReadOnlyList<FlowNodeViewModel> nodes, IEnumerable<VisualPluginPortDefinition> definitions,
        IEnumerable<NodeConnectorViewModel> publicPorts, bool input)
    {
        var byId = nodes.ToDictionary(node => node.InstanceId);
        Dictionary<NodeConnectorViewModel, NodeConnectorViewModel> result = [];
        foreach (var definition in definitions)
        {
            var target = publicPorts.Single(port => port.StableId == definition.Id);
            foreach (var binding in definition.GetBindings())
            {
                result.Add(Resolve(byId, binding.NodeInstanceId, binding.PortId, input), target);
            }
        }
        return result;
    }

    private static NodeConnectorViewModel Resolve(IReadOnlyDictionary<string, FlowNodeViewModel> nodes,
        string nodeId, string portId, bool input)
    {
        var node = nodes[nodeId];
        return (input ? node.Input : node.Output).Single(port => port.StableId == portId);
    }
}