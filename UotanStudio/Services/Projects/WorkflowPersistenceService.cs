using System.Text.Json;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Desktop node-to-data conversion; file persistence is provided by the project library.</summary>
public static class WorkflowPersistenceService
{
    internal static WorkflowSnapshot CloneForEditing(WorkflowSnapshot snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, WorkflowJsonContext.Default.WorkflowSnapshot);
        var clone = JsonSerializer.Deserialize(bytes, WorkflowJsonContext.Default.WorkflowSnapshot)!;
        RestoreResourceSources(snapshot, clone);
        return clone;
    }

    private static void RestoreResourceSources(WorkflowSnapshot source, WorkflowSnapshot target)
    {
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            if (source.Nodes[index].CompositePackage is not { } original
                || target.Nodes[index].CompositePackage is not { } copy)
            {
                continue;
            }
            // These capabilities come only from already loaded objects, never from serialized input.
            copy.BundlePath = original.BundlePath;
            copy.BundleRoot = original.BundleRoot;
            copy.Assets = [.. original.Assets.Select(asset => asset.Snapshot())];
            RestoreResourceSources(original.Graph, copy.Graph);
        }
    }

    public static Task<bool> SaveAsync(string path, IReadOnlyList<FlowNodeViewModel> nodes,
        IReadOnlyList<NodeConnectionViewModel> connections, CancellationToken cancellationToken = default)
        => WorkflowFile.SaveAsync(path, new() { Nodes = BuildNodeDtos(nodes), Connections = BuildConnectionDtos(nodes, connections) }, cancellationToken);
    public static Task<WorkflowSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
        => WorkflowFile.LoadAsync(path, cancellationToken);
    internal static List<NodeDto> BuildNodeDtos(IReadOnlyList<FlowNodeViewModel> nodes)
    {
        var result = new List<NodeDto>(nodes.Count);
        foreach (var n in nodes)
        {
            result.Add(new NodeDto
            {
                InstanceId = n.InstanceId,
                Title = n.Title,
                Category = n.Category.ToString(),
                X = n.Location.X,
                Y = n.Location.Y,
                TypeKey = n.TypeKey,
                CompositePackage = (n as CompositeNodeViewModel)?.Package,
                NodeValueType = n.IsConstantNode ? n.NodeValueType : null,
                NodeValue = n.IsConstantNode ? n.NodeValue : null,
                SelectedImage = n.IsProjectImageNode ? n.SelectedImage : null,
                CustomName = string.IsNullOrWhiteSpace(n.CustomName) ? null : n.CustomName,
                InputInitialValues = BuildInitialValueMap(n, useStableIds: false),
                InputInitialValuesByPortId = BuildInitialValueMap(n, useStableIds: true),
                InputPorts = BuildPortDtos(n.Input),
                OutputPorts = BuildPortDtos(n.Output)
            });
        }
        return result;
    }

    private static Dictionary<string, string>? BuildInitialValueMap(FlowNodeViewModel node, bool useStableIds)
    {
        Dictionary<string, string>? map = null;
        foreach (var connector in node.Input)
        {
            // Membership in Input is authoritative even before the node view initializes IsInput.
            if (connector.IsSensitive || connector.InitialValue is null
                || !node.IsUnavailable && connector.PortType is not (NodePortType.Path or NodePortType.Text or NodePortType.Number or NodePortType.Boolean))
            {
                continue;
            }

            var text = System.Convert.ToString(connector.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
            if (text is null)
            {
                continue;
            }

            if (useStableIds || node.Input.Count(port => port.Title == connector.Title) == 1)
            {
                (map ??= []).Add(useStableIds ? connector.StableId : connector.Title, text);
            }
        }

        return map;
    }

    private static List<NodePortDto> BuildPortDtos(IEnumerable<NodeConnectorViewModel> ports)
        => [.. ports.Select(port => new NodePortDto
        {
            Id = port.StableId,
            Title = port.Title,
            PortType = port.PortType.ToString(),
            MaxConnections = port.MaxConnections
        })];

    /// <summary>
    /// <para>由节点与连接集合构建连接 DTO 列表，解析每个连接器所属节点以持久化源/目标实例 ID（供无头执行等场景可靠重建拓扑）。</para>
    /// Builds connection DTOs from node and connection collections, resolving each connector's owning node so that
    /// source/target instance IDs can be persisted (reliable topology rebuild for headless execution and similar).
    /// </summary>
    public static List<ConnectionDto> BuildConnectionDtos(IReadOnlyList<FlowNodeViewModel> nodes, IReadOnlyList<NodeConnectionViewModel> connections)
    {
        var result = new List<ConnectionDto>(connections.Count);
        foreach (var c in connections)
        {
            if (c.Source == null || c.Target == null) continue;
            result.Add(new ConnectionDto
            {
                SourceConnectorTitle = c.Source.Title,
                TargetConnectorTitle = c.Target.Title,
                SourcePortId = c.Source.StableId,
                TargetPortId = c.Target.StableId,
                SourceInstanceId = FindOwnerNodeId(nodes, c.Source),
                TargetInstanceId = FindOwnerNodeId(nodes, c.Target)
            });
        }
        return result;
    }

    /// <summary>
    /// <para>按引用扫描查找连接器所属节点实例 ID；未找到返回 <c>null</c>。</para>
    /// Finds the owning node instance ID of a connector by reference scan; returns <c>null</c> if not found.
    /// </summary>
    private static string? FindOwnerNodeId(IReadOnlyList<FlowNodeViewModel> nodes, NodeConnectorViewModel connector)
    {
        foreach (var n in nodes)
        {
            if (n.Input.Contains(connector) || n.Output.Contains(connector))
            {
                return n.InstanceId;
            }
        }
        return null;
    }
}