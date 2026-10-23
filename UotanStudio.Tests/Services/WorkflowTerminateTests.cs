using UotanStudio.Services;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Verifies that a Terminate node stops the workflow without being reported as user cancellation.<para>验证终止节点停止工作流时不会被报告为用户取消。</para></summary>
public sealed class WorkflowTerminateTests
{
    [Fact]
    public async Task Terminate_Requested_ReportsTerminatedNotCancelled()
    {
        var node = Factory("Flow.Execution.Terminate");

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Terminated, result.Status);
        Assert.NotEqual(ExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Terminate_WithReason_ReportsReasonOnResult()
    {
        var node = Factory("Flow.Execution.Terminate");
        Seed(node, "Reason", "license rejected");

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Terminated, result.Status);
        Assert.Equal("license rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task Terminate_InGraphWithEarlierNode_StopsBeforeAnyLaterNode()
    {
        var first = new RecordingNode("First effect");
        var terminate = Factory("Flow.Execution.Terminate");
        var later = new RecordingNode("Later effect");
        ConnectFlow(first, terminate);
        Edges.Add(new()
        {
            SourceInstanceId = first.InstanceId,
            SourceConnector = "Completed",
            TargetInstanceId = later.InstanceId,
            TargetConnector = "Flow"
        });

        var result = await new NodeExecutionEngine(new TestLog())
            .ExecuteAsync([first, terminate, later], [.. Edges], TestContext.Current.CancellationToken);

        Assert.True(result.Status == ExecutionStatus.Terminated, result.ErrorMessage);
        Assert.True(first.Executed);
        Assert.NotEqual(NodeExecutionStatus.Succeeded, later.ExecutionStatus);
    }

    [Fact]
    public async Task UserCancellation_StillReportsCancelled()
    {
        var node = Factory("Flow.Execution.Delay");
        Seed(node, "Milliseconds", 60_000d);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await new NodeExecutionEngine(new TestLog())
            .ExecuteAsync([node], [], cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public void TerminatedResult_CarriesStatusAndReason()
    {
        var result = ExecutionResult.Terminated("stop here");

        Assert.Equal(ExecutionStatus.Terminated, result.Status);
        Assert.Equal("stop here", result.ErrorMessage);
    }

    private static FlowNodeViewModel Factory(string typeKey)
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();

    private static void Seed(FlowNodeViewModel node, string inputPort, object? value)
        => node.Input.Single(port => port.Title == inputPort).InitialValue = value;

    private static void ConnectFlow(FlowNodeViewModel source, FlowNodeViewModel target)
        => Edges.Add(new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = source.Output.FirstOrDefault(port => port.PortType == NodePortType.Flow)?.Title ?? "Completed",
            TargetInstanceId = target.InstanceId,
            TargetConnector = target.Input.First(port => port.PortType == NodePortType.Flow).Title
        });

    private static Task<ExecutionResult> RunAsync(FlowNodeViewModel node)
    {
        Edges.Clear();
        return new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);
    }

    private static readonly List<NodeConnection> Edges = [];

    private sealed class RecordingNode : FlowNodeViewModel
    {
        internal bool Executed { get; private set; }

        internal RecordingNode(string title) : base(title, NodeCategory.Output)
        {
            Input.Add(new NodeConnectorViewModel { Title = "Flow", PortType = NodePortType.Flow });
            Output.Add(new NodeConnectorViewModel { Title = "Completed", PortType = NodePortType.Flow });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            Executed = true;
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