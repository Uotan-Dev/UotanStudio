using System.Collections.ObjectModel;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;
using Xunit;

namespace UotanStudio.Tests.ViewModels;

/// <summary>
/// <para>节点编辑器后端测试：覆盖执行引擎的数据流/拓扑排序/取消，以及撤销重做命令栈。</para>
/// Node-editor backend tests: cover the execution engine's data flow / topological sort / cancellation,
/// and the undo/redo command stack.
/// </summary>
public sealed class NodeEditorTests
{
    /// <summary>
    /// <para>测试用节点，允许注入自定义执行逻辑。</para>
    /// Test node allowing injection of custom execution logic.
    /// </summary>
    private sealed class TestNodeViewModel : FlowNodeViewModel
    {
        public Func<INodeExecutionContext, Task>? Action { get; set; }
        public TestNodeViewModel(string title, NodeCategory category) : base(title, category) { }
        public override Task ExecuteAsync(INodeExecutionContext context) => Action?.Invoke(context) ?? Task.CompletedTask;
    }

    /// <summary>
    /// <para>内存日志，用于构造执行引擎。</para>
    /// In-memory log used to construct the execution engine.
    /// </summary>
    private sealed class MemoryLog : IUnifiedLog
    {
        public List<string> Lines { get; } = [];
        public void Write(LogLevel level, string source, string message) => Lines.Add($"[{level}] {source}: {message}");
        public void Write(LogLevel level, string source, string format, params object[] args) => Write(level, source, string.Format(format, args));
        public void SetMinimumLevel(LogLevel level) { }
    }

    // ---- P0-1: NodeExecutionEngine ----

    [Fact]
    public async Task Engine_FlowsUpstreamOutputIntoTargetInput()
    {
        var log = new MemoryLog();
        var engine = new NodeExecutionEngine(log);

        var source = new TestNodeViewModel("Source", NodeCategory.Data);
        source.Output.Add(new NodeConnectorViewModel { Title = "Out" });
        source.Action = _ => { source.WriteOutput("Out", "hello"); return Task.CompletedTask; };

        var target = new TestNodeViewModel("Target", NodeCategory.Data);
        target.Input.Add(new NodeConnectorViewModel { Title = "In" });

        var nodes = new[] { source, target };
        var connections = new[]
        {
            new NodeConnection
            {
                SourceInstanceId = source.InstanceId,
                SourceConnector = "Out",
                TargetInstanceId = target.InstanceId,
                TargetConnector = "In"
            }
        };

        var result = await engine.ExecuteAsync(nodes, connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var input = Assert.Single(target.Input);
        Assert.Equal("hello", input.Data);
    }

    [Fact]
    public async Task Engine_EmptyGraph_SucceedsWithZero()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var result = await engine.ExecuteAsync([], Array.Empty<NodeConnection>(), TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExecutedCount);
    }

    [Fact]
    public async Task Engine_SingleNode_Succeeds()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var node = new TestNodeViewModel("Alone", NodeCategory.Flow);
        var result = await engine.ExecuteAsync([node], Array.Empty<NodeConnection>(), TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.ExecutedCount);
        Assert.Equal(NodeExecutionStatus.Succeeded, node.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_Cycle_ReturnsFailed()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var a = new TestNodeViewModel("A", NodeCategory.Flow);
        var b = new TestNodeViewModel("B", NodeCategory.Flow);
        var connections = new[]
        {
            new NodeConnection { SourceInstanceId = a.InstanceId, SourceConnector = "O", TargetInstanceId = b.InstanceId, TargetConnector = "I" },
            new NodeConnection { SourceInstanceId = b.InstanceId, SourceConnector = "O", TargetInstanceId = a.InstanceId, TargetConnector = "I" }
        };
        var result = await engine.ExecuteAsync([a, b], connections, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Engine_Cancellation_ReturnsCancelled()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var stuck = new TestNodeViewModel("Stuck", NodeCategory.Flow);
        stuck.Action = async context =>
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await Task.Delay(10, context.CancellationToken);
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            return;
        };

        using var cts = new CancellationTokenSource();
#pragma warning disable xUnit1051 // 本测试刻意使用独立 CTS 验证取消传播，而非 TestContext 令牌
        var runTask = engine.ExecuteAsync([stuck], Array.Empty<NodeConnection>(), cts.Token);
#pragma warning restore xUnit1051
        await Task.Delay(30, TestContext.Current.CancellationToken);
        cts.Cancel();

        var result = await runTask;
        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

    // ---- P0-1b: Engine real flow control ----

    /// <summary>
    /// <para>构造带 Flow 端口的测试节点。</para>
    /// Creates a test node with Flow ports.
    /// </summary>
    private static TestNodeViewModel FlowNode(string title, string typeKey, string[] inputs, string[] outputs, Func<INodeExecutionContext, Task>? action = null)
    {
        var node = new TestNodeViewModel(title, NodeCategory.Flow) { Action = action, TypeKey = typeKey };
        foreach (var input in inputs)
        {
            node.Input.Add(new NodeConnectorViewModel
            {
                Title = input,
                IsInput = true,
                PortType = input == "Condition" ? NodePortType.Boolean : input == "Count" ? NodePortType.Number : NodePortType.Flow
            });
        }
        foreach (var output in outputs)
        {
            node.Output.Add(new NodeConnectorViewModel { Title = output, PortType = NodePortType.Flow });
        }
        return node;
    }

    [Fact]
    public async Task Engine_Branch_SkipsUntakenSubGraph()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var branch = FlowNode("Branch", "Flow.Branch", ["Condition"], ["True", "False"]);
        var onTrue = FlowNode("OnTrue", "Test.OnTrue", ["Flow"], []);
        var onFalse = FlowNode("OnFalse", "Test.OnFalse", ["Flow"], []);
        var executed = new List<string>();

        branch.Input[0].InitialValue = true;
        branch.Action = _ => { branch.WriteOutput("True", true); return Task.CompletedTask; };
        onTrue.Action = _ => { executed.Add("true"); return Task.CompletedTask; };
        onFalse.Action = _ => { executed.Add("false"); return Task.CompletedTask; };

        var connections = new[]
        {
            Conn(branch, "True", onTrue, "Flow"),
            Conn(branch, "False", onFalse, "Flow")
        };

        var result = await engine.ExecuteAsync([branch, onTrue, onFalse], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["true"], executed);
        Assert.Equal(NodeExecutionStatus.Succeeded, onTrue.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, onFalse.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_Loop_IteratesBodySubGraph()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var loop = FlowNode("Loop", "Flow.Loop", ["Count"], ["Body", "Index"]);
        var body1 = FlowNode("Body1", "Test.Body1", ["Flow"], []);
        var body2 = FlowNode("Body2", "Test.Body2", ["Flow"], []);
        var after = FlowNode("After", "Test.After", ["Flow"], []);
        var runs = 0;

        loop.Input[0].InitialValue = 3.0;
        body1.Action = _ => { runs++; return Task.CompletedTask; };

        var connections = new[]
        {
            Conn(loop, "Body", body1, "Flow"),
            Conn(body1, "Flow", body2, "Flow"),
            Conn(loop, "Body", after, "Flow")
        };

        var result = await engine.ExecuteAsync([loop, body1, body2, after], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, runs);
        Assert.Equal(NodeExecutionStatus.Succeeded, body2.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_Loop_WritesIndexPerIteration()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var loop = FlowNode("Loop", "Flow.Loop", ["Count"], ["Body", "Index"]);
        var body = FlowNode("Body", "Test.Body", ["Flow"], []);
        var indexes = new List<object?>();

        loop.Input[0].InitialValue = 2.0;
        body.Action = _ => { indexes.Add(loop.Output.First(o => o.Title == "Index").Data); return Task.CompletedTask; };

        var connections = new[] { Conn(loop, "Body", body, "Flow") };

        var result = await engine.ExecuteAsync([loop, body], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, indexes.Count);
        Assert.Equal(0, indexes[0]);
        Assert.Equal(1, indexes[1]);
    }

    [Fact]
    public async Task Engine_Parallel_RunsBranchesConcurrently()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var parallel = FlowNode("Parallel", "Flow.Parallel", ["Flow"], ["Branch A", "Branch B"]);
        var a = FlowNode("A", "Test.A", ["Flow"], []);
        var b = FlowNode("B", "Test.B", ["Flow"], []);

        var insideParallel = 0;
        var peak = 0;

        Task Track(INodeExecutionContext _)
        {
            var current = Interlocked.Increment(ref insideParallel);
            peak = Math.Max(peak, current);
            Thread.Sleep(50);
            Interlocked.Decrement(ref insideParallel);
            return Task.CompletedTask;
        }

        a.Action = Track;
        b.Action = Track;

        var connections = new[]
        {
            Conn(parallel, "Branch A", a, "Flow"),
            Conn(parallel, "Branch B", b, "Flow")
        };

        var result = await engine.ExecuteAsync([parallel, a, b], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Succeeded, a.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, b.ExecutionStatus);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Engine_Loop_ClampsExcessiveIterations()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var loop = FlowNode("Loop", "Flow.Loop", ["Count"], ["Body", "Index"]);
        var body = FlowNode("Body", "Test.Body", ["Flow"], []);
        var runs = 0;

        loop.Input[0].InitialValue = (double)NodeExecutionEngine.MaxLoopIterations * 10;
        body.Action = _ => { runs++; return Task.CompletedTask; };

        var connections = new[] { Conn(loop, "Body", body, "Flow") };

        var result = await engine.ExecuteAsync([loop, body], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionEngine.MaxLoopIterations, runs);
    }

    /// <summary>
    /// <para>构造测试连接。</para>
    /// Builds a test connection.
    /// </summary>
    private static NodeConnection Conn(FlowNodeViewModel source, string sourceConnector, FlowNodeViewModel target, string targetConnector)
        => new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = sourceConnector,
            TargetInstanceId = target.InstanceId,
            TargetConnector = targetConnector
        };

    // ---- P0-2: Undo / Redo command stack ----

    [Fact]
    public void AddNodeCommand_ExecuteThenUndo_RestoresCollections()
    {
        var nodes = new ObservableCollection<FlowNodeViewModel>();
        var connections = new ObservableCollection<NodeConnectionViewModel>();
        var node = new TestNodeViewModel("N", NodeCategory.Data);

        var command = new AddNodeCommand(nodes, connections, node);
        command.Execute();
        Assert.Contains(node, nodes);

        command.Undo();
        Assert.Empty(nodes);
    }

    [Fact]
    public void RemoveNodeCommand_Undo_RestoresNodeAndConnections()
    {
        var nodes = new ObservableCollection<FlowNodeViewModel>();
        var connections = new ObservableCollection<NodeConnectionViewModel>();

        var node = new TestNodeViewModel("N", NodeCategory.Data);
        node.Output.Add(new NodeConnectorViewModel { Title = "Out" });
        node.Input.Add(new NodeConnectorViewModel { Title = "In" });
        nodes.Add(node);

        var conn = new NodeConnectionViewModel(node.Output[0], node.Input[0]);
        connections.Add(conn);

        var command = new RemoveNodeCommand(nodes, connections, node);
        command.Execute();
        Assert.Empty(nodes);
        Assert.Empty(connections);

        command.Undo();
        Assert.Contains(node, nodes);
        Assert.Contains(conn, connections);
    }

    [Fact]
    public void AddConnectionCommand_ExecuteThenUndo_Works()
    {
        var connections = new ObservableCollection<NodeConnectionViewModel>();
        var source = new NodeConnectorViewModel();
        var target = new NodeConnectorViewModel();
        var conn = new NodeConnectionViewModel(source, target);

        var command = new AddConnectionCommand(connections, conn);
        command.Execute();
        Assert.Contains(conn, connections);

        command.Undo();
        Assert.Empty(connections);
    }

    [Fact]
    public void MoveNodeCommand_ExecuteThenUndo_RestoresPosition()
    {
        var node = new TestNodeViewModel("N", NodeCategory.Data) { Location = new Point(10, 20) };
        var command = new MoveNodeCommand();
        command.AddTarget(node, new Point(100, 200));
        Assert.True(command.HasTargets);

        command.Execute();
        Assert.Equal(new Point(100, 200), node.Location);

        command.Undo();
        Assert.Equal(new Point(10, 20), node.Location);
    }

    [Fact]
    public void MoveNodeCommand_WithNoChange_HasNoTargets()
    {
        var node = new TestNodeViewModel("N", NodeCategory.Data) { Location = new Point(10, 20) };
        var command = new MoveNodeCommand();
        command.AddTarget(node, new Point(10, 20));
        Assert.False(command.HasTargets);
    }

    [Fact]
    public void CompoundCommand_UndoesInReverseOrder()
    {
        var nodes = new ObservableCollection<FlowNodeViewModel>();
        var connections = new ObservableCollection<NodeConnectionViewModel>();

        var a = new TestNodeViewModel("A", NodeCategory.Data);
        var b = new TestNodeViewModel("B", NodeCategory.Data);

        var commands = new IEditorCommand[]
        {
            new AddNodeCommand(nodes, connections, a),
            new AddNodeCommand(nodes, connections, b)
        };
        var compound = new CompoundCommand(commands);

        compound.Execute();
        Assert.Equal(2, nodes.Count);

        compound.Undo();
        Assert.Empty(nodes);
    }
}