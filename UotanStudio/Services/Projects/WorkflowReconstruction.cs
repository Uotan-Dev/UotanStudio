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
            RestoreInstanceId(node, dto, restoreInstanceId);
            return true;
        }
        if (!NodeTypeRegistry.Contains(dto.TypeKey))
        {
            if (!preserveUnknown) return false;
            node = new MissingNodeViewModel(dto, $"Node type '{dto.TypeKey}' is unavailable.");
            RestoreInstanceId(node, dto, restoreInstanceId);
            RestoreInitialValues(node, dto.InputInitialValues, dto.InputInitialValuesByPortId);
            return true;
        }
        // A faulty registered factory must not become an "unknown type" warning during refactoring.
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
    /// Stable identities are authoritative: a missing endpoint must never bind to another node by name.
    /// </summary>
    internal static (FlowNodeViewModel? Node, NodeConnectorViewModel? Port) ResolveEndpoint(
        IEnumerable<FlowNodeViewModel> nodes, IReadOnlyDictionary<string, FlowNodeViewModel> created,
        string? instanceId, string title, bool output, bool fallbackOnMissingPort, string? portId = null)
    {
        FlowNodeViewModel? node = null;
        if (!string.IsNullOrEmpty(instanceId))
        {
            created.TryGetValue(instanceId, out node);
            return (node, FindPort(node, portId, title, output));
        }
        var port = FindPort(node, portId, title, output);
        if (node is not null && (!fallbackOnMissingPort || port is not null)) return (node, port);
        foreach (var candidate in nodes)
            if (FindPort(candidate, portId, title, output) is { } match) return (candidate, match);
        return (null, null);
    }

    private static NodeConnectorViewModel? FindPort(FlowNodeViewModel? node, string? portId, string title, bool output)
    {
        if (node is null) return null;
        var ports = output ? node.Output : node.Input;
        if (!string.IsNullOrWhiteSpace(portId))
        {
            return ports.FirstOrDefault(port => string.Equals(port.StableId, portId, StringComparison.Ordinal));
        }
        return ports.FirstOrDefault(port => string.Equals(port.Title, title, StringComparison.Ordinal));
    }
}