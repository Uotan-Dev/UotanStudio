using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Interactions;

/// <summary>Checks the complete graph before any file, device or interaction effect is executed.</summary>
internal static class WorkflowInteractionPreflight
{
    internal static string? Validate(IReadOnlyList<FlowNodeViewModel> nodes, IWorkflowInteractionService host)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is InteractionNodeViewModel { SchemaError: { } schemaError })
            {
                return schemaError;
            }
            var error = node is CompositeNodeViewModel composite
                ? InspectPackage(composite.Package, host, visiting, 0)
                : InspectType(node.TypeKey, host, visiting, 0);
            if (error is not null)
            {
                return error;
            }
        }
        return null;
    }

    private static string? InspectPackage(VisualPluginPackage package, IWorkflowInteractionService host, HashSet<string> visiting, int depth)
    {
        if (depth >= 32 || !visiting.Add(package.NodeTypeKey))
        {
            return LanguageService.Instance.GetString("Node_Interaction_RecursiveGraph");
        }
        try
        {
            foreach (var node in package.Graph.Nodes)
            {
                var error = node.CompositePackage is { } embedded
                    ? InspectPackage(embedded, host, visiting, depth + 1)
                    : InspectType(node.TypeKey, host, visiting, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
            return null;
        }
        finally
        {
            visiting.Remove(package.NodeTypeKey);
        }
    }

    private static string? InspectType(string typeKey, IWorkflowInteractionService host, HashSet<string> visiting, int depth)
    {
        if (NodeFactoryRegistrar.TryGetVisualPluginPackage(typeKey, out var package))
        {
            return InspectPackage(package, host, visiting, depth);
        }
        var descriptor = NodeFactoryRegistrar.Descriptors.FirstOrDefault(candidate => candidate.TypeKey == typeKey);
        if (descriptor?.Capabilities.HasFlag(NodeCapability.RequiresInteraction) == true && !host.IsSupported)
        {
            return LanguageService.Instance.GetString("Node_Interaction_UnsupportedHost");
        }
        if (typeKey.StartsWith("UI.Progress.", StringComparison.Ordinal) || typeKey == "UI.Notification.Show")
        {
            if (host is not IWorkflowFeedbackService { SupportsFeedback: true })
            {
                return LanguageService.Instance.GetString("Node_Interaction_UnsupportedHost");
            }
        }
        return null;
    }
}