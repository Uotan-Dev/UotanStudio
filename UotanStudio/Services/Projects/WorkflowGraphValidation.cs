using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Services.Projects;

/// <summary>Shared structural and execution checks for editor and headless workflows.
/// <para>编辑画布与无头工作流共用的结构与执行校验。</para></summary>
internal static class WorkflowGraphValidation
{
    internal static string? ValidateConnection(IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnectionViewModel> connections, NodeConnectorViewModel? source, NodeConnectorViewModel? target)
    {
        var sourceNode = nodes.FirstOrDefault(node => source is not null && node.Output.Contains(source));
        var targetNode = nodes.FirstOrDefault(node => target is not null && node.Input.Contains(target));
        if (sourceNode is null || targetNode is null)
        {
            return Text("Node_Graph_MissingEndpoint");
        }
        if (ReferenceEquals(sourceNode, targetNode))
        {
            return Text("Node_Connection_SameNode");
        }
        if (!NodeDataUtil.CanConnect(source!.PortType, target!.PortType))
        {
            return Text("Node_Connection_Incompatible");
        }
        if (connections.Any(edge => ReferenceEquals(edge.Source, source) && ReferenceEquals(edge.Target, target)))
        {
            return Text("Node_Connection_Duplicate");
        }
        if (source.MaxConnections > 0 && connections.Count(edge => ReferenceEquals(edge.Source, source)) >= source.MaxConnections
            || target.MaxConnections > 0 && connections.Count(edge => ReferenceEquals(edge.Target, target)) >= target.MaxConnections)
        {
            return Text("Node_Connection_MaxReached");
        }
        return null;
    }

    internal static IReadOnlyList<WorkflowGraphIssue> Validate(IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnectionViewModel> connections, bool forExecution = false,
        bool deferConditionalRequirements = false)
    {
        List<WorkflowGraphIssue> issues = [];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<NodeConnectorViewModel, FlowNodeViewModel> owners = [];
        HashSet<NodeConnectorViewModel> inputs = [];
        HashSet<NodeConnectorViewModel> outputs = [];
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.InstanceId) || !identities.Add(node.InstanceId)
                || !double.IsFinite(node.Location.X) || !double.IsFinite(node.Location.Y))
            {
                issues.Add(new(node, Text("Node_Graph_InvalidIdentity")));
            }
            foreach (var ports in new[] { node.Input, node.Output })
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var port in ports)
                {
                    (ReferenceEquals(ports, node.Input) ? inputs : outputs).Add(port);
                    if (!ids.Add(port.StableId) || !owners.TryAdd(port, node) || port.MaxConnections < 0)
                    {
                        issues.Add(new(node, Text("Node_Graph_InvalidPort")));
                    }
                }
            }
            if (forExecution && node.IsUnavailable)
            {
                issues.Add(new(node, Text("Node_Graph_Unavailable")));
            }
        }
        List<NodeConnectionViewModel> accepted = [];
        HashSet<NodeConnectorViewModel> connected = [];
        HashSet<(NodeConnectorViewModel, NodeConnectorViewModel)> pairs = [];
        Dictionary<NodeConnectorViewModel, int> counts = [];
        foreach (var edge in connections)
        {
            string? error = null;
            if (edge.Source is not { } source || !outputs.Contains(source)
                || edge.Target is not { } target || !inputs.Contains(target))
            {
                error = Text("Node_Graph_MissingEndpoint");
            }
            else
            {
                if (ReferenceEquals(owners[source], owners[target]))
                {
                    error = Text("Node_Connection_SameNode");
                }
                else if (!NodeDataUtil.CanConnect(source.PortType, target.PortType))
                {
                    error = Text("Node_Connection_Incompatible");
                }
                else if (!pairs.Add((source, target)))
                {
                    error = Text("Node_Connection_Duplicate");
                }
                else if (source.MaxConnections > 0 && counts.GetValueOrDefault(source) >= source.MaxConnections
                    || target.MaxConnections > 0 && counts.GetValueOrDefault(target) >= target.MaxConnections)
                {
                    error = Text("Node_Connection_MaxReached");
                }
                else
                {
                    accepted.Add(edge);
                    connected.Add(target);
                    counts[source] = counts.GetValueOrDefault(source) + 1;
                    counts[target] = counts.GetValueOrDefault(target) + 1;
                }
            }
            if (error is not null)
            {
                issues.Add(new(edge.Target is not null ? owners.GetValueOrDefault(edge.Target) : null, error));
            }
        }
        if (issues.Count == 0)
        {
            var incoming = nodes.ToDictionary(node => node, _ => 0);
            var outgoing = nodes.ToDictionary(node => node, _ => new List<FlowNodeViewModel>());
            foreach (var edge in accepted)
            {
                var target = owners[edge.Target!];
                incoming[target]++;
                outgoing[owners[edge.Source!]].Add(target);
            }
            var ready = new Queue<FlowNodeViewModel>(nodes.Where(node => incoming[node] == 0));
            while (ready.TryDequeue(out var node))
            {
                foreach (var target in outgoing[node])
                {
                    if (--incoming[target] == 0)
                    {
                        ready.Enqueue(target);
                    }
                }
            }
            foreach (var node in nodes.Where(node => incoming[node] > 0))
            {
                issues.Add(new(node, Text("Node_Graph_Cycle")));
            }
        }
        if (forExecution)
        {
            var deferred = deferConditionalRequirements
                ? ConditionalFlowDescendants(nodes, accepted, owners)
                : [];
            foreach (var node in nodes)
            {
                if (deferred.Contains(node))
                {
                    continue;
                }
                foreach (var input in node.Input)
                {
                    if (input.IsRequired && input.PortType != NodePortType.Flow && !connected.Contains(input)
                        && input.IsVisibleWhen?.Invoke(node) != false
                        && (input.InitialValue is null || input.InitialValue is string value && string.IsNullOrWhiteSpace(value)))
                    {
                        issues.Add(new(node, LanguageService.Instance.GetString("Node_Graph_Required", input.LocalizedTitle)));
                    }
                }
            }
        }
        return issues;
    }

    private static HashSet<FlowNodeViewModel> ConditionalFlowDescendants(
        IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> edges,
        IReadOnlyDictionary<NodeConnectorViewModel, FlowNodeViewModel> owners)
    {
        var flow = edges.Where(edge => edge.Source?.PortType == NodePortType.Flow
                && edge.Target?.PortType == NodePortType.Flow)
            .GroupBy(edge => owners[edge.Source!])
            .ToDictionary(group => group.Key, group => group.Select(edge => owners[edge.Target!]).ToArray());
        HashSet<FlowNodeViewModel> deferred = [];
        var pending = new Queue<FlowNodeViewModel>(nodes
            .Where(node => node.TypeKey is "Flow.Condition.Branch" or "Flow.Applicability.Gate")
            .SelectMany(node => flow.GetValueOrDefault(node) ?? []));
        while (pending.TryDequeue(out var node))
        {
            if (!deferred.Add(node))
            {
                continue;
            }
            foreach (var next in flow.GetValueOrDefault(node) ?? [])
            {
                pending.Enqueue(next);
            }
        }
        return deferred;
    }

    internal static IReadOnlyList<WorkflowGraphIssue> ValidateRuntime(IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnection> connections)
    {
        List<WorkflowGraphIssue> issues = [];
        var byId = nodes.GroupBy(node => node.InstanceId).ToDictionary(group => group.Key, group => group.First());
        List<NodeConnectionViewModel> edges = [];
        foreach (var edge in connections)
        {
            byId.TryGetValue(edge.SourceInstanceId, out var source);
            byId.TryGetValue(edge.TargetInstanceId, out var target);
            var output = ResolvePort(source?.Output, edge.SourcePortId, edge.SourceConnector);
            var input = ResolvePort(target?.Input, edge.TargetPortId, edge.TargetConnector);
            if (output is null || input is null)
            {
                issues.Add(new(target, Text("Node_Graph_MissingEndpoint")));
            }
            else
            {
                edges.Add(new(output, input));
            }
        }
        issues.AddRange(Validate(nodes, edges, forExecution: true, deferConditionalRequirements: true));
        return issues;
    }

    internal static NodeConnectorViewModel? ResolvePort(IEnumerable<NodeConnectorViewModel>? ports, string? id, string title)
    {
        if (ports is null)
        {
            return null;
        }
        var matches = ports.Where(port => !string.IsNullOrWhiteSpace(id)
            ? string.Equals(port.StableId, id, StringComparison.Ordinal)
            : string.Equals(port.Title, title, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Text(string key) => LanguageService.Instance.GetString(key);
}

internal sealed record WorkflowGraphIssue(FlowNodeViewModel? Node, string Message)
{
    public string DisplayText => Node is null ? Message : $"{Node.DisplayHeader}: {Message}";
}