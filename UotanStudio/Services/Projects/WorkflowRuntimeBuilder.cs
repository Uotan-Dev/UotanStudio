using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Reconstructs an executable workflow without creating an editor or mutating the source snapshot.
/// <para>重建可执行工作流，不创建编辑画布也不改动源快照。</para></summary>
internal static class WorkflowRuntimeBuilder
{
    internal static WorkflowRuntime Build(WorkflowSnapshot snapshot, bool preserveUnknown,
        bool restoreInstanceIds = true, bool canvasIdentityComparison = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.EnsureSupportedVersion();
        var nodes = new List<FlowNodeViewModel>(snapshot.Nodes.Count);
        var created = new Dictionary<string, FlowNodeViewModel>(
            canvasIdentityComparison ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        List<string> errors = [];

        foreach (var dto in snapshot.Nodes)
        {
            if (!WorkflowReconstruction.TryCreateNode(dto, out var node, preserveUnknown,
                    restoreInstanceId: restoreInstanceIds))
            {
                errors.Add($"Node type '{dto.TypeKey}' is unavailable.");
                continue;
            }

            WorkflowReconstruction.RestoreInitialValues(node, dto.InputInitialValues,
                dto.InputInitialValuesByPortId);
            node.Location = new Avalonia.Point(dto.X, dto.Y);
            nodes.Add(node);
            if (string.IsNullOrWhiteSpace(dto.InstanceId) || !created.TryAdd(dto.InstanceId, node))
            {
                errors.Add(LanguageService.Instance.GetString("Node_Graph_InvalidIdentity"));
            }
        }

        Interactions.InteractionSchemaResolver.RefreshFromSnapshot(nodes, created, snapshot);
        var connections = new List<NodeConnection>(snapshot.Connections.Count);
        var viewConnections = new List<NodeConnectionViewModel>(snapshot.Connections.Count);
        foreach (var dto in snapshot.Connections)
        {
            var (sourceNode, sourcePort) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                dto.SourceInstanceId, dto.SourceConnectorTitle, true, dto.SourcePortId);
            var (targetNode, targetPort) = WorkflowReconstruction.ResolveEndpoint(nodes, created,
                dto.TargetInstanceId, dto.TargetConnectorTitle, false, dto.TargetPortId);
            if (sourceNode is null || sourcePort is null || targetNode is null || targetPort is null)
            {
                errors.Add(LanguageService.Instance.GetString("Node_Graph_MissingEndpoint"));
                continue;
            }

            connections.Add(new NodeConnection
            {
                SourceInstanceId = sourceNode.InstanceId,
                SourceConnector = sourcePort.Title,
                SourcePortId = sourcePort.StableId,
                TargetInstanceId = targetNode.InstanceId,
                TargetConnector = targetPort.Title,
                TargetPortId = targetPort.StableId
            });
            viewConnections.Add(new(sourcePort, targetPort));
        }

        errors.AddRange(WorkflowGraphValidation.Validate(nodes, viewConnections).Select(issue => issue.DisplayText));
        return new(nodes, connections, created, errors, viewConnections);
    }
}

internal sealed record WorkflowRuntime(
    IReadOnlyList<FlowNodeViewModel> Nodes,
    IReadOnlyList<NodeConnection> Connections,
    IReadOnlyDictionary<string, FlowNodeViewModel> NodesBySerializedId,
    IReadOnlyList<string> Errors,
    IReadOnlyList<NodeConnectionViewModel> ViewConnections);