using System.Globalization;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Map-over-list node. Runs one embedded sub-graph once per list element, feeding the element through the
/// sub-graph's <c>Item</c> input and collecting its <c>Result</c> output, so a batch step is expressed in the
/// graph instead of a script. Elements run sequentially in list order; the first failure stops the run.
/// <para>列表映射节点。对每个列表元素执行一次内嵌子图，经子图的 <c>Item</c> 输入送入元素并收集其
/// <c>Result</c> 输出，使批量步骤在图中表达而无需脚本。元素按列表顺序串行执行；首个失败即停止。</para>
/// </summary>
public sealed class ListMapNodeViewModel : FlowNodeViewModel
{
    /// <summary>Port title receiving the current element inside the embedded sub-graph.<para>内嵌子图中接收当前元素的端口标题。</para></summary>
    public const string ItemPortTitle = "Item";

    /// <summary>Port title carrying the per-element result out of the embedded sub-graph.<para>内嵌子图中携带单元素结果的输出端口标题。</para></summary>
    public const string ResultPortTitle = "Result";

    /// <summary>Upper bound on mapped elements, matching the loop guard so a malformed list cannot run unbounded.<para>映射元素数量上限，与循环守卫一致，避免异常列表无界执行。</para></summary>
    public const int MaxElements = NodeExecutionEngine.MaxLoopIterations;

    /// <summary>
    /// Creates a map node with the specified display title.
    /// <para>创建具有指定显示标题的映射节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public ListMapNodeViewModel(string title) : base(title, NodeCategory.Flow)
    {
    }

    /// <summary>
    /// Gets or sets the sub-graph executed once per element. The graph declares an <c>Item</c> input and a
    /// <c>Result</c> output; a graph missing either is rejected before the first element runs.
    /// <para>获取或设置对每个元素执行一次的子图。子图必须声明 <c>Item</c> 输入与 <c>Result</c> 输出；
    /// 缺少任一端口的子图会在首个元素执行前被拒绝。</para>
    /// </summary>
    /// <value>The embedded mapping graph, or <see langword="null"/> when the node has none.<para>内嵌映射图；节点没有时为 <see langword="null"/>。</para></value>
    public WorkflowSnapshot? MapGraph { get; set; }

    /// <summary>
    /// Creates the map-over-list node.
    /// <para>创建列表映射节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static ListMapNodeViewModel CreateMapNode()
    {
        var node = new ListMapNodeViewModel("Map List")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.List.Map"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "List", PortType = NodePortType.List, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Results", PortType = NodePortType.List });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Runs the embedded graph once per element and collects its results.
    /// <para>对每个元素执行内嵌图一次并收集结果。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The list input is missing or the embedded graph is unusable. <para>列表输入缺失或内嵌图不可用。</para></exception>
    /// <exception cref="InvalidDataException">The list exceeds <see cref="MaxElements"/>. <para>列表元素数超过上限。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "Data.List.Map", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var items = NodeDataUtil.ToList(ReadInput("List"));
        if (items.Count > MaxElements)
        {
            throw new InvalidDataException(
                $"The list carries {items.Count} elements, above the {MaxElements} element limit.  列表元素数超过上限。");
        }
        if (MapGraph is not { Nodes.Count: > 0 } graph)
        {
            throw new InvalidOperationException("The Map node has no embedded sub-graph.  映射节点缺少内嵌子图。");
        }

        if (items.Count == 0)
        {
            WriteOutput("Results", (IReadOnlyList<object?>)[]);
            WriteOutput("Count", 0d);
            context.Log.Write(LogLevel.Info, Title, "Mapped an empty list.  空列表映射。");
            return;
        }

        var results = new List<object?>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            results.Add(await MapOneAsync(graph, items[index], context).ConfigureAwait(false));
            context.ReportProgress((index + 1) / (double)items.Count);
        }

        WriteOutput("Results", (IReadOnlyList<object?>)results);
        WriteOutput("Count", (double)results.Count);
        context.Log.Write(LogLevel.Info, Title, $"Mapped {results.Count} element(s).  已完成列表映射。");
    }

    private async Task<object?> MapOneAsync(WorkflowSnapshot graph, object? element,
        INodeExecutionContext context)
    {
        var runtime = WorkflowRuntimeBuilder.Build(graph, preserveUnknown: false);
        if (runtime.Errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, runtime.Errors));
        }

        var feeders = runtime.Nodes.Where(node => node.Input.Any(port =>
            string.Equals(port.Title, ItemPortTitle, StringComparison.Ordinal))).ToList();
        if (feeders.Count == 0)
        {
            throw new InvalidOperationException(
                $"The embedded sub-graph declares no '{ItemPortTitle}' input.  内嵌子图未声明 Item 输入。");
        }
        var collectors = runtime.Nodes.Where(node => node.Output.Any(port =>
            string.Equals(port.Title, ResultPortTitle, StringComparison.Ordinal))).ToList();
        if (collectors.Count == 0)
        {
            throw new InvalidOperationException(
                $"The embedded sub-graph declares no '{ResultPortTitle}' output.  内嵌子图未声明 Result 输出。");
        }

        foreach (var feeder in feeders)
        {
            foreach (var port in feeder.Input.Where(port =>
                string.Equals(port.Title, ItemPortTitle, StringComparison.Ordinal)))
            {
                port.InitialValue = element;
                port.Data = element;
            }
        }

        var engine = new NodeExecutionEngine(context.Log);
        var result = await engine.ExecuteAsync(runtime.Nodes, runtime.Connections, context.CancellationToken,
            new NodeExecutionOptions
            {
                SharedPatches = context.Patches,
                Documents = context.Documents,
                Interactions = context.Interactions
            }).ConfigureAwait(false);

        if (result.Status == Services.Execution.ExecutionStatus.Cancelled)
        {
            throw new OperationCanceledException(context.CancellationToken);
        }
        if (result.Status is not (Services.Execution.ExecutionStatus.Succeeded or Services.Execution.ExecutionStatus.Terminated))
        {
            throw new InvalidOperationException(result.ErrorMessage ?? $"Map sub-graph ended with {result.Status}.");
        }

        var values = collectors
            .SelectMany(node => node.Output.Where(port =>
                string.Equals(port.Title, ResultPortTitle, StringComparison.Ordinal)))
            .Select(port => port.Data)
            .ToList();
        return values.Count == 1 ? values[0] : values;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1} elements)", Title, MapGraph?.Nodes.Count ?? 0);
}