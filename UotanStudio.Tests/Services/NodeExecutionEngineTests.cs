using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Tests workflow data flow, scheduling, cancellation, and control flow.
/// <para>验证工作流的数据传递、调度、取消和流控算法。</para>
/// </summary>
public sealed class NodeExecutionEngineTests
{
    [Fact]
    public async Task ExecuteAsync_ConnectedNullOutput_DoesNotReuseIgnoredInputDefault()
    {
        var source = new TestNodeViewModel("source", NodeCategory.Data);
        source.Output.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text });
        var target = new TestNodeViewModel("target", NodeCategory.Data);
        target.Input.Add(new NodeConnectorViewModel { Title = "Value", PortType = NodePortType.Text, InitialValue = "ignored" });
        object? observed = "not executed";
        target.Action = _ => { observed = target.ReadInput("Value"); return Task.CompletedTask; };

        await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([source, target],
            [new NodeConnection { SourceInstanceId = source.InstanceId, SourceConnector = "Value", TargetInstanceId = target.InstanceId, TargetConnector = "Value" }],
            TestContext.Current.CancellationToken);

        Assert.Null(observed);
    }
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
        var runTask = engine.ExecuteAsync([stuck], Array.Empty<NodeConnection>(), cts.Token);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        cts.Cancel();

        var result = await runTask;
        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

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

        var branch = FlowNode("Branch", "Flow.Condition.Branch", ["Condition"], ["True", "False"]);
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
    public async Task Engine_Branch_MergeExecutesAfterTakenPath()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var branch = FlowNode("Branch", "Flow.Condition.Branch", ["Condition"], ["True", "False"]);
        var onTrue = FlowNode("OnTrue", "Test.OnTrue", ["Flow"], ["Flow"]);
        var onFalse = FlowNode("OnFalse", "Test.OnFalse", ["Flow"], ["Flow"]);
        var merge = FlowNode("Merge", "Test.Merge", ["Flow"], []);
        var executed = new List<string>();

        branch.Input[0].InitialValue = true;
        branch.Action = _ => { branch.WriteOutput("True", true); return Task.CompletedTask; };
        onTrue.Action = _ => { executed.Add("true"); onTrue.WriteOutput("Flow", true); return Task.CompletedTask; };
        onFalse.Action = _ => { executed.Add("false"); onFalse.WriteOutput("Flow", true); return Task.CompletedTask; };
        merge.Action = _ => { executed.Add("merge"); return Task.CompletedTask; };

        var connections = new[]
        {
            Conn(branch, "True", onTrue, "Flow"),
            Conn(branch, "False", onFalse, "Flow"),
            Conn(onTrue, "Flow", merge, "Flow"),
            Conn(onFalse, "Flow", merge, "Flow")
        };

        var result = await engine.ExecuteAsync([branch, onTrue, onFalse, merge], connections, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["true", "merge"], executed);
        Assert.Equal(NodeExecutionStatus.Skipped, onFalse.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, merge.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_FailurePolicy_SkipsDependentsButRunsIndependentNodes()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var failing = FlowNode("Fail", "Test.Fail", [], ["Flow"]);
        var dependent = FlowNode("Dependent", "Test.Dependent", ["Flow"], []);
        var independent = FlowNode("Independent", "Test.Independent", [], []);
        var independentRuns = 0;
        failing.Action = _ => throw new InvalidOperationException("expected");
        dependent.Action = _ => throw new InvalidOperationException("must not execute");
        independent.Action = _ => { independentRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [failing, dependent, independent],
            [Conn(failing, "Flow", dependent, "Flow")],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { FailurePolicy = ExecutionFailurePolicy.SkipDependents });

        Assert.Equal(ExecutionStatus.PartiallySucceeded, result.Status);
        Assert.Equal(1, independentRuns);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_FailurePolicy_FailFastStopsRemainingNodes()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var failing = FlowNode("Fail", "Test.Fail", [], []);
        var later = FlowNode("Later", "Test.Later", [], []);
        var laterRuns = 0;
        failing.Action = _ => throw new InvalidOperationException("expected");
        later.Action = _ => { laterRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [failing, later],
            [],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { FailurePolicy = ExecutionFailurePolicy.FailFast });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, laterRuns);
    }

    [Fact]
    public async Task Engine_Loop_IteratesBodySubGraph()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Index"]);
        var body1 = FlowNode("Body1", "Test.Body1", ["Flow"], ["Flow"]);
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

        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Index"]);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_InteractionInsideScheduledScope_HonorsCancelAndRunsJoin(bool parallel)
    {
        var owner = FlowNode("Owner", parallel ? "Flow.Execution.Parallel" : "Flow.Collection.Loop",
            parallel ? ["Flow"] : ["Count"], parallel ? ["Branch A", "Branch B"] : ["Body", "Completed"]);
        if (!parallel)
        {
            owner.Input[0].InitialValue = 2;
        }
        var dialog = FlowNode("Dialog", "UI.Dialog.Show", ["Flow"], ["Submitted", "Cancelled"]);
        var submitted = FlowNode("Submitted", "Test.Submit", ["Flow"], ["Completed"]);
        var cancelled = FlowNode("Cancelled", "Test.Cancel", ["Flow"], ["Completed"]);
        var join = FlowNode("Join", "Test.Join", ["Flow"], []);
        var independent = FlowNode("Independent", "Test.Independent", ["Flow"], []);
        var submits = 0;
        var cancels = 0;
        var joins = 0;
        dialog.Action = _ =>
        {
            dialog.WriteOutput("Submitted", false);
            dialog.WriteOutput("Cancelled", true);
            return Task.CompletedTask;
        };
        submitted.Action = _ => { submits++; return Task.CompletedTask; };
        cancelled.Action = _ => { cancels++; return Task.CompletedTask; };
        join.Action = _ => { joins++; return Task.CompletedTask; };
        var edges = new List<NodeConnection>
        {
            Conn(owner, parallel ? "Branch A" : "Body", dialog, "Flow"),
            Conn(dialog, "Submitted", submitted, "Flow"),
            Conn(dialog, "Cancelled", cancelled, "Flow"),
            Conn(submitted, "Completed", join, "Flow"),
            Conn(cancelled, "Completed", join, "Flow"),
            Conn(owner, parallel ? "Branch B" : "Completed", independent, "Flow")
        };
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync(
            [owner, dialog, submitted, cancelled, join, independent], edges, TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = new SupportedInteractionHost() });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, submits);
        Assert.Equal(parallel ? 1 : 2, cancels);
        Assert.Equal(parallel ? 1 : 2, joins);
        Assert.Equal(NodeExecutionStatus.Succeeded, independent.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_LoopChangingDecision_ResetsSkippedBranchesPerIteration()
    {
        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body"]);
        loop.Input[0].InitialValue = 2;
        var dialog = FlowNode("Dialog", "UI.Dialog.Show", ["Flow"], ["Submitted", "Cancelled"]);
        var accepted = FlowNode("Accepted", "Test.Accepted", ["Flow"], []);
        var rejected = FlowNode("Rejected", "Test.Rejected", ["Flow"], []);
        var prompts = 0;
        var accepts = 0;
        var rejects = 0;
        dialog.Action = _ =>
        {
            var submit = prompts++ == 1;
            dialog.WriteOutput("Submitted", submit);
            dialog.WriteOutput("Cancelled", !submit);
            return Task.CompletedTask;
        };
        accepted.Action = _ => { accepts++; return Task.CompletedTask; };
        rejected.Action = _ => { rejects++; return Task.CompletedTask; };

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([loop, dialog, accepted, rejected],
            [Conn(loop, "Body", dialog, "Flow"), Conn(dialog, "Submitted", accepted, "Flow"),
                Conn(dialog, "Cancelled", rejected, "Flow")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = new SupportedInteractionHost() });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, accepts);
        Assert.Equal(1, rejects);
    }

    [Fact]
    public async Task Engine_Loop_CompletedPathRunsOnceAfterBody()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Completed", "Index"]);
        var body = FlowNode("Body", "Test.Body", ["Flow"], []);
        var completed = FlowNode("Completed", "Test.Completed", ["Flow"], []);
        var bodyRuns = 0;
        var completedRuns = 0;
        loop.Input[0].InitialValue = 3.0;
        body.Action = _ => { bodyRuns++; return Task.CompletedTask; };
        completed.Action = _ => { completedRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [loop, body, completed],
            [Conn(loop, "Body", body, "Flow"), Conn(loop, "Completed", completed, "Flow")],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, bodyRuns);
        Assert.Equal(1, completedRuns);
    }

    [Fact]
    public async Task Engine_Loop_ZeroIterations_SkipsBodyAndRunsCompletedPath()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Completed", "Index"]);
        var body = FlowNode("Body", "Test.Body", ["Flow"], []);
        var completed = FlowNode("Completed", "Test.Completed", ["Flow"], []);
        var bodyRuns = 0;
        var completedRuns = 0;
        loop.Input[0].InitialValue = 0.0;
        body.Action = _ => { bodyRuns++; return Task.CompletedTask; };
        completed.Action = _ => { completedRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync([loop, body, completed],
            [Conn(loop, "Body", body, "Flow"), Conn(loop, "Completed", completed, "Flow")],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Skipped, body.ExecutionStatus);
        Assert.Equal(0, bodyRuns);
        Assert.Equal(1, completedRuns);
    }

    [Fact]
    public async Task Engine_Loop_SkipDependents_SkipsFailedBodyDependentsButRunsCompletedPath()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Completed", "Index"]);
        var failing = FlowNode("Failing", "Test.Failing", ["Flow"], ["Flow"]);
        var dependent = FlowNode("Dependent", "Test.Dependent", ["Flow"], []);
        var completed = FlowNode("Completed", "Test.Completed", ["Flow"], []);
        var dependentRuns = 0;
        var completedRuns = 0;
        loop.Input[0].InitialValue = 2.0;
        failing.Action = _ => throw new InvalidOperationException("expected");
        dependent.Action = _ => { dependentRuns++; return Task.CompletedTask; };
        completed.Action = _ => { completedRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [loop, failing, dependent, completed],
            [
                Conn(loop, "Body", failing, "Flow"),
                Conn(failing, "Flow", dependent, "Flow"),
                Conn(loop, "Completed", completed, "Flow")
            ],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { FailurePolicy = ExecutionFailurePolicy.SkipDependents });

        Assert.Equal(ExecutionStatus.PartiallySucceeded, result.Status);
        Assert.Equal(0, dependentRuns);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
        Assert.Equal(1, completedRuns);
    }

    [Fact]
    public async Task Engine_Parallel_RunsBranchesConcurrently()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var parallel = FlowNode("Parallel", "Flow.Execution.Parallel", ["Flow"], ["Branch A", "Branch B"]);
        var a = FlowNode("A", "Test.A", ["Flow"], []);
        var b = FlowNode("B", "Test.B", ["Flow"], []);

        var insideParallel = 0;
        var peak = 0;
        var started = 0;
        var gate = new object();
        using var bothStarted = new ManualResetEventSlim();

        Task Track(INodeExecutionContext _)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.Set();
            }

            if (!bothStarted.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("Parallel branches did not run concurrently.");
            }

            lock (gate)
            {
                var current = ++insideParallel;
                peak = Math.Max(peak, current);
            }

            Thread.Sleep(20);
            lock (gate)
            {
                insideParallel--;
            }

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
    public async Task Engine_Parallel_MergeRunsOnceAfterBothBranches()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var parallel = FlowNode("Parallel", "Flow.Execution.Parallel", ["Flow"], ["Branch A", "Branch B"]);
        var a = FlowNode("A", "Test.A", ["Flow"], ["Flow"]);
        var b = FlowNode("B", "Test.B", ["Flow"], ["Flow"]);
        var merge = FlowNode("Merge", "Test.Merge", ["Flow"], []);
        var branchRuns = 0;
        var mergeRuns = 0;
        a.Action = _ => { Interlocked.Increment(ref branchRuns); return Task.CompletedTask; };
        b.Action = _ => { Interlocked.Increment(ref branchRuns); return Task.CompletedTask; };
        merge.Action = _ => { mergeRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [parallel, a, b, merge],
            [
                Conn(parallel, "Branch A", a, "Flow"),
                Conn(parallel, "Branch B", b, "Flow"),
                Conn(a, "Flow", merge, "Flow"),
                Conn(b, "Flow", merge, "Flow")
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, branchRuns);
        Assert.Equal(1, mergeRuns);
    }

    [Fact]
    public async Task Engine_Parallel_SkipDependents_SkipsFailedBranchDependentsAndRunsOtherBranch()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var parallel = FlowNode("Parallel", "Flow.Execution.Parallel", ["Flow"], ["Branch A", "Branch B"]);
        var failing = FlowNode("Failing", "Test.Failing", ["Flow"], ["Flow"]);
        var dependent = FlowNode("Dependent", "Test.Dependent", ["Flow"], []);
        var independent = FlowNode("Independent", "Test.Independent", ["Flow"], []);
        var dependentRuns = 0;
        var independentRuns = 0;
        failing.Action = _ => throw new InvalidOperationException("expected");
        dependent.Action = _ => { dependentRuns++; return Task.CompletedTask; };
        independent.Action = _ => { Interlocked.Increment(ref independentRuns); return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [parallel, failing, dependent, independent],
            [
                Conn(parallel, "Branch A", failing, "Flow"),
                Conn(failing, "Flow", dependent, "Flow"),
                Conn(parallel, "Branch B", independent, "Flow")
            ],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { FailurePolicy = ExecutionFailurePolicy.SkipDependents });

        Assert.Equal(ExecutionStatus.PartiallySucceeded, result.Status);
        Assert.Equal(0, dependentRuns);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
        Assert.Equal(1, independentRuns);
    }

    [Fact]
    public async Task Engine_Parallel_FailFast_DoesNotScheduleRemainingBranchNodes()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());
        var parallel = FlowNode("Parallel", "Flow.Execution.Parallel", ["Flow"], ["Branch A"]);
        var failing = FlowNode("Failing", "Test.Failing", ["Flow"], ["Flow"]);
        var later = FlowNode("Later", "Test.Later", ["Flow"], []);
        var laterRuns = 0;
        failing.Action = _ => throw new InvalidOperationException("expected");
        later.Action = _ => { laterRuns++; return Task.CompletedTask; };

        var result = await engine.ExecuteAsync(
            [parallel, failing, later],
            [Conn(parallel, "Branch A", failing, "Flow"), Conn(failing, "Flow", later, "Flow")],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { FailurePolicy = ExecutionFailurePolicy.FailFast });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, laterRuns);
        Assert.Equal(NodeExecutionStatus.Idle, later.ExecutionStatus);
    }

    [Fact]
    public async Task Engine_Loop_ClampsExcessiveIterations()
    {
        var engine = new NodeExecutionEngine(new MemoryLog());

        var loop = FlowNode("Loop", "Flow.Collection.Loop", ["Count"], ["Body", "Index"]);
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
    private sealed class SupportedInteractionHost : IWorkflowInteractionService
    {
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This scheduler test supplies interaction output through its test node.");
    }

    private static NodeConnection Conn(FlowNodeViewModel source, string sourceConnector, FlowNodeViewModel target, string targetConnector)
        => new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = sourceConnector,
            TargetInstanceId = target.InstanceId,
            TargetConnector = targetConnector
        };
}