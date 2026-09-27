using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Interactions;

/// <summary>Checks the complete graph before any file, device or interaction effect is executed.</summary>
internal static class WorkflowInteractionPreflight
{
    internal static string? Validate(IReadOnlyList<FlowNodeViewModel> nodes, IWorkflowInteractionService host, bool supportsDocuments = false)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is InteractionNodeViewModel { SchemaError: { } schemaError })
            {
                return schemaError;
            }
            var error = node is CompositeNodeViewModel composite
                ? InspectPackage(composite.Package, host, visiting, 0, supportsDocuments)
                : InspectType(node.TypeKey, host, visiting, 0, supportsDocuments);
            if (error is not null)
            {
                return error;
            }
        }
        return null;
    }

    /// <summary>
    /// <para>图内存在破坏性设备操作时请求用户显式确认；未获同意或宿主不支持交互时返回拒绝原因，否则返回 <c>null</c>。</para>
    /// Asks for explicit user consent when the graph contains destructive device operations; returns a rejection
    /// reason when consent is missing or the host cannot ask, otherwise <c>null</c>.
    /// </summary>
    internal static async Task<string?> ConfirmDestructiveAsync(
        IReadOnlyList<FlowNodeViewModel> nodes, IWorkflowInteractionService host, CancellationToken cancellationToken)
    {
        var operations = new SortedSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is CompositeNodeViewModel composite)
            {
                CollectDestructive(composite.Package, operations, visiting, 0);
            }
            else
            {
                CollectDestructive(node.TypeKey, operations, visiting, 0);
            }
        }
        if (operations.Count == 0)
        {
            return null;
        }
        if (!host.IsSupported)
        {
            return LanguageService.Instance.GetString("Node_Destructive_UnsupportedHost");
        }
        var request = new InteractionRequest
        {
            Form = new InteractionForm
            {
                Title = LanguageService.Instance.GetString("Node_Destructive_Title"),
                SubmitLabel = LanguageService.Instance.GetString("Node_Destructive_Confirm"),
                CancelLabel = LanguageService.Instance.GetString("Node_Destructive_Cancel"),
                Content = new InteractionElement
                {
                    Id = "message",
                    Kind = InteractionElementKind.Text,
                    Label = LanguageService.Instance.GetString("Node_Destructive_Message"),
                    Description = string.Join(Environment.NewLine, operations.Select(DisplayName))
                }
            }
        };
        var outcome = (await host.ShowAsync(request, cancellationToken).ConfigureAwait(false)).Outcome;
        return outcome == InteractionOutcome.Submitted
            ? null
            : LanguageService.Instance.GetString("Node_Destructive_Rejected");
    }

    private static void CollectDestructive(string typeKey, ISet<string> operations, HashSet<string> visiting, int depth)
    {
        if (depth >= 32)
        {
            return;
        }
        if (NodeFactoryRegistrar.TryGetVisualPluginPackage(typeKey, out var package))
        {
            CollectDestructive(package, operations, visiting, depth);
            return;
        }
        var descriptor = NodeFactoryRegistrar.Descriptors.FirstOrDefault(candidate => candidate.TypeKey == typeKey);
        if (descriptor?.Capabilities.HasFlag(NodeCapability.MutatesDevice) == true)
        {
            operations.Add(typeKey);
        }
    }

    private static void CollectDestructive(VisualPluginPackage package, ISet<string> operations, HashSet<string> visiting, int depth)
    {
        if (depth >= 32 || !visiting.Add(package.NodeTypeKey))
        {
            return;
        }
        try
        {
            foreach (var node in package.Graph.Nodes)
            {
                if (node.CompositePackage is { } embedded)
                {
                    CollectDestructive(embedded, operations, visiting, depth + 1);
                }
                else
                {
                    CollectDestructive(node.TypeKey, operations, visiting, depth + 1);
                }
            }
        }
        finally
        {
            visiting.Remove(package.NodeTypeKey);
        }
    }

    private static string DisplayName(string typeKey)
    {
        var key = "Node_" + typeKey.Replace('.', '_');
        var localized = LanguageService.Instance.GetString(key);
        return localized == key ? typeKey : localized;
    }

    private static string? InspectPackage(VisualPluginPackage package, IWorkflowInteractionService host, HashSet<string> visiting, int depth, bool supportsDocuments)
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
                    ? InspectPackage(embedded, host, visiting, depth + 1, supportsDocuments)
                    : InspectType(node.TypeKey, host, visiting, depth + 1, supportsDocuments);
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

    private static string? InspectType(string typeKey, IWorkflowInteractionService host, HashSet<string> visiting, int depth, bool supportsDocuments)
    {
        if (!supportsDocuments && typeKey.StartsWith("Workspace.Document.", StringComparison.Ordinal))
        {
            return LanguageService.Instance.GetString("Node_Document_Unsupported");
        }
        if (NodeFactoryRegistrar.TryGetVisualPluginPackage(typeKey, out var package))
        {
            return InspectPackage(package, host, visiting, depth, supportsDocuments);
        }
        var descriptor = NodeFactoryRegistrar.Descriptors.FirstOrDefault(candidate => candidate.TypeKey == typeKey);
        if (descriptor?.Capabilities.HasFlag(NodeCapability.MutatesDevice) == true && !host.IsSupported)
        {
            return LanguageService.Instance.GetString("Node_Destructive_UnsupportedHost");
        }
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