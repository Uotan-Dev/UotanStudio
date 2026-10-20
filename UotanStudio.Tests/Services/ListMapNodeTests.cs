using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Verifies that the map node runs its embedded sub-graph once per element and collects each result.<para>验证映射节点对每个元素执行一次内嵌子图并收集各自结果。</para></summary>
public sealed class ListMapNodeTests
{
    [Fact]
    public async Task Map_ThreeElements_RunsSubGraphOncePerElementAndOrdersResults()
    {
        var node = MapNode([.. new object?[] { "a", "b", "c" }]);

        var result = await RunAsync(node);

        Assert.True(result.Status == ExecutionStatus.Succeeded, result.ErrorMessage);
        Assert.Equal(["a!", "b!", "c!"], ReadResults(node).Cast<string>());
        Assert.Equal(3d, node.Output.Single(port => port.Title == "Count").Data);
    }

    [Fact]
    public async Task Map_EmptyList_SucceedsWithoutRunningSubGraph()
    {
        var node = MapNode([]);

        var result = await RunAsync(node);

        Assert.True(result.Status == ExecutionStatus.Succeeded, result.ErrorMessage);
        Assert.Empty(ReadResults(node));
        Assert.Equal(0d, node.Output.Single(port => port.Title == "Count").Data);
    }

    [Fact]
    public async Task Map_WithoutEmbeddedGraph_Fails()
    {
        var node = Factory();
        Seed(node, "List", new object[] { "x" });

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Map_SubGraphWithoutItemInput_Fails()
    {
        var node = Assert.IsType<ListMapNodeViewModel>(Factory());
        Seed(node, "List", new object[] { "x" });
        node.MapGraph = new WorkflowSnapshot { Nodes = [] };

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Map_ElementFailure_StopsAndReportsFailure()
    {
        var node = MapNode(["ok"], transformer: "throw");

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public void Map_Descriptor_DeclaresDataCategoryAndListOutputs()
    {
        var descriptor = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Data.List.Map");

        Assert.Equal("Data", descriptor.CategoryPath);
        var node = descriptor.Factory();
        Assert.Equal(NodePortType.List, node.Input.Single(port => port.Title == "List").PortType);
        Assert.Equal(NodePortType.List, node.Output.Single(port => port.Title == "Results").PortType);
    }

    [Fact]
    public async Task Map_BeyondElementLimit_FailsBeforeExecuting()
    {
        var node = MapNode(Enumerable.Range(0, ListMapNodeViewModel.MaxElements + 1)
            .Select(index => (object?)index.ToString()).ToList());

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Null(node.Output.Single(port => port.Title == "Count").Data);
    }

    [Fact]
    public void SaveAndLoad_MapNodeWithEmbeddedGraph_RestoresTheSubGraph()
    {
        var node = MapNode(["a"]);

        var dto = Assert.Single(WorkflowPersistenceService.BuildNodeDtos([node]));
        var restored = Assert.Single(WorkflowRuntimeBuilder.Build(new WorkflowSnapshot { Nodes = [dto] },
            preserveUnknown: false).Nodes);

        var map = Assert.IsType<ListMapNodeViewModel>(restored);
        Assert.Equal(1, map.MapGraph?.Nodes.Count);
        Assert.Equal(TransformerKey, map.MapGraph?.Nodes[0].TypeKey);
    }

    [Fact]
    public async Task Map_LoadedFromSnapshot_RunsTheRestoredSubGraph()
    {
        var node = MapNode(["a", "b"]);
        var dto = Assert.Single(WorkflowPersistenceService.BuildNodeDtos([node]));

        var restored = Assert.IsType<ListMapNodeViewModel>(Assert.Single(WorkflowRuntimeBuilder.Build(
            new WorkflowSnapshot { Nodes = [dto] }, preserveUnknown: false).Nodes));
        Seed(restored, "List", new object?[] { "a", "b" });
        var result = await RunAsync(restored);

        Assert.True(result.Status == ExecutionStatus.Succeeded, result.ErrorMessage);
        Assert.Equal(["a!", "b!"], ReadResults(restored).Cast<string>());
    }

    private static FlowNodeViewModel Factory()
        => NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Data.List.Map").Factory();

    private static ListMapNodeViewModel MapNode(List<object?> items, string transformer = "append")
    {
        var node = Assert.IsType<ListMapNodeViewModel>(Factory());
        Seed(node, "List", items);
        node.MapGraph = BuildGraph(transformer);
        return node;
    }

    /// <summary>A one-node sub-graph that reads Item, transforms it and writes Result.<para>读取 Item、变换后写出 Result 的单节点子图。</para></summary>
    private static WorkflowSnapshot BuildGraph(string transformer)
    {
        NodeTypeRegistry.Register(TransformerKey, () => new TransformerNode(transformer));
        var inner = new TransformerNode(transformer) { InstanceId = "inner", TypeKey = TransformerKey };
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([inner]) };
        snapshot.Nodes[0].TypeKey = TransformerKey;
        return snapshot;
    }

    private const string TransformerKey = "Test.Map.Transformer";

    private static void Seed(FlowNodeViewModel node, string inputPort, object? value)
        => node.Input.Single(port => port.Title == inputPort).InitialValue = value;

    private static IReadOnlyList<object?> ReadResults(FlowNodeViewModel node)
        => Assert.IsAssignableFrom<IReadOnlyList<object?>>(node.Output.Single(port => port.Title == "Results").Data);

    private static Task<ExecutionResult> RunAsync(FlowNodeViewModel node)
        => new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);

    /// <summary>Test node standing in for an arbitrary user-authored mapping step.<para>代表用户任意映射步骤的测试节点。</para></summary>
    private sealed class TransformerNode : FlowNodeViewModel
    {
        private readonly string _mode;

        internal TransformerNode(string mode) : base("Transformer", NodeCategory.Data)
        {
            _mode = mode;
            TypeKey = "Test.Map.Transformer";
            Input.Add(new NodeConnectorViewModel { Title = "Item", PortType = NodePortType.Any });
            Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Any });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            if (_mode == "throw")
            {
                throw new InvalidOperationException("transform failed");
            }
            var item = Convert.ToString(ReadInput("Item"), System.Globalization.CultureInfo.InvariantCulture);
            WriteOutput("Result", item + "!");
            return Task.CompletedTask;
        }
    }

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}