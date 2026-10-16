using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Patching;

/// <summary>Requires mutations in a gated graph to be exclusively controlled by an Apply edge.<para>要求受门控图中的变更全部由 Apply 边控制。</para></summary>
internal static class PatchGatePolicy
{
    internal static string? Validate(IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnection>? connections,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flow)
    {
        var gates = nodes.Where(node => node.TypeKey == "Flow.Applicability.Gate").ToArray();
        if (gates.Length == 0)
        {
            return null;
        }
        if (nodes.Count > 1024 || connections?.Count > 4096)
        {
            return LanguageService.Instance.GetString("Node_Patch_GateGraphLimit");
        }
        var byId = nodes.ToDictionary(node => node.InstanceId);
        foreach (var connection in connections ?? [])
        {
            if (string.IsNullOrEmpty(connection.SourceInstanceId) || string.IsNullOrEmpty(connection.TargetInstanceId))
            {
                return LanguageService.Instance.GetString("Node_Patch_GateFlowRequired");
            }
            if (!byId.TryGetValue(connection.SourceInstanceId, out var source)
                || !byId.TryGetValue(connection.TargetInstanceId, out var target))
            {
                continue;
            }
            if (WorkflowGraphValidation.ResolvePort(source.Output, connection.SourcePortId, connection.SourceConnector)?.PortType == NodePortType.Flow
                && WorkflowGraphValidation.ResolvePort(target.Input, connection.TargetPortId, connection.TargetConnector)?.PortType != NodePortType.Flow)
            {
                return LanguageService.Instance.GetString("Node_Patch_GateFlowRequired");
            }
        }

        foreach (var controller in nodes.Where(node => node.TypeKey is "Flow.Collection.Loop" or "Flow.Execution.Parallel"))
        {
            var body = Reach(Edges(controller.InstanceId, flow).Where(edge => edge.Connector != "Completed")
                .Select(edge => edge.Target.InstanceId), flow);
            var completed = Reach(Edges(controller.InstanceId, flow).Where(edge => edge.Connector == "Completed")
                .Select(edge => edge.Target.InstanceId), flow);
            body.ExceptWith(completed);
            if (gates.Any(gate => body.Contains(gate.InstanceId)))
            {
                return LanguageService.Instance.GetString("Node_Patch_GateNestedControl");
            }
        }

        var incoming = flow.Values.SelectMany(edges => edges).Select(edge => edge.Target.InstanceId).ToHashSet();
        var roots = nodes.Where(node => !incoming.Contains(node.InstanceId)).Select(node => node.InstanceId).ToArray();
        HashSet<string> guarded = [];
        foreach (var gate in gates)
        {
            var apply = Reach(Edges(gate.InstanceId, flow).Where(edge => edge.Connector == "Apply")
                .Select(edge => edge.Target.InstanceId), flow);
            var skip = Reach(Edges(gate.InstanceId, flow).Where(edge => edge.Connector == "Skip")
                .Select(edge => edge.Target.InstanceId), flow);
            var bypass = Reach(roots, flow, gate.InstanceId);
            apply.ExceptWith(skip);
            apply.ExceptWith(bypass);
            guarded.UnionWith(apply);
        }
        foreach (var node in nodes)
        {
            var mutates = node is CompositeNodeViewModel composite
                ? PackageMutates(composite.Package, [], 0)
                : TypeMutates(node.TypeKey, [], 0);
            if (mutates && !guarded.Contains(node.InstanceId))
            {
                return LanguageService.Instance.GetString("Node_Patch_UnguardedMutation", node.LocalizedTitle);
            }
        }
        return null;
    }

    private static bool TypeMutates(string type, HashSet<string> visiting, int depth)
    {
        if (NodeFactoryRegistrar.Descriptors.Any(descriptor => descriptor.TypeKey == type
            && descriptor.Capabilities.HasFlag(NodeCapability.StagedWrite)))
        {
            return true;
        }
        if (NodeFactoryRegistrar.TryGetVisualPluginPackage(type, out var package))
        {
            return PackageMutates(package, visiting, depth);
        }
        return type.StartsWith("VisualPlugin.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PackageMutates(VisualPluginPackage package, HashSet<string> visiting, int depth)
    {
        if (depth >= 32 || !visiting.Add(package.NodeTypeKey))
        {
            return true;
        }
        var mutates = package.Graph.Nodes.Any(node => node.CompositePackage is { } embedded
            ? PackageMutates(embedded, visiting, depth + 1)
            : TypeMutates(node.TypeKey, visiting, depth + 1));
        visiting.Remove(package.NodeTypeKey);
        return mutates;
    }

    private static IEnumerable<(string Connector, FlowNodeViewModel Target)> Edges(string id,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flow)
        => flow.TryGetValue(id, out var edges) ? edges : [];

    private static HashSet<string> Reach(IEnumerable<string> starts,
        Dictionary<string, List<(string Connector, FlowNodeViewModel Target)>> flow, string? removeApplyFrom = null)
    {
        HashSet<string> visited = [];
        var queue = new Queue<string>(starts);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            foreach (var edge in Edges(current, flow))
            {
                if (current == removeApplyFrom && edge.Connector == "Apply")
                {
                    continue;
                }
                queue.Enqueue(edge.Target.InstanceId);
            }
        }
        return visited;
    }
}