using System.Runtime.CompilerServices;
using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Workflow.Registry;

/// <summary>
/// Holds the process-wide resource candidates offered by resource identifier ports. The candidate list is replaced
/// as a whole, so a reader never observes a partially rebuilt list. An owner token scopes publication: the first
/// publisher becomes the current owner, only that owner can replace or clear the owned list, and clearing it releases
/// the scope for the next publisher. Ports registered through <see cref="Track"/> receive every replacement while they
/// are alive, and the registry keeps only a weak reference to each port, so a released node neither keeps its ports
/// reachable nor refreshes them.
/// <para>持有资源标识端口提供的进程级资源候选。候选列表整体替换，读取方不会观察到重建到一半的列表。owner 令牌限定发布作用域：
/// 首个发布者成为当前 owner，只有该 owner 能替换或清空其列表，清空后作用域释放给下一个发布者。经 <see cref="Track"/>
/// 登记的端口在存活期间接收每次替换，登记表只保留端口的弱引用，已释放的节点既不会保持端口存活，也不会再刷新它们。</para>
/// </summary>
public static class NodePortChoiceSource
{
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<NodeConnectorViewModel, NodeTarget> Targets = new();
    private static IReadOnlyList<NodePortChoice> resources = [];
    private static object? currentOwner;

    /// <summary>
    /// Gets the current resource candidates, ordered by identifier and labeled with the published resource name.
    /// <para>取得当前资源候选，按标识排序并以发布的资源名称作为标签。</para>
    /// </summary>
    /// <value>A snapshot that is never modified in place.
    /// <para>永不原地修改的快照。</para></value>
    public static IReadOnlyList<NodePortChoice> Resources => Volatile.Read(ref resources);

    /// <summary>
    /// Replaces the resource candidates with the given identifiers and display names without an owner scope; this
    /// also releases the current owner scope. Blank identifiers are dropped and duplicates keep the first name.
    /// Registered ports are refreshed before this method returns.
    /// <para>在不占用 owner 作用域的情况下用给定的标识与显示名替换资源候选，同时释放当前 owner 作用域；空白标识被丢弃，
    /// 重复标识保留首个名称。方法返回前已刷新登记过的端口。</para>
    /// </summary>
    /// <param name="resources">The identifiers and display names to offer.
    /// <para>要提供的标识与显示名。</para></param>
    public static void SetResources(IEnumerable<(string Id, string Name)> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Publish(Build(resources), null, claim: true);
    }

    /// <summary>
    /// Replaces the resource candidates for an owner scope. The first publisher becomes the current owner; while a
    /// different owner holds the scope this call is a no-op, so two package editors cannot overwrite each other.
    /// <para>为 owner 作用域替换资源候选。首个发布者成为当前 owner；当另一个 owner 持有作用域时本调用不生效，
    /// 因此两个包编辑器不会互相覆盖。</para>
    /// </summary>
    /// <param name="owner">The token identifying the publishing scope, usually the owning window.
    /// <para>标识发布作用域的令牌，通常是拥有它的窗口。</para></param>
    /// <param name="resources">The identifiers and display names to offer.
    /// <para>要提供的标识与显示名。</para></param>
    public static void SetResources(object owner, IEnumerable<(string Id, string Name)> resources)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resources);
        Publish(Build(resources), owner, claim: true);
    }

    /// <summary>
    /// Drops every resource candidate, releases the current owner scope and refreshes registered ports with the
    /// empty list.
    /// <para>清空全部资源候选、释放当前 owner 作用域，并用空列表刷新登记过的端口。</para>
    /// </summary>
    public static void Clear() => Publish([], null, claim: true);

    /// <summary>
    /// Drops every resource candidate when the caller owns the scope; a different current owner keeps its
    /// candidates, so closing one editor never clears another editor's list.
    /// <para>调用方拥有作用域时清空全部资源候选；当前 owner 是别人时保留其候选，因此关闭一个编辑器不会清空另一个编辑器的列表。</para>
    /// </summary>
    /// <param name="owner">The token that must be the current owner for the clear to apply.
    /// <para>必须等于当前 owner 才能生效的令牌。</para></param>
    public static void Clear(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Publish([], owner, claim: false);
    }

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
        foreach (var (id, name) in resources)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                ordered.Add(new NodePortChoice(id, displayLabel: name));
            }
        }
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
        return [.. ordered];
    }

    private static void Publish(NodePortChoice[] candidates, object? ownerToken, bool claim)
    {
        List<NodeTarget> registered = [];
        lock (Gate)
        {
            if (ownerToken is null)
            {
                currentOwner = null;
            }
            else if (currentOwner is not null && !ReferenceEquals(currentOwner, ownerToken))
            {
                return;
            }
            else
            {
                currentOwner = claim ? ownerToken : null;
            }
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