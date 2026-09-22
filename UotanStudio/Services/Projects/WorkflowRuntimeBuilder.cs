using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Reconstructs an executable workflow without creating an editor or mutating the source snapshot.</summary>
internal static class WorkflowRuntimeBuilder
{
    internal static WorkflowRuntime Build(WorkflowSnapshot snapshot, bool preserveUnknown)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.EnsureSupportedVersion();
        var nodes = new List<FlowNodeViewModel>(snapshot.Nodes.Count);
        var created = new Dictionary<string, FlowNodeViewModel>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var dto in snapshot.Nodes)
        {
            if (!WorkflowReconstruction.TryCreateNode(dto, out var node, preserveUnknown,
                    restoreInstanceId: true))
            {
                errors.Add($"Node type '{dto.TypeKey}' is unavailable.");
                continue;
            }

            WorkflowReconstruction.RestoreInitialValues(node, dto.InputInitialValues,
                dto.InputInitialValuesByPortId);
            node.Location = new Avalonia.Point(dto.X, dto.Y);
            nodes.Add(node);
            if (!string.IsNullOrWhiteSpace(dto.InstanceId)) created[dto.InstanceId] = node;
        }

        Interactions.InteractionSchemaResolver.RefreshFromSnapshot(nodes, created, snapshot);
        var connections = new List<NodeConnection>(snapshot.Connections.Count);
        foreach (var dto in snapshot.Connections)
        {
            var (sourceNode, sourcePort) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                dto.SourceInstanceId, dto.SourceConnectorTitle, true, false, dto.SourcePortId);
            var (targetNode, targetPort) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                dto.TargetInstanceId, dto.TargetConnectorTitle, false, false, dto.TargetPortId);
            if (sourceNode is null || sourcePort is null || targetNode is null || targetPort is null)
            {
                errors.Add($"Connection '{dto.SourceInstanceId}:{dto.SourcePortId ?? dto.SourceConnectorTitle}' to "
                    + $"'{dto.TargetInstanceId}:{dto.TargetPortId ?? dto.TargetConnectorTitle}' cannot be resolved.");
                continue;
            }

            connections.Add(new NodeConnection
            {
                SourceInstanceId = sourceNode.InstanceId,
                SourceConnector = sourcePort.Title,
                TargetInstanceId = targetNode.InstanceId,
                TargetConnector = targetPort.Title
            });
        }

        return new(nodes, connections, created, errors);
    }
}

internal sealed record WorkflowRuntime(
    IReadOnlyList<FlowNodeViewModel> Nodes,
    IReadOnlyList<NodeConnection> Connections,
    IReadOnlyDictionary<string, FlowNodeViewModel> NodesBySerializedId,
    IReadOnlyList<string> Errors);