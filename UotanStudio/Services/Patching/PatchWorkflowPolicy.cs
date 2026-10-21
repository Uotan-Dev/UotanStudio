using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Patching;

/// <summary>Inspects actual graphs rather than trusting plugin capability declarations.<para>检查实际的节点图，而不是轻信插件的能力声明。</para></summary>
internal static class PatchWorkflowPolicy
{
    /// <summary>
    /// Inspects the whole graph as one unit.
    /// <para>把整张图作为一个整体检查。</para>
    /// </summary>
    /// <param name="nodes">Nodes of the graph.<para>图的节点。</para></param>
    /// <returns>Whether any node writes patches and whether every node is patch-safe.
    /// <para>是否存在写入补丁的节点，以及每个节点是否都补丁安全。</para></returns>
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

    /// <summary>
    /// Inspects each execution stage on its own so a device stage may follow a patch stage.
    /// <para>逐个执行阶段单独检查，使设备阶段可以跟在补丁阶段之后。</para>
    /// </summary>
    /// <param name="nodes">Nodes of the graph, grouped here by <see cref="FlowNodeViewModel.ExecutionStage"/>.
    /// <para>图的节点；此处按 <see cref="FlowNodeViewModel.ExecutionStage"/> 分组。</para></param>
    /// <returns>One verdict per stage in ascending stage order; empty for an empty graph.
    /// <para>按阶段升序排列的每阶段判定；图为空时为空列表。</para></returns>
    internal static IReadOnlyList<PatchStagePolicy> InspectStages(IReadOnlyList<FlowNodeViewModel> nodes)
    {
        List<PatchStagePolicy> policies = [];
        foreach (var stage in nodes.GroupBy(node => node.ExecutionStage).OrderBy(group => group.Key))
        {
            var hasPatches = false;
            var safe = true;
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in stage)
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
            policies.Add(new PatchStagePolicy(stage.Key, hasPatches, safe));
        }
        return policies;
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

/// <summary>
/// Patch verdict for one execution stage.
/// <para>单个执行阶段的补丁判定。</para>
/// </summary>
/// <param name="Stage">Stage index the verdict covers.<para>该判定覆盖的阶段索引。</para></param>
/// <param name="HasPatches">Whether the stage writes patches.<para>该阶段是否写入补丁。</para></param>
/// <param name="Safe">Whether every node of the stage is patch-safe.<para>该阶段所有节点是否都补丁安全。</para></param>
internal readonly record struct PatchStagePolicy(int Stage, bool HasPatches, bool Safe);