using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Patching;

/// <summary>Inspects actual graphs rather than trusting plugin capability declarations.</summary>
internal static class PatchWorkflowPolicy
{
    internal static (bool HasPatches, bool Safe) Inspect(IReadOnlyList<FlowNodeViewModel> nodes)
    {
        var hasPatches = false;
        var safe = true;
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node is CompositeNodeViewModel composite)
            {
                InspectPackage(composite.Package, visiting, ref hasPatches, ref safe, 0);
            }
            else
            {
                InspectType(node.TypeKey, visiting, ref hasPatches, ref safe, 0);
            }
        }
        return (hasPatches, safe);
    }

    private static void InspectPackage(VisualPluginPackage package, HashSet<string> visiting,
        ref bool hasPatches, ref bool safe, int depth)
    {
        if (depth >= 32 || !visiting.Add(package.NodeTypeKey))
        {
            safe = false;
            return;
        }
        foreach (var node in package.Graph.Nodes)
        {
            InspectType(node.TypeKey, visiting, ref hasPatches, ref safe, depth + 1);
        }
        visiting.Remove(package.NodeTypeKey);
    }

    private static void InspectType(string? type, HashSet<string> visiting,
        ref bool hasPatches, ref bool safe, int depth)
    {
        if (type is null) { safe = false; return; }
        if (type.StartsWith("Patch.", StringComparison.Ordinal))
        {
            hasPatches = true;
            safe &= NodeFactoryRegistrar.BuiltInTypeKeys.Contains(type);
        }
        else if (NodeFactoryRegistrar.TryGetVisualPluginPackage(type, out var package))
        {
            InspectPackage(package, visiting, ref hasPatches, ref safe, depth);
        }
        else
        {
            safe &= type is "Data.Constant" or "Data.ProjectImage" or "Output.Log" or "Output.Progress"
                or "Flow.Start" or "Flow.End" or "Flow.Branch" or "Flow.Loop" or "Flow.Delay" or "Flow.Parallel" or "Flow.Terminate";
        }
    }
}