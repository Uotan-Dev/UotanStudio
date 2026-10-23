using UotanStudio.Services;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Proves the engine dispatches control flow by declared role rather than by type key, so plugin nodes receive identical semantics.<para>证明引擎按声明的角色而非类型键分派控制流，使插件节点获得相同语义。</para></summary>
public sealed class FlowRoleDispatchTests
{
    [Fact]
    public async Task PluginBranch_DeclaredRoles_SkipsUntakenBranchLikeBuiltInBranch()
    {
        var plugin = new DeclaredBranch("Plugin Branch", "Plugin.Custom.Branch");
        var taken = new Effect("Taken effect");
        var untaken = new Effect("Untaken effect");
        plugin.Input.Single(port => port.Title == "Condition").InitialValue = true;

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync(
            [plugin, taken, untaken],
            [
                Edge(plugin, "True", taken),
                Edge(plugin, "False", untaken)
            ], TestContext.Current.CancellationToken);

        Assert.True(result.Status == ExecutionStatus.Succeeded, result.ErrorMessage);
        Assert.True(taken.Executed);
        Assert.False(untaken.Executed);
        Assert.Equal(NodeExecutionStatus.Skipped, untaken.ExecutionStatus);
    }

    [Fact]
    public async Task PluginBranch_RenamedOutputs_StillDispatchByRole()
    {
        var plugin = new DeclaredBranch("Plugin Branch", "Plugin.Renamed.Branch");
        plugin.Output[0].Title = "Yes";
        plugin.Output[1].Title = "No";
        var taken = new Effect("Taken effect");
        var untaken = new Effect("Untaken effect");
        plugin.Input.Single(port => port.Title == "Condition").InitialValue = false;

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync(
            [plugin, taken, untaken],
            [
                Edge(plugin, "Yes", taken),
                Edge(plugin, "No", untaken)
            ], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.False(taken.Executed);
        Assert.True(untaken.Executed);
    }

    [Fact]
    public async Task BuiltInBranch_DeclaresRoles_UsedByEngine()
    {
        var branch = BuiltIn("Flow.Condition.Branch");

        Assert.Equal(NodeFlowKind.Branch, branch.FlowKind);
        Assert.Equal(NodeFlowRole.True, branch.Output.Single(port => port.Title == "True").FlowRole);
        Assert.Equal(NodeFlowRole.False, branch.Output.Single(port => port.Title == "False").FlowRole);
    }

    [Fact]
    public void BuiltInLoopAndGate_DeclareTheirRoles()
    {
        var loop = BuiltIn("Flow.Collection.Loop");
        var gate = BuiltIn("Flow.Applicability.Gate");

        Assert.Equal(NodeFlowKind.Loop, loop.FlowKind);
        Assert.Equal(NodeFlowRole.Body, loop.Output.Single(port => port.Title == "Body").FlowRole);
        Assert.Equal(NodeFlowRole.Completed, loop.Output.Single(port => port.Title == "Completed").FlowRole);
        Assert.Equal(NodeFlowKind.Branch, gate.FlowKind);
        Assert.Equal(NodeFlowRole.True, gate.Output.Single(port => port.Title == "Apply").FlowRole);
        Assert.Equal(NodeFlowRole.False, gate.Output.Single(port => port.Title == "Skip").FlowRole);
    }

    private static FlowNodeViewModel BuiltIn(string typeKey)
        => UotanStudio.Workflow.Registry.NodeFactoryRegistrar.Descriptors
            .Single(descriptor => descriptor.TypeKey == typeKey).Factory();

    private static NodeConnection Edge(FlowNodeViewModel source, string sourcePort, FlowNodeViewModel target)
        => new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = sourcePort,
            TargetInstanceId = target.InstanceId,
            TargetConnector = target.Input.First(port => port.PortType == NodePortType.Flow).Title
        };

    /// <summary>A plugin-authored branch that only declares roles, never a built-in type key.<para>只声明角色、从不使用内置类型键的插件分支节点。</para></summary>
    private sealed class DeclaredBranch : FlowNodeViewModel
    {
        internal DeclaredBranch(string title, string typeKey) : base(title, NodeCategory.Flow)
        {
            TypeKey = typeKey;
            Input.Add(new NodeConnectorViewModel { Title = "Flow", PortType = NodePortType.Flow });
            Input.Add(new NodeConnectorViewModel { Title = "Condition", PortType = NodePortType.Boolean });
            Output.Add(new NodeConnectorViewModel { Title = "True", PortType = NodePortType.Flow, FlowRole = NodeFlowRole.True });
            Output.Add(new NodeConnectorViewModel { Title = "False", PortType = NodePortType.Flow, FlowRole = NodeFlowRole.False });
        }

        public override NodeFlowKind FlowKind => NodeFlowKind.Branch;

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            WriteOutput(Output[0].Title, NodeDataUtil.ToBool(ReadInput("Condition")));
            return Task.CompletedTask;
        }
    }

    private sealed class Effect : FlowNodeViewModel
    {
        internal Effect(string title) : base(title, NodeCategory.Output)
        {
            Input.Add(new NodeConnectorViewModel { Title = "Flow", PortType = NodePortType.Flow });
        }

        internal bool Executed { get; private set; }

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