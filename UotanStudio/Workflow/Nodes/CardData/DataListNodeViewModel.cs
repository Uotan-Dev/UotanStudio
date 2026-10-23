using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Ordered-collection primitives: text-to-list splitting, element access, counting and record field access.
/// Values produced here use <see cref="NodePortType.List"/> and <see cref="NodePortType.Record"/>, so a
/// partition table read by a device node can feed batch processing without an ad-hoc script.
/// <para>有序集合原语：文本切分为列表、取元素、计数与记录字段访问。产出的值使用
/// <see cref="NodePortType.List"/> 与 <see cref="NodePortType.Record"/> 端口类型，使设备节点读到的分区表能被批量处理消费，无需临时脚本。</para>
/// </summary>
public sealed class DataListNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a collection node with the specified display title.
    /// <para>创建具有指定显示标题的集合节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public DataListNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// Creates the text-to-list split node.
    /// <para>创建文本切分列表节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DataListNodeViewModel CreateFromTextNode()
    {
        var node = new DataListNodeViewModel("Text to List")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.List.FromText"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Text", PortType = NodePortType.Text, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Separator",
            PortType = NodePortType.Text,
            InitialValue = "\n"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Trim",
            PortType = NodePortType.Boolean,
            InitialValue = true
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "List", PortType = NodePortType.List });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Creates the element access node that selects one list entry and splits it into name and value.
    /// <para>创建取元素节点：选中列表中的一项并拆分为名称与取值。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DataListNodeViewModel CreateElementAtNode()
    {
        var node = new DataListNodeViewModel("List Element")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.List.ElementAt"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "List", PortType = NodePortType.List, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Index", PortType = NodePortType.Number, InitialValue = 0 });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Value Separator",
            PortType = NodePortType.Text,
            InitialValue = "="
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Name", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Field", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Index", PortType = NodePortType.Number });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Creates the count node.
    /// <para>创建计数节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DataListNodeViewModel CreateCountNode()
    {
        var node = new DataListNodeViewModel("List Count")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.List.Count"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "List", PortType = NodePortType.List, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Executes the primitive selected by the node type key.
    /// <para>按节点类型键执行对应原语。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        switch (TypeKey)
        {
            case "Data.List.FromText":
                ExecuteFromText(context);
                break;
            case "Data.List.ElementAt":
                ExecuteElementAt(context);
                break;
            case "Data.List.Count":
                ExecuteCount(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteFromText(INodeExecutionContext context)
    {
        var text = Convert.ToString(ReadInput("Text"), CultureInfo.InvariantCulture) ?? string.Empty;
        var separator = Convert.ToString(ReadInput("Separator"), CultureInfo.InvariantCulture) ?? "\n";
        var trim = NodeDataUtil.ToBool(ReadInput("Trim"));

        var items = text
            .Split(separator.Length == 0 ? ["\n"] : [separator], StringSplitOptions.None)
            .Select(item => trim ? item.Trim() : item)
            .Where(item => item.Length > 0)
            .Select(item => (object?)item)
            .ToList();

        WriteOutput("List", (IReadOnlyList<object?>)items);
        WriteOutput("Count", (double)items.Count);
        context.Log.Write(LogLevel.Info, Title, $"Split into {items.Count} items on '{separator}'.  已切分为列表。");
    }

    private void ExecuteElementAt(INodeExecutionContext context)
    {
        var list = NodeDataUtil.ToList(ReadInput("List"));
        if (list.Count == 0)
        {
            throw new InvalidDataException("The List input is empty.  列表输入为空。");
        }

        var index = NodeDataUtil.ToInt(ReadInput("Index"), 0);
        if (index < 0 || index >= list.Count)
        {
            throw new InvalidDataException(
                $"List index {index} is out of range for {list.Count} items.  列表索引越界。");
        }

        var value = Convert.ToString(list[index], CultureInfo.InvariantCulture) ?? string.Empty;
        var separator = Convert.ToString(ReadInput("Value Separator"), CultureInfo.InvariantCulture) ?? "=";
        var split = string.IsNullOrEmpty(separator) ? -1 : value.IndexOf(separator, StringComparison.Ordinal);

        WriteOutput("Value", value);
        WriteOutput("Name", split >= 0 ? value[..split] : value);
        WriteOutput("Field", split >= 0 ? value[(split + separator.Length)..] : string.Empty);
        WriteOutput("Index", (double)index);
        WriteOutput("Count", (double)list.Count);
        context.Log.Write(LogLevel.Info, Title, $"Selected item {index} of {list.Count}.  已取列表元素。");
    }

    private void ExecuteCount(INodeExecutionContext context)
    {
        var list = NodeDataUtil.ToList(ReadInput("List"));
        WriteOutput("Count", (double)list.Count);
        context.Log.Write(LogLevel.Info, Title, $"List has {list.Count} items.  已统计列表元素。");
    }
}