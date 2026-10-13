using System.Text.Json;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Converts workflow nodes and connections into persistence DTOs.<para>把工作流节点与连接转换为持久化用的 DTO。</para></summary>
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
    /// Builds connection DTOs from node and connection collections, resolving each connector's owning node
    /// into the persisted source and target instance IDs.
    /// <para>由节点与连接集合构建连接 DTO，解析每个连接器的所属节点作为持久化的源与目标实例 ID。</para>
    /// </summary>
    /// <param name="nodes">The nodes owning the connectors. <para>持有这些连接器的节点。</para></param>
    /// <param name="connections">The connections to convert. <para>待转换的连接。</para></param>
    /// <returns>The built connection DTO list. <para>构建出的连接 DTO 列表。</para></returns>
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
    /// Finds the owning node instance ID of a connector by reference scan; returns <c>null</c> if not found.
    /// <para>按引用扫描查找连接器所属节点实例 ID；未找到返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="nodes">The nodes to scan. <para>待扫描的节点。</para></param>
    /// <param name="connector">The connector whose owner is sought. <para>需要确定所属节点的连接器。</para></param>
    /// <returns>The owning node instance ID, or <c>null</c>. <para>所属节点的实例 ID，或 <c>null</c>。</para></returns>
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