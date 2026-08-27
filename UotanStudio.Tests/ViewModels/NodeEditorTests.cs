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