using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Patching;

/// <summary>Inspects actual graphs rather than trusting plugin capability declarations.<para>检查实际的节点图，而不是轻信插件的能力声明。</para></summary>
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
            if (node.CompositePackage is { } embedded)
            {
                InspectPackage(embedded, visiting, ref hasPatches, ref safe, depth + 1);
            }
            else
            {
                InspectType(node.TypeKey, visiting, ref hasPatches, ref safe, depth + 1);
            }
        }
        visiting.Remove(package.NodeTypeKey);
    }

    private static void InspectType(string? type, HashSet<string> visiting,
        ref bool hasPatches, ref bool safe, int depth)
    {
        if (type is null) { safe = false; return; }
        var descriptor = NodeFactoryRegistrar.Descriptors.FirstOrDefault(candidate => candidate.TypeKey == type);
        if (NodeFactoryRegistrar.BuiltInTypeKeys.Contains(type) && descriptor is not null)
        {
            hasPatches |= descriptor.Capabilities.HasFlag(NodeCapability.PatchOperation);
            safe &= descriptor.Capabilities.HasFlag(NodeCapability.PatchSafe);
        }
        else if (NodeFactoryRegistrar.TryGetVisualPluginPackage(type, out var package))
        {
            InspectPackage(package, visiting, ref hasPatches, ref safe, depth);
        }
        else
        {
            safe = false;
        }
    }
}