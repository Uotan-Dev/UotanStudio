namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// <para>把后端返回的扁平节点统一成树。所有平台共用这一份实现，因此排序、孤儿提升与父子环处理
/// 只有一处定义；父标识缺失、自引用或成环的节点一律提升为根，绝不静默丢弃任何节点。</para>
/// Unifies the flat nodes returned by any backend into a tree. Every platform shares this one
/// implementation, so ordering, orphan hoisting and parent-cycle handling are defined exactly once:
/// nodes with a missing, self-referencing or cyclic parent are promoted to roots, and no node is ever
/// silently dropped.
/// </summary>
public static class UsbTopologyTree
{
    /// <summary>
    /// Builds display-ordered branches from flat nodes.
    /// <para>由扁平节点构建按显示顺序排列的分支。</para>
    /// </summary>
    /// <param name="nodes">
    /// The flat nodes of one enumeration; duplicate instance ids keep their first occurrence.
    /// <para>单次枚举的扁平节点；重复实例标识保留首次出现。</para>
    /// </param>
    /// <returns>
    /// The roots in display order. Every input node with a non-blank instance id appears exactly once.
    /// <para>按显示顺序排列的根；每个实例标识非空的输入节点在结果中恰好出现一次。</para>
    /// </returns>
    public static IReadOnlyList<UsbTopologyBranch> Build(IReadOnlyList<UsbTopologyNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var byId = new Dictionary<string, UsbTopologyNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.InstanceId))
            {
                byId.TryAdd(node.InstanceId, node);
            }
        }

        var children = new Dictionary<string, List<UsbTopologyNode>>(StringComparer.Ordinal);
        var roots = new List<UsbTopologyNode>();
        foreach (var node in byId.Values)
        {
            if (node.ParentInstanceId is { Length: > 0 } parent &&
                !string.Equals(parent, node.InstanceId, StringComparison.Ordinal) &&
                byId.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    list = [];
                    children[parent] = list;
                }

                list.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (var node in UnreachableFromRoots(byId.Values, roots, children))
        {
            if (node.ParentInstanceId is { Length: > 0 } cycleParent &&
                children.TryGetValue(cycleParent, out var siblings))
            {
                siblings.RemoveAll(sibling =>
                    string.Equals(sibling.InstanceId, node.InstanceId, StringComparison.Ordinal));
            }

            roots.Add(node);
        }

        return Branch(roots, children, []);
    }

    /// <summary>
    /// Keeps branches that contain at least one node accepted by <paramref name="matches"/>, preserving
    /// the ancestor chain so a filtered tree still shows where a device is plugged in.
    /// <para>保留至少含一个被 <paramref name="matches"/> 接受节点的分支，并保留祖先链，
    /// 使过滤后的树仍能看出设备插在哪个口上。</para>
    /// </summary>
    /// <param name="roots">
    /// The unfiltered roots. <para>未过滤的根。</para>
    /// </param>
    /// <param name="matches">
    /// Predicate over a single node. <para>针对单个节点的判定。</para>
    /// </param>
    /// <returns>
    /// The filtered roots in the original order. <para>按原顺序过滤后的根。</para>
    /// </returns>
    public static IReadOnlyList<UsbTopologyBranch> Retain(IReadOnlyList<UsbTopologyBranch> roots,
        Func<UsbTopologyNode, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(matches);

        IReadOnlyList<UsbTopologyBranch> Walk(IReadOnlyList<UsbTopologyBranch> branches)
        {
            var kept = new List<UsbTopologyBranch>();
            foreach (var branch in branches)
            {
                var children = Walk(branch.Children);
                if (matches(branch.Node) || children.Count > 0)
                {
                    kept.Add(new UsbTopologyBranch(branch.Node, children));
                }
            }

            return kept;
        }

        return Walk(roots);
    }

    /// <summary>
    /// Flattens a tree back into nodes in display order.
    /// <para>把树按显示顺序展平回节点序列。</para>
    /// </summary>
    /// <param name="roots">
    /// The tree roots. <para>树根。</para>
    /// </param>
    /// <returns>
    /// Every node once, parents before children. <para>每个节点一次，父节点先于子节点。</para>
    /// </returns>
    public static IReadOnlyList<UsbTopologyNode> Flatten(IReadOnlyList<UsbTopologyBranch> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var nodes = new List<UsbTopologyNode>();
        void Walk(IReadOnlyList<UsbTopologyBranch> branches)
        {
            foreach (var branch in branches)
            {
                nodes.Add(branch.Node);
                Walk(branch.Children);
            }
        }

        Walk(roots);
        return nodes;
    }

    private static IReadOnlyList<UsbTopologyBranch> Branch(IReadOnlyList<UsbTopologyNode> nodes,
        Dictionary<string, List<UsbTopologyNode>> children, HashSet<string> path)
    {
        var branches = new List<UsbTopologyBranch>();
        foreach (var node in Order(nodes))
        {
            if (!path.Add(node.InstanceId))
            {
                branches.Add(new UsbTopologyBranch(node, []));
                continue;
            }

            var childNodes = children.TryGetValue(node.InstanceId, out var list) ? list : [];
            branches.Add(new UsbTopologyBranch(node, Branch(childNodes, children, path)));
            path.Remove(node.InstanceId);
        }

        return branches;
    }

    private static IEnumerable<UsbTopologyNode> Order(IEnumerable<UsbTopologyNode> nodes)
        => nodes.OrderBy(node => Rank(node.Kind))
            .ThenBy(node => node.InterfaceNumber ?? 0)
            .ThenBy(node => node.VendorId ?? 0)
            .ThenBy(node => node.ProductId ?? 0)
            .ThenBy(node => node.InstanceId, StringComparer.Ordinal);

    private static int Rank(UsbTopologyNodeKind kind) => kind switch
    {
        UsbTopologyNodeKind.HostController => 0,
        UsbTopologyNodeKind.RootHub => 1,
        UsbTopologyNodeKind.Hub => 2,
        UsbTopologyNodeKind.Device => 3,
        UsbTopologyNodeKind.Function => 4,
        _ => 5
    };

    private static IReadOnlyList<UsbTopologyNode> UnreachableFromRoots(
        IReadOnlyCollection<UsbTopologyNode> allNodes, IReadOnlyList<UsbTopologyNode> roots,
        Dictionary<string, List<UsbTopologyNode>> children)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        foreach (var root in roots)
        {
            pending.Push(root.InstanceId);
        }

        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!reachable.Add(id))
            {
                continue;
            }

            if (children.TryGetValue(id, out var list))
            {
                foreach (var child in list)
                {
                    pending.Push(child.InstanceId);
                }
            }
        }

        return [.. allNodes.Where(node => !reachable.Contains(node.InstanceId))];
    }
}