using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkflowGraphValidationTests
{
    [Fact]
    public async Task ExecuteAsync_TwoSourcesToOrdinaryInput_RejectsBeforeAnyNodeExecutes()
    {
        var calls = 0;
        var first = new TestNode(() => calls++);
        var second = new TestNode(() => calls++);
        var target = new TestNode(() => calls++);
        first.Output.Add(new() { Title = "Value", PortType = NodePortType.Text });
        second.Output.Add(new() { Title = "Value", PortType = NodePortType.Text });
        target.Input.Add(new() { Title = "Value", PortType = NodePortType.Text });

        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([first, second, target],
            [Edge(first, target), Edge(second, target)], TestContext.Current.CancellationToken);

        Assert.Equal(1, target.Input.Single().MaxConnections);
        Assert.Equal(0, first.Output.Single().MaxConnections);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("missing-node")]
    [InlineData("missing-port")]
    [InlineData("duplicate-edge")]
    [InlineData("incompatible")]
    [InlineData("duplicate-id")]
    [InlineData("max-connections")]
    [InlineData("cycle")]
    [InlineData("required")]
    public async Task ExecuteAsync_InvalidGraph_RejectsBeforeAnySideEffect(string invalidity)
    {
        var calls = 0;
        var source = new TestNode(() => calls++);
        var target = new TestNode(() => calls++);
        source.Output.Add(new() { Title = "Value", PortType = NodePortType.Text });
        target.Input.Add(new() { Title = "Value", PortType = NodePortType.Text });
        var edge = Edge(source, target);
        var edges = new List<NodeConnection> { edge };
        switch (invalidity)
        {
            case "missing-node":
                edges[0] = new()
                {
                    SourceInstanceId = "absent",
                    SourceConnector = "Value",
                    TargetInstanceId = target.InstanceId,
                    TargetConnector = "Value"
                };
                break;
            case "missing-port": source.Output.Clear(); break;
            case "duplicate-edge": edges.Add(edge); break;
            case "incompatible": target.Input[0].PortType = NodePortType.Flow; break;
            case "duplicate-id": target.InstanceId = source.InstanceId; break;
            case "max-connections":
                target.Input[0].MaxConnections = 1;
                source.Output.Add(new() { Title = "Other", PortType = NodePortType.Text });
                edges.Add(new()
                {
                    SourceInstanceId = source.InstanceId,
                    SourceConnector = "Other",
                    TargetInstanceId = target.InstanceId,
                    TargetConnector = "Value"
                });
                break;
            case "cycle":
                source.Input.Add(new() { Title = "Value", PortType = NodePortType.Text });
                target.Output.Add(new() { Title = "Value", PortType = NodePortType.Text });
                edges.Add(Edge(target, source));
                break;
            case "required":
                edges.Clear();
                target.Input[0].IsRequired = true;
                break;
        }

        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([source, target], edges,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, calls);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateTitlesWithStableIds_RoutesExactPorts()
    {
        var source = new TestNode(() => { });
        source.Output.Add(new() { Title = "Value", PortId = "first", PortType = NodePortType.Text });
        source.Output.Add(new() { Title = "Value", PortId = "second", PortType = NodePortType.Text });
        source.Action = () =>
        {
            source.Output[0].Data = "wrong";
            source.Output[1].Data = "correct";
        };
        var target = new TestNode(() => { });
        target.Input.Add(new() { Title = "Value", PortId = "first", PortType = NodePortType.Text });
        target.Input.Add(new() { Title = "Value", PortId = "second", PortType = NodePortType.Text });

        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([source, target],
            [new()
            {
                SourceInstanceId = source.InstanceId, SourceConnector = "obsolete caption", SourcePortId = "second",
                TargetInstanceId = target.InstanceId, TargetConnector = "", TargetPortId = "second"
            }], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(target.Input[0].Data);
        Assert.Equal("correct", target.Input[1].Data);
    }

    [Fact]
    public void ResolveEndpoint_AmbiguousIdentityFreeEndpoint_RejectsInsteadOfPickingFirst()
    {
        var first = new TestNode(() => { });
        var second = new TestNode(() => { });
        first.Output.Add(new() { Title = "Value" });
        second.Output.Add(new() { Title = "Value" });
        var nodes = new[] { first, second };

        var resolved = WorkflowReconstruction.ResolveEndpoint(nodes, nodes.ToDictionary(node => node.InstanceId,
            node => (FlowNodeViewModel)node), null, "Value", true);

        Assert.Null(resolved.Node);
        Assert.Null(resolved.Port);
    }

    [Fact]
    public void Validate_InactiveRequiredParameter_DoesNotRejectUnusedValue()
    {
        var node = new TestNode(() => { });
        node.Input.Add(new()
        {
            Title = "Inactive",
            IsRequired = true,
            IsVisibleWhen = _ => false,
            PortType = NodePortType.Text
        });

        Assert.Empty(WorkflowGraphValidation.Validate([node], [], forExecution: true));
    }

    [Theory]
    [InlineData(false, ExecutionStatus.Succeeded, NodeExecutionStatus.Skipped)]
    [InlineData(true, ExecutionStatus.PartiallySucceeded, NodeExecutionStatus.Failed)]
    public async Task ExecuteAsync_ConditionalRequiredInput_ValidatesOnlyTakenBranch(
        bool condition, ExecutionStatus expectedResult, NodeExecutionStatus expectedNode)
    {
        var branch = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Flow.Condition.Branch").Factory();
        branch.Input.Single(port => port.Title == "Condition").InitialValue = condition;
        var calls = 0;
        var target = new TestNode(() => calls++);
        target.Input.Add(new() { Title = "Flow", PortType = NodePortType.Flow });
        target.Input.Add(new() { Title = "Path", PortType = NodePortType.Path, IsRequired = true });

        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([branch, target],
            [new NodeConnection
            {
                SourceInstanceId = branch.InstanceId, SourceConnector = "True",
                TargetInstanceId = target.InstanceId, TargetConnector = "Flow"
            }], TestContext.Current.CancellationToken);

        Assert.Equal(expectedResult, result.Status);
        Assert.Equal(expectedNode, target.ExecutionStatus);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_SensitiveSource_DoesNotPersistentlyMarkOrdinaryInput()
    {
        var source = new TestNode(() => { });
        source.Output.Add(new()
        {
            Title = "Secret",
            PortType = NodePortType.Text,
            IsSensitive = true,
            PortId = "secret"
        });
        source.Action = () => source.Output.Single().Data = "private";
        var target = new TestNode(() => { });
        target.Input.Add(new() { Title = "Value", PortType = NodePortType.Text, PortId = "value" });
        var input = target.Input.Single();
        var engine = new NodeExecutionEngine(new SilentLog());

        var first = await engine.ExecuteAsync([source, target],
            [new NodeConnection
            {
                SourceInstanceId = source.InstanceId, SourceConnector = "Secret",
                TargetInstanceId = target.InstanceId, TargetConnector = "Value"
            }], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, first.Status);
        Assert.False(input.IsSensitive);
        Assert.True(input.IsRuntimeSensitive);
        input.InitialValue = "ordinary";
        var saved = WorkflowPersistenceService.BuildNodeDtos([target]).Single();
        Assert.Equal("ordinary", saved.InputInitialValuesByPortId!["value"]);

        var second = await engine.ExecuteAsync([target], [], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, second.Status);
        Assert.False(input.IsRuntimeSensitive);
        Assert.Equal("ordinary", input.DataPreview);
    }

    [Fact]
    public void Validate_DuplicateStablePortId_RejectsAmbiguousSchema()
    {
        var node = new TestNode(() => { });
        node.Input.Add(new() { Title = "First", PortId = "same" });
        node.Input.Add(new() { Title = "Second", PortId = "same" });

        Assert.NotEmpty(WorkflowGraphValidation.Validate([node], []));
    }

    private static NodeConnection Edge(FlowNodeViewModel source, FlowNodeViewModel target) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = "Value",
        TargetInstanceId = target.InstanceId,
        TargetConnector = "Value"
    };

    private sealed class TestNode(Action action) : FlowNodeViewModel("Test", NodeCategory.Data)
    {
        public Action Action { get; set; } = action;
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            Action();
            return Task.CompletedTask;
        }
    }

    private sealed class SilentLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}