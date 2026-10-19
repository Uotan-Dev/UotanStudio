using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// Composition helpers for port visibility predicates. Predicates locate ports through the referenced declaration,
/// so a declaration table guarantees that referenced ports live on the same node; a missing port throws immediately
/// instead of silently disabling the rule.
/// <para>端口可见性谓词的组合助手。谓词通过被引用的声明对象定位端口，声明表保证被引用端口与谓词属于同一节点，端口缺失时立即抛出而不是静默失效。</para>
/// </summary>
public static class NodePortVisibility
{
    /// <summary>
    /// Visible while the referenced port is on: either connected upstream or its initial value parses as true.
    /// <para>引用端口处于"开启"状态时可见：端口已连接上游，或其初始值被解析为真。</para>
    /// </summary>
    /// <param name="source">The referenced Boolean input declaration. <para>被引用的布尔输入端口声明。</para></param>
    /// <returns>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>. <para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para></returns>
    public static Func<FlowNodeViewModel, bool> IsOn(NodeInputDeclaration source) => node => ReadBoolean(node, source);

    /// <summary>
    /// Visible while every referenced port is on.
    /// <para>全部引用端口都处于"开启"状态时可见。</para>
    /// </summary>
    /// <param name="first">First referenced Boolean input declaration. <para>第一个被引用的布尔输入端口声明。</para></param>
    /// <param name="second">Second referenced Boolean input declaration. <para>第二个被引用的布尔输入端口声明。</para></param>
    /// <returns>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>. <para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para></returns>
    public static Func<FlowNodeViewModel, bool> IsOnAll(NodeInputDeclaration first, NodeInputDeclaration second)
        => node => ReadBoolean(node, first) && ReadBoolean(node, second);

    /// <summary>
    /// Visible while the referenced initial value does not equal the given value (or the port is connected upstream);
    /// a connection defers the value to execution time, so the rule must not hide the port.
    /// <para>引用端口的初值不等于给定值（或已连接上游）时可见；连接上游意味着执行期才确定取值，规则不能据此隐藏端口。</para>
    /// </summary>
    /// <param name="source">The referenced input declaration. <para>被引用的输入端口声明。</para></param>
    /// <param name="value">The value that hides the port. <para>触发隐藏的取值。</para></param>
    /// <returns>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>. <para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para></returns>
    public static Func<FlowNodeViewModel, bool> NotEquals(NodeInputDeclaration source, string value)
        => node => !EqualsValue(node, source, value);

    /// <summary>
    /// Visible while the referenced port may equal any given value: connected upstream or the initial value matches one
    /// of the values, because a connection defers the value to execution time.
    /// <para>引用端口可能等于任一给定值时可见：已连接上游，或初值命中给定值之一；连接上游意味着取值要到执行期才确定。</para>
    /// </summary>
    /// <param name="source">The referenced input declaration. <para>被引用的输入端口声明。</para></param>
    /// <param name="values">The values that keep the port visible. <para>保持可见的取值列表。</para></param>
    /// <returns>A predicate assignable to <see cref="NodeInputDeclaration.VisibleWhen"/>. <para>可挂到 <see cref="NodeInputDeclaration.VisibleWhen"/> 的可见性谓词。</para></returns>
    public static Func<FlowNodeViewModel, bool> MayEqualAny(NodeInputDeclaration source, params string[] values)
        => node => values.Any(value => MayEqual(node, source, value));

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