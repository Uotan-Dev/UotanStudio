using System.Globalization;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Projects;

/// <summary>Shared node creation, input conversion and endpoint lookup for canvas and CLI loading.</summary>
internal static class WorkflowReconstruction
{
    /// <summary>Creates a registered node; registration and unknown-type diagnostics remain with the caller.</summary>
    internal static bool TryCreateNode(NodeDto dto, out FlowNodeViewModel node)
    {
        node = null!;
        if (string.IsNullOrEmpty(dto.TypeKey) || !NodeTypeRegistry.Contains(dto.TypeKey)) return false;
        // A faulty registered factory must not become an "unknown type" warning during refactoring.
        node = NodeTypeRegistry.Create(dto.TypeKey)!;
        return true;
    }

    /// <summary>Restores named data inputs without changing Flow inputs, runtime data or missing ports.</summary>
    internal static void RestoreInitialValues(FlowNodeViewModel node, Dictionary<string, string>? values)
    {
        if (values is not { Count: > 0 }) return;
        foreach (var (title, value) in values)
        {
            var input = node.Input.FirstOrDefault(port => string.Equals(port.Title, title, StringComparison.Ordinal));
            if (input is null || input.PortType == NodePortType.Flow) continue;
            input.InitialValue = ConvertInputValue(input.PortType, value);
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
    /// Resolves IDs first, then the first matching title. The canvas falls back on a missing port;
    /// the CLI preserves an ID-matched node and lets the execution engine handle a missing port.
    /// </summary>
    internal static (FlowNodeViewModel? Node, NodeConnectorViewModel? Port) ResolveEndpoint(
        IEnumerable<FlowNodeViewModel> nodes, IReadOnlyDictionary<string, FlowNodeViewModel> created,
        string? instanceId, string title, bool output, bool fallbackOnMissingPort)
    {
        FlowNodeViewModel? node = null;
        if (!string.IsNullOrEmpty(instanceId)) created.TryGetValue(instanceId, out node);
        var port = FindPort(node, title, output);
        if (node is not null && (!fallbackOnMissingPort || port is not null)) return (node, port);
        foreach (var candidate in nodes)
            if (FindPort(candidate, title, output) is { } match) return (candidate, match);
        return (null, null);
    }

    private static NodeConnectorViewModel? FindPort(FlowNodeViewModel? node, string title, bool output)
        => node is null ? null : (output ? node.Output : node.Input).FirstOrDefault(port => port.Title == title);
}
