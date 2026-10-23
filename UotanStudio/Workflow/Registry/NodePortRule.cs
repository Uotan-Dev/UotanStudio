using UotanStudio.Workflow.Nodes;

namespace UotanStudio.Workflow.Registry;

/// <summary>
/// One declarative port-visibility condition: the controlling port title, the comparison applied to it and the values
/// the comparison uses. The rule is data rather than a closure so a host, a test or a third-party toolbox can read the
/// condition back and render the same panel without linking against this assembly's delegate instances.
/// <para>一条声明式端口可见性条件：控制端口标题、施加于它的比较方式，以及比较使用的取值。规则是数据而不是闭包，因此宿主、测试或第三方工具箱都能把它读回来渲染同一套面板，而不必链接本程序集的委托实例。</para>
/// </summary>
/// <remarks>
/// The condition is evaluated against a node by title, matching how <see cref="NodePortVisibility"/> resolves its
/// declaration: a table that references a title it does not declare is rejected when the port set is built, so a
/// mistyped title fails loudly instead of silently hiding every port the rule guards.
/// <para>条件按标题对节点求值，与 <see cref="NodePortVisibility"/> 解析声明的方式一致：引用了自身未声明标题的表会在构建端口集时被拒绝，因此拼错的标题会立即失败，而不是静默隐藏该规则守护的全部端口。</para>
/// </remarks>
/// <param name="PortTitle">
/// Title of the controlling input port.
/// <para>控制输入端口的标题。</para>
/// </param>
/// <param name="Comparison">
/// How the controlling value is compared.
/// <para>控制端口的比较方式。</para>
/// </param>
/// <param name="Values">
/// Values the comparison tests against; empty for <see cref="NodePortComparison.IsOn"/>.
/// <para>比较所测试的取值；<see cref="NodePortComparison.IsOn"/> 时为空。</para>
/// </param>
public sealed record NodePortRule(
    string PortTitle,
    NodePortComparison Comparison,
    params string[] Values)
{
    /// <summary>
    /// Gets whether the rule keeps the guarded port visible on the given node.
    /// <para>获取该规则在给定节点上是否让被守护端口保持可见。</para>
    /// </summary>
    /// <param name="node">The node whose ports are inspected. <para>待检查端口的节点。</para></param>
    /// <returns><see langword="true"/> when the port stays visible; a connection upstream always keeps it visible because the value is only decided at execution time.
    /// <para>端口保持可见时为 <see langword="true"/>；已连接上游时总是可见，因为取值要到执行期才确定。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.
    /// <para><paramref name="node"/> 为 <see langword="null"/>。</para></exception>
    public bool IsSatisfiedBy(FlowNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var port = node.Input.FirstOrDefault(input => string.Equals(input.Title, PortTitle, StringComparison.Ordinal));
        if (port is null)
        {
            return false;
        }

        return Comparison switch
        {
            NodePortComparison.IsOn => port.HasConnectedSource || NodeDataUtil.ToBool(port.InitialValue),
            NodePortComparison.IsOff => !port.HasConnectedSource && !NodeDataUtil.ToBool(port.InitialValue),
            NodePortComparison.Equals => !port.HasConnectedSource && MatchesAny(port, Values),
            NodePortComparison.NotEquals => !port.HasConnectedSource && !MatchesAny(port, Values),
            NodePortComparison.MayEqualAny => port.HasConnectedSource || MatchesAny(port, Values),
            _ => true
        };
    }

    private static bool MatchesAny(NodeConnectorViewModel port, string[] values)
    {
        var current = Convert.ToString(port.InitialValue, System.Globalization.CultureInfo.InvariantCulture);
        return values.Length == 0
            ? current is null
            : values.Any(value => string.Equals(current, value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Compares the rule as data. The record's synthesized equality compares the <see cref="Values"/> array by
    /// reference, so two rules built from identical values would never be equal; this override restores value equality
    /// and keeps <see cref="GetHashCode"/> consistent with it.
    /// <para>按数据比较规则。记录合成的相等性按引用比较 <see cref="Values"/> 数组，导致由相同取值构建的两条规则永不相等；
    /// 本重写恢复值相等语义，并使 <see cref="GetHashCode"/> 与之保持一致。</para>
    /// </summary>
    /// <param name="other">The rule to compare with. <para>要比较的规则。</para></param>
    /// <returns><see langword="true"/> when both rules carry the same title, comparison and values in order.
    /// <para>两条规则的标题、比较方式与取值顺序完全相同时为 <see langword="true"/>。</para></returns>
    public bool Equals(NodePortRule? other)
        => other is not null
            && string.Equals(PortTitle, other.PortTitle, StringComparison.Ordinal)
            && Comparison == other.Comparison
            && Values.AsSpan().SequenceEqual(other.Values);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PortTitle, StringComparer.Ordinal);
        hash.Add(Comparison);
        foreach (var value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns the rule that keeps a port visible while another port is on.
    /// <para>返回"另一端口开启时可见"的规则。</para>
    /// </summary>
    /// <param name="portTitle">Title of the controlling Boolean port. <para>控制布尔端口的标题。</para></param>
    /// <returns>The rule. <para>规则。</para></returns>
    public static NodePortRule IsOn(string portTitle) => new(portTitle, NodePortComparison.IsOn);

    /// <summary>
    /// Returns the rule that keeps a port visible while another port's value differs from the given value.
    /// <para>返回"另一端口取值不等于给定值"的规则。</para>
    /// </summary>
    /// <param name="portTitle">Title of the controlling port. <para>控制端口的标题。</para></param>
    /// <param name="value">The value that hides the guarded port. <para>触发隐藏的取值。</para></param>
    /// <returns>The rule. <para>规则。</para></returns>
    public static NodePortRule NotEquals(string portTitle, string value)
        => new(portTitle, NodePortComparison.NotEquals, value);

    /// <summary>
    /// Returns the rule that keeps a port visible while another port may equal any of the given values.
    /// <para>返回"另一端口可能等于任一给定值"的规则。</para>
    /// </summary>
    /// <param name="portTitle">Title of the controlling port. <para>控制端口的标题。</para></param>
    /// <param name="values">The values that keep the guarded port visible. <para>保持可见的取值。</para></param>
    /// <returns>The rule. <para>规则。</para></returns>
    public static NodePortRule MayEqualAny(string portTitle, params string[] values)
        => new(portTitle, NodePortComparison.MayEqualAny, values);
}

/// <summary>
/// How a <see cref="NodePortRule"/> compares the controlling port value.
/// <para><see cref="NodePortRule"/> 对控制端口取值的比较方式。</para>
/// </summary>
public enum NodePortComparison
{
    /// <summary>
    /// Visible while the Boolean port is on.
    /// <para>布尔端口开启时可见。</para>
    /// </summary>
    IsOn,

    /// <summary>
    /// Visible while the Boolean port is off.
    /// <para>布尔端口关闭时可见。</para>
    /// </summary>
    IsOff,

    /// <summary>
    /// Visible while the port value equals the single given value.
    /// <para>端口取值等于给定值时可见。</para>
    /// </summary>
    Equals,

    /// <summary>
    /// Visible while the port value differs from the given value.
    /// <para>端口取值不等于给定值时可见。</para>
    /// </summary>
    NotEquals,

    /// <summary>
    /// Visible while the port may equal any given value, treating an upstream connection as "may equal".
    /// <para>端口可能等于任一给定值时可见，已连接上游视为"可能等于"。</para>
    /// </summary>
    MayEqualAny
}