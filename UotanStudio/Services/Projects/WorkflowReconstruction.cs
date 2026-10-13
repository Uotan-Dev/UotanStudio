using System.Globalization;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Shared node creation, input conversion and endpoint lookup for canvas and CLI loading.
/// <para>画布与命令行加载共用的节点创建、输入转换与端点查找。</para></summary>
internal static class WorkflowReconstruction
{
    /// <summary>Creates a registered node or, when requested, a lossless unavailable-node placeholder.
    /// <para>创建已注册的节点，或按需创建无损保留的不可用节点占位符。</para></summary>
    /// <param name="dto">The serialized node description.<para>序列化后的节点描述。</para></param>
    /// <param name="node">The created node when this method returns <see langword="true"/>.<para>返回 <see langword="true"/> 时创建出的节点。</para></param>
    /// <param name="preserveUnknown">Whether unknown types yield a lossless placeholder.<para>未知类型是否生成无损占位符。</para></param>
    /// <param name="restoreInstanceId">Whether the stored instance identity is reapplied.<para>是否重新应用已存储的实例标识。</para></param>
    /// <returns><see langword="true"/> when a node was created.<para>成功创建节点时为 <see langword="true"/>。</para></returns>
    internal static bool TryCreateNode(NodeDto dto, out FlowNodeViewModel node, bool preserveUnknown = false,
        bool restoreInstanceId = false)
    {
        node = null!;
        if (string.IsNullOrEmpty(dto.TypeKey)) return false;
        if (dto.CompositePackage is { } embedded)
        {
            var errors = VisualPluginValidation.Validate(embedded);
            if (errors.Count > 0 || embedded.NodeTypeKey != dto.TypeKey)
            {
                throw new InvalidDataException("Invalid embedded composite: " + string.Join(Environment.NewLine, errors));
            }
            node = new CompositeNodeViewModel(embedded);
            node.CustomName = dto.CustomName ?? string.Empty;
            RestoreInstanceId(node, dto, restoreInstanceId);
            return true;
        }
        if (!NodeTypeRegistry.Contains(dto.TypeKey))
        {
            var alias = NodeTypeAlias.Find(dto.TypeKey);
            if (alias is null || !NodeTypeRegistry.Contains(alias.TargetTypeKey))
            {
                if (!preserveUnknown) return false;
                node = new MissingNodeViewModel(dto, $"Node type '{dto.TypeKey}' is unavailable.");
                node.CustomName = dto.CustomName ?? string.Empty;
                RestoreInstanceId(node, dto, restoreInstanceId);
                RestoreInitialValues(node, dto.InputInitialValues, dto.InputInitialValuesByPortId);
                return true;
            }
            node = NodeTypeRegistry.Create(alias.TargetTypeKey)!;
            NodeTypeAlias.ApplyDefaults(node, alias);
            if (!string.IsNullOrWhiteSpace(dto.CustomName))
            {
                node.CustomName = dto.CustomName;
            }
            RestoreInstanceId(node, dto, restoreInstanceId);
            return true;
        }
        node = NodeTypeRegistry.Create(dto.TypeKey)!;
        if (dto.NodeValueType is not null && node.IsConstantNode)
        {
            node.NodeValueType = dto.NodeValueType;
        }
        if (dto.NodeValue is not null && node.IsConstantNode)
        {
            node.NodeValue = dto.NodeValue;
        }
        if (dto.SelectedImage is not null && node.IsProjectImageNode)
        {
            node.SelectedImage = dto.SelectedImage;
        }
        if (!string.IsNullOrWhiteSpace(dto.CustomName))
        {
            node.CustomName = dto.CustomName;
        }
        RestoreInstanceId(node, dto, restoreInstanceId);
        return true;
    }

    private static void RestoreInstanceId(FlowNodeViewModel node, NodeDto dto, bool restoreInstanceId)
    {
        if (restoreInstanceId && !string.IsNullOrWhiteSpace(dto.InstanceId))
            node.InstanceId = dto.InstanceId;
    }

    /// <summary>Restores named data inputs without changing Flow inputs, runtime data or missing ports.
    /// <para>恢复具名数据输入，不改动 Flow 输入、运行时数据或缺失端口。</para></summary>
    /// <param name="node">The node whose inputs are restored.<para>要恢复输入的节点。</para></param>
    /// <param name="values">Initial values keyed by port title.<para>按端口标题索引的初始值。</para></param>
    /// <param name="valuesByPortId">Optional initial values keyed by stable port identity.<para>可选的按稳定端口标识索引的初始值。</para></param>
    internal static void RestoreInitialValues(FlowNodeViewModel node, Dictionary<string, string>? values,
        Dictionary<string, string>? valuesByPortId = null)
    {
        if (valuesByPortId is { Count: > 0 })
        {
            foreach (var (portId, value) in valuesByPortId)
            {
                var input = node.Input.FirstOrDefault(port => string.Equals(port.StableId, portId, StringComparison.Ordinal));
                if (input is null || input.PortType == NodePortType.Flow) continue;
                input.InitialValue = ConvertInputValue(input.PortType, value);
            }
            return;
        }

        if (values is { Count: > 0 })
        {
            foreach (var (title, value) in values)
            {
                var input = node.Input.FirstOrDefault(port => string.Equals(port.Title, title, StringComparison.Ordinal));
                if (input is null || input.PortType == NodePortType.Flow) continue;
                input.InitialValue = ConvertInputValue(input.PortType, value);
            }
        }
    }

    /// <summary>Preserves invariant numeric conversion and lenient string fallback.
    /// <para>按端口类型执行数值或布尔转换，其余保持原字符串。</para></summary>
    /// <param name="portType">The port type selecting the conversion.<para>决定转换方式的端口类型。</para></param>
    /// <param name="value">The raw string to convert.<para>要转换的原始字符串。</para></param>
    /// <returns>The converted object or the original string.<para>转换后的对象或原字符串。</para></returns>
    internal static object ConvertInputValue(NodePortType portType, string value) => portType switch
    {
        NodePortType.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        NodePortType.Boolean when bool.TryParse(value, out var boolean) => boolean,
        _ => value
    };

    /// <summary>
    /// Resolves explicit identities exactly. Identity-free endpoints must have one unambiguous match.
    /// <para>按显式标识精确解析端点；无标识的端点必须存在唯一匹配。</para>
    /// </summary>
    /// <param name="nodes">The candidate nodes for identity-free matching.<para>无标识匹配时的候选节点集合。</para></param>
    /// <param name="created">Nodes indexed by instance identity.<para>按实例标识索引的节点集合。</para></param>
    /// <param name="instanceId">The explicit node identity, if any.<para>可选的显式节点标识。</para></param>
    /// <param name="title">The port title used when no port identity matches.<para>端口标识未命中时使用的端口标题。</para></param>
    /// <param name="output">Whether an output or input port is sought.<para>要查找输出端口还是输入端口。</para></param>
    /// <param name="portId">The optional stable port identity.<para>可选的稳定端口标识。</para></param>
    /// <returns>The resolved node and port, or <see langword="null"/> pair when unmatched.
    /// <para>解析出的节点与端口；无法唯一匹配时二者均为 <see langword="null"/>。</para></returns>
    internal static (FlowNodeViewModel? Node, NodeConnectorViewModel? Port) ResolveEndpoint(
        IEnumerable<FlowNodeViewModel> nodes, IReadOnlyDictionary<string, FlowNodeViewModel> created,
        string? instanceId, string title, bool output, string? portId = null)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            created.TryGetValue(instanceId, out var node);
            return (node, FindPort(node, portId, title, output));
        }
        (FlowNodeViewModel? Node, NodeConnectorViewModel? Port) result = (null, null);
        foreach (var candidate in nodes)
        {
            if (FindPort(candidate, portId, title, output) is not { } match)
            {
                continue;
            }
            if (result.Node is not null)
            {
                return (null, null);
            }
            result = (candidate, match);
        }
        return result;
    }

    private static NodeConnectorViewModel? FindPort(FlowNodeViewModel? node, string? portId, string title, bool output)
    {
        if (node is null) return null;
        return WorkflowGraphValidation.ResolvePort(output ? node.Output : node.Input, portId, title);
    }
}