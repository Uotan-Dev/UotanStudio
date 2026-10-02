using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>端口可见性谓词的组合助手。谓词通过被引用的声明对象定位端口，声明表保证被引用端口与谓词属于同一节点，端口缺失时立即抛出而不是静默失效。</para>
/// Composition helpers for port visibility predicates. Predicates locate ports through the referenced declaration,
/// so a declaration table guarantees that referenced ports live on the same node; a missing port throws immediately
/// instead of silently disabling the rule.
/// </summary>
public static class NodePortVisibility
{
    /// <summary>
    /// <para>引用端口处于"开启"状态时可见：端口已连接上游，或其初始值被解析为真。</para>
    /// Visible while the referenced port is on: either connected upstream or its initial value parses as true.
    /// </summary>
    /// <param name="source"><para>被引用的布尔输入端口声明。</para>The referenced Boolean input declaration.</param>
    /// <returns><para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>.</returns>
    public static Func<FlowNodeViewModel, bool> IsOn(NodeInputDeclaration source) => node => ReadBoolean(node, source);

    /// <summary>
    /// <para>全部引用端口都处于"开启"状态时可见。</para>
    /// Visible while every referenced port is on.
    /// </summary>
    /// <param name="first"><para>第一个被引用的布尔输入端口声明。</para>First referenced Boolean input declaration.</param>
    /// <param name="second"><para>第二个被引用的布尔输入端口声明。</para>Second referenced Boolean input declaration.</param>
    /// <returns><para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>.</returns>
    public static Func<FlowNodeViewModel, bool> IsOnAll(NodeInputDeclaration first, NodeInputDeclaration second)
        => node => ReadBoolean(node, first) && ReadBoolean(node, second);

    /// <summary>
    /// <para>引用端口的初值不等于给定值（或已连接上游）时可见；连接上游意味着执行期才确定取值，规则不能据此隐藏端口。</para>
    /// Visible while the referenced initial value does not equal the given value (or the port is connected upstream);
    /// a connection defers the value to execution time, so the rule must not hide the port.
    /// </summary>
    /// <param name="source"><para>被引用的输入端口声明。</para>The referenced input declaration.</param>
    /// <param name="value"><para>触发隐藏的取值。</para>The value that hides the port.</param>
    /// <returns><para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>.</returns>
    public static Func<FlowNodeViewModel, bool> NotEquals(NodeInputDeclaration source, string value)
        => node => !EqualsValue(node, source, value);

    /// <summary>
    /// <para>引用端口初值不等于任一给定值（或已连接上游）时可见。</para>
    /// Visible while the referenced initial value equals none of the given values (or the port is connected upstream).
    /// </summary>
    /// <param name="source"><para>被引用的输入端口声明。</para>The referenced input declaration.</param>
    /// <param name="values"><para>触发隐藏的取值列表。</para>The values that hide the port.</param>
    /// <returns><para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>.</returns>
    public static Func<FlowNodeViewModel, bool> NotEqualsAny(NodeInputDeclaration source, params string[] values)
        => node => values.All(value => !EqualsValue(node, source, value));

    /// <summary>
    /// <para>引用端口可能等于任一给定值时可见：已连接上游，或初值命中给定值之一。与 <see cref="NotEqualsAny"/> 的差异在于连接上游时保持可见而非隐藏。</para>
    /// Visible while the referenced port may equal any given value: connected upstream or the initial value matches one
    /// of the values. Unlike <see cref="NotEqualsAny"/>, a connected upstream keeps the port visible.
    /// </summary>
    /// <param name="source"><para>被引用的输入端口声明。</para>The referenced input declaration.</param>
    /// <param name="values"><para>保持可见的取值列表。</para>The values that keep the port visible.</param>
    /// <returns><para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>.</returns>
    public static Func<FlowNodeViewModel, bool> MayEqualAny(NodeInputDeclaration source, params string[] values)
        => node => values.Any(value => MayEqual(node, source, value));

    /// <summary>
    /// <para>判断引用端口初值是否等于任一给定值，供更复杂的组合谓词复用。</para>
    /// Whether the referenced initial value equals any of the given values; reused by richer composed predicates.
    /// </summary>
    /// <param name="node"><para>拥有该端口的节点。</para>The node owning the port.</param>
    /// <param name="source"><para>被引用的输入端口声明。</para>The referenced input declaration.</param>
    /// <param name="values"><para>参与比较的取值列表。</para>The values to compare against.</param>
    /// <returns><para>命中任一取值时为 true。</para>true when any value matches.</returns>
    internal static bool EqualsAny(FlowNodeViewModel node, NodeInputDeclaration source, params string[] values)
        => values.Any(value => EqualsValue(node, source, value));

    /// <summary>
    /// <para>判断引用端口初值是否包含非空白文本。</para>
    /// Whether the referenced initial value contains non-whitespace text.
    /// </summary>
    /// <param name="node"><para>拥有该端口的节点。</para>The node owning the port.</param>
    /// <param name="source"><para>被引用的输入端口声明。</para>The referenced input declaration.</param>
    /// <returns><para>初值非空白时为 true。</para>true when the initial value is non-whitespace.</returns>
    internal static bool HasText(FlowNodeViewModel node, NodeInputDeclaration source) => HasText(node, source.Title);

    /// <summary>
    /// <para>判断指定标题端口的初值是否包含非空白文本，供声明自身初始化期间的谓词复用。</para>
    /// Whether the initial value of the port with the given title contains non-whitespace text; usable by predicates
    /// composed during the declaration's own initialization.
    /// </summary>
    /// <param name="node"><para>拥有该端口的节点。</para>The node owning the port.</param>
    /// <param name="title"><para>端口标题。</para>The port title.</param>
    /// <returns><para>初值非空白时为 true。</para>true when the initial value is non-whitespace.</returns>
    internal static bool HasText(FlowNodeViewModel node, string title)
        => !string.IsNullOrWhiteSpace(Convert.ToString(node.Input.First(input => input.Title == title).InitialValue));

    private static bool ReadBoolean(FlowNodeViewModel node, NodeInputDeclaration source)
    {
        var port = InputPort(node, source);
        return port.HasConnectedSource || NodeDataUtil.ToBool(port.InitialValue);
    }

    private static bool EqualsValue(FlowNodeViewModel node, NodeInputDeclaration source, string expected)
    {
        var port = InputPort(node, source);
        return !port.HasConnectedSource && string.Equals(Convert.ToString(port.InitialValue), expected, StringComparison.Ordinal);
    }

    private static bool MayEqual(FlowNodeViewModel node, NodeInputDeclaration source, string expected)
        => InputPort(node, source).HasConnectedSource || EqualsValue(node, source, expected);

    private static NodeConnectorViewModel InputPort(FlowNodeViewModel node, NodeInputDeclaration source)
        => node.Input.First(input => input.Title == source.Title);
}