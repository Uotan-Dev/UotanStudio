using System.Runtime.CompilerServices;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// Holds the process-wide resource candidates offered by resource identifier ports. The candidate list is replaced
/// as a whole, so a reader never observes a partially rebuilt list. Ports registered through <see cref="Track"/>
/// receive every replacement while they are alive, and the registry keeps only a weak reference to each port, so a
/// released node neither keeps its ports reachable nor refreshes them.
/// <para>持有资源标识端口提供的进程级资源候选。候选列表整体替换，读取方不会观察到重建到一半的列表。经 <see cref="Track"/>
/// 登记的端口在存活期间接收每次替换，登记表只保留端口的弱引用，已释放的节点既不会保持端口存活，也不会再刷新它们。</para>
/// </summary>
public static class NodePortChoiceSource
{
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<NodeConnectorViewModel, NodeTarget> Targets = new();
    private static IReadOnlyList<NodePortChoice> resources = [];

    /// <summary>
    /// Gets the current resource candidates, ordered by identifier. Names are accepted with the identifiers but are
    /// not used: a port choice label is resolved from the language resources exactly like every other choice, so a
    /// resource name never becomes a persisted value.
    /// <para>取得当前资源候选，按标识排序。名称与标识一起传入但不参与取值：端口候选标签与其它候选一样从语言资源解析，
    /// 资源名称不会成为持久化取值。</para>
    /// </summary>
    /// <value>A snapshot that is never modified in place.
    /// <para>永不原地修改的快照。</para></value>
    public static IReadOnlyList<NodePortChoice> Resources => Volatile.Read(ref resources);

    /// <summary>
    /// Replaces the resource candidates with the given identifiers and display names; blank identifiers are dropped
    /// and duplicates keep the first name. Registered ports are refreshed before this method returns.
    /// <para>用给定的标识与显示名替换资源候选，空白标识被丢弃，重复标识保留首个名称。方法返回前已刷新登记过的端口。</para>
    /// </summary>
    /// <param name="resources">The identifiers and display names to offer.
    /// <para>要提供的标识与显示名。</para></param>
    public static void SetResources(IEnumerable<(string Id, string Name)> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Publish(Build(resources));
    }

    /// <summary>
    /// Drops every resource candidate and refreshes registered ports with the empty list.
    /// <para>清空全部资源候选，并用空列表刷新登记过的端口。</para>
    /// </summary>
    public static void Clear() => Publish([]);

    /// <summary>
    /// Registers a port so later replacements of <see cref="Resources"/> refresh its choices; registering the same
    /// port twice keeps one entry.
    /// <para>登记端口，使此后 <see cref="Resources"/> 的替换刷新其候选；同一端口重复登记只保留一条。</para>
    /// </summary>
    /// <param name="port">The port that mirrors the process-level candidates.
    /// <para>镜像进程级候选的端口。</para></param>
    public static void Track(NodeConnectorViewModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        lock (Gate)
        {
            _ = Targets.GetValue(port, static tracked => new NodeTarget(tracked));
        }
    }

    private static NodePortChoice[] Build(IEnumerable<(string Id, string Name)> resources)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<NodePortChoice> ordered = [];
        foreach (var (id, _) in resources)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                ordered.Add(new NodePortChoice(id));
            }
        }
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
        return [.. ordered];
    }

    private static void Publish(NodePortChoice[] candidates)
    {
        List<NodeTarget> registered = [];
        lock (Gate)
        {
            Volatile.Write(ref resources, candidates);
            foreach (var entry in Targets)
            {
                registered.Add(entry.Value);
            }
        }
        foreach (var target in registered)
        {
            target.Notify(candidates);
        }
    }

    private sealed class NodeTarget(NodeConnectorViewModel port)
    {
        public void Notify(IReadOnlyList<NodePortChoice> candidates) => port.ApplyTrackedChoices(candidates);
    }
}