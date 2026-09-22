using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Interactions;

/// <summary>Infers form field interfaces from definition nodes without executing the workflow.</summary>
public static class InteractionSchemaResolver
{
    public static void Refresh(IEnumerable<FlowNodeViewModel> sourceNodes, IEnumerable<NodeConnectionViewModel> sourceConnections)
    {
        var nodes = sourceNodes.ToArray();
        var connections = sourceConnections.ToArray();
        foreach (var field in nodes.OfType<InteractionNodeViewModel>().Where(node => node.TypeKey.StartsWith("UI.Field.", StringComparison.Ordinal)))
        {
            var sensitivity = field.Input.Single(port => port.Title == "Sensitive");
            field.Input.Single(port => port.Title == "Default").IsSensitive = sensitivity.InitialValue is true
                || connections.Any(connection => ReferenceEquals(connection.Target, sensitivity));
        }
        var owners = nodes.SelectMany(node => node.Output.Select(port => (port, node))).ToDictionary(pair => pair.port, pair => pair.node);
        foreach (var dialog in nodes.OfType<InteractionNodeViewModel>().Where(node => node.TypeKey == "UI.Dialog.Show"))
        {
            var fields = new List<InteractionFieldSchema>();
            var visited = new HashSet<FlowNodeViewModel>();
            var input = dialog.Input.Single(port => port.Title == "Form");
            var complete = VisitInput(input);
            dialog.ApplyFieldSchema(fields, complete, connections);

            bool VisitInput(NodeConnectorViewModel port)
            {
                var edge = connections.FirstOrDefault(connection => ReferenceEquals(connection.Target, port));
                return edge?.Source is not null && owners.TryGetValue(edge.Source, out var owner) && Visit(owner);
            }

            bool Visit(FlowNodeViewModel node)
            {
                if (!visited.Add(node))
                {
                    return false;
                }
                if (node.TypeKey == "UI.Form.Create")
                {
                    return VisitInput(node.Input.Single(port => port.Title == "Content"));
                }
                if (node.TypeKey.StartsWith("UI.Layout.", StringComparison.Ordinal))
                {
                    var completeLayout = true;
                    foreach (var child in node.Input.Where(port => port.PortType == NodePortType.UiComponent))
                    {
                        if (connections.Any(connection => ReferenceEquals(connection.Target, child)))
                        {
                            completeLayout &= VisitInput(child);
                        }
                    }
                    return completeLayout;
                }
                if (node.TypeKey.StartsWith("UI.Content.", StringComparison.Ordinal))
                {
                    return true;
                }
                if (!node.TypeKey.StartsWith("UI.Field.", StringComparison.Ordinal))
                {
                    return false;
                }
                var idPort = node.Input.Single(port => port.Title == "Field ID");
                if (connections.Any(connection => ReferenceEquals(connection.Target, idPort)) || idPort.InitialValue is not string id || string.IsNullOrWhiteSpace(id))
                {
                    return false;
                }
                var label = node.Input.Single(port => port.Title == "Label").InitialValue as string;
                var type = node.TypeKey switch
                {
                    "UI.Field.Number" => NodePortType.Number,
                    "UI.Field.Boolean" => NodePortType.Boolean,
                    "UI.Field.MultiChoice" => NodePortType.List,
                    "UI.Field.File" or "UI.Field.Directory" or "UI.Field.SavePath" => NodePortType.Path,
                    _ => NodePortType.Text
                };
                var sensitive = node.Input.Single(port => port.Title == "Default").IsSensitive;
                fields.Add(new InteractionFieldSchema(id, string.IsNullOrWhiteSpace(label) ? id : label, type, sensitive));
                return true;
            }
        }
    }

    internal static void RefreshFromSnapshot(IEnumerable<FlowNodeViewModel> nodes, IReadOnlyDictionary<string, FlowNodeViewModel> created, WorkflowSnapshot snapshot)
    {
        var edges = new List<NodeConnectionViewModel>();
        foreach (var connection in snapshot.Connections)
        {
            var (_, source) = WorkflowReconstruction.ResolveEndpoint(nodes, created, connection.SourceInstanceId, connection.SourceConnectorTitle, true, false, connection.SourcePortId);
            var (_, target) = WorkflowReconstruction.ResolveEndpoint(nodes, created, connection.TargetInstanceId, connection.TargetConnectorTitle, false, false, connection.TargetPortId);
            if (source is not null && target is not null)
            {
                edges.Add(new NodeConnectionViewModel(source, target));
            }
        }
        Refresh(nodes, edges);
    }
}

public sealed record InteractionFieldSchema(string Id, string Label, NodePortType Type, bool Sensitive = false);