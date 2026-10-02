using System.Globalization;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Shared node creation, input conversion and endpoint lookup for canvas and CLI loading.</summary>
internal static class WorkflowReconstruction
{
    /// <summary>Creates a registered node or, when requested, a lossless unavailable-node placeholder.</summary>
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
        // A registered factory that throws surfaces here as an error, not the "unknown type" MissingNode above.
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

    /// <summary>Restores named data inputs without changing Flow inputs, runtime data or missing ports.</summary>
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

    /// <summary>Preserves invariant numeric conversion and lenient string fallback.</summary>
    internal static object ConvertInputValue(NodePortType portType, string value) => portType switch
    {
        NodePortType.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        NodePortType.Boolean when bool.TryParse(value, out var boolean) => boolean,
        _ => value
    };

    /// <summary>
    /// Resolves explicit identities exactly. Identity-free endpoints must have one unambiguous match.
    /// </summary>
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