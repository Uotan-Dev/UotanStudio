using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>常量输入节点视图模型，在属性面板配置一个固定值并广播到下游节点。</para>
/// Constant-input node view model; configures a fixed value in the properties panel and broadcasts it downstream.
/// </summary>
public class ConstantNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的常量输入节点。</para>
    /// Creates a constant-input node with the specified title.
    /// </summary>
    /// <param name="title">节点的显示标题。<para>The display title of the node.</para></param>
    public ConstantNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// <para>创建默认的常量输入节点，输出 <c>Text</c> 类型常量。</para>
    /// Creates a default constant node outputting a <c>Text</c>-typed constant.
    /// </summary>
    public static ConstantNodeViewModel CreateConstantNode()
    {
        var node = new ConstantNodeViewModel("Constant")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.Value.Constant",
            NodeValueType = "Text"
        };
        node.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text, IsInput = false });
        return node;
    }

    /// <summary>
    /// <para>获取该节点为常量输入节点，属性面板据此显示常量编辑区。</para>
    /// Gets whether the node is a constant-input node; the properties panel shows the constant editor for it.
    /// </summary>
    public override bool IsConstantNode => true;

    /// <summary>
    /// <para>获取或设置常量值的类型（Text/Number/Boolean/Path）。变化时同步更新输出端口类型。</para>
    /// Gets or sets the constant's value type (Text/Number/Boolean/Path); the output port type follows.
    /// </summary>
    public override string NodeValueType
    {
        get => base.NodeValueType;
        set
        {
            if (!IsEditingLocked && base.NodeValueType != value)
            {
                base.NodeValueType = value;
                if (Output.Count > 0)
                {
                    Output[0].PortType = ResolvePortType(value);
                }
            }
        }
    }

    /// <summary>
    /// <para>把字符串类型名解析为端口类型。</para>
    /// Resolves a value-type name to a port type.
    /// </summary>
    /// <param name="typeName">值类型名。<para>The value type name.</para></param>
    /// <returns>对应的端口类型。<para>The matching port type.</para></returns>
    private static NodePortType ResolvePortType(string typeName)
        => typeName switch
        {
            "Number" => NodePortType.Number,
            "Boolean" => NodePortType.Boolean,
            "Path" => NodePortType.Path,
            _ => NodePortType.Text
        };

    /// <summary>
    /// <para>执行常量节点：把配置值按类型写入 <c>Value</c> 输出端口。</para>
    /// Executes the constant node, writing the configured value to the <c>Value</c> output port.
    /// </summary>
    /// <param name="context">执行上下文。<para>The execution context.</para></param>
    /// <returns>表示异步执行的任务。<para>A task representing the asynchronous execution.</para></returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        WriteOutput("Value", NodeValueType switch
        {
            "Number" when double.TryParse(NodeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            "Boolean" when bool.TryParse(NodeValue, out var b) => b,
            _ => NodeValue
        });
        context.Log.Write(LogLevel.Info, Title, $"Constant '{NodeValueType}': {NodeValue}  已输出常量。");
        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}