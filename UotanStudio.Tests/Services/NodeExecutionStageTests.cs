using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Single-graph execution stages: ordering, per-stage patch publication, stage-boundary safety and persistence.
/// <para>单图执行阶段：顺序、按阶段发布补丁、阶段边界安全与持久化。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class NodeExecutionStageTests : IDisposable
{
    private const string ProbeTypeKey = "Test.Stage.Probe";
    private const string FailTypeKey = "Test.Stage.Fail";

    private static int _probeRuns;
    private static bool _publishedFileObserved;
    private static string? _observedPath;

    private readonly TempDirectory _temp = new();

    public NodeExecutionStageTests()
    {
        NodeFactoryRegistrar.RegisterAll();
        NodeTypeRegistry.Register(ProbeTypeKey, () => new ProbeNode());
        NodeTypeRegistry.Register(FailTypeKey, () => new FailingNode());
    }

    public void Dispose()
    {
        NodeTypeRegistry.Unregister(ProbeTypeKey);
        NodeTypeRegistry.Unregister(FailTypeKey);
        _temp.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_MultiStage_PublishesPatchArtifactsBeforeTheNextStage()
    {
        var token = TestContext.Current.CancellationToken;
        var target = Path.Combine(_temp.Path, "published.prop");
        var (edit, write) = PublishingPair(target);
        var probe = new ProbeNode { ExecutionStage = 1 };
        _probeRuns = 0;
        _publishedFileObserved = false;
        _observedPath = target;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write, probe],
            [Connect(edit, "Text", write, "Text")], token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.ExecutedCount);
        Assert.Equal(1, _probeRuns);
        Assert.True(_publishedFileObserved);
        Assert.True(File.Exists(target));
        Assert.Equal(NodeExecutionStatus.Succeeded, probe.ExecutionStatus);
        Assert.Equal(PatchRunOutcome.Applied, result.PatchOutcome);
        Assert.Contains(result.PatchChanges, change => string.Equals(change.Path, target, StringComparison.Ordinal)
            && change.Kind == PatchChangeKind.Added);
    }

    [Fact]
    public async Task ExecuteAsync_StagesRunInAscendingOrderRegardlessOfNodeOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var later = new ProbeNode { ExecutionStage = 3 };
        var earlier = new ProbeNode { ExecutionStage = 1 };
        _probeRuns = 0;
        _publishedFileObserved = false;
        _observedPath = null;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([later, earlier], [], token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, _probeRuns);
        Assert.Equal(NodeExecutionStatus.Succeeded, earlier.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, later.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_StageFailure_StopsLaterStagesAndKeepsEarlierArtifacts()
    {
        var token = TestContext.Current.CancellationToken;
        var target = Path.Combine(_temp.Path, "kept.prop");
        var (edit, write) = PublishingPair(target);
        var failing = new FailingNode { ExecutionStage = 1 };
        var probe = new ProbeNode { ExecutionStage = 2 };
        _probeRuns = 0;
        _publishedFileObserved = false;
        _observedPath = target;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write, failing, probe],
            [Connect(edit, "Text", write, "Text")], token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, failing.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, probe.ExecutionStatus);
        Assert.Equal(0, _probeRuns);
        Assert.True(File.Exists(target));
        Assert.Equal(PatchRunOutcome.Applied, result.PatchOutcome);
    }

    [Fact]
    public async Task ExecuteAsync_ContinueAfterStageFailure_RunsTheRemainingStages()
    {
        var token = TestContext.Current.CancellationToken;
        var failing = new FailingNode { ExecutionStage = 0 };
        var probe = new ProbeNode { ExecutionStage = 1 };
        _probeRuns = 0;
        _publishedFileObserved = false;
        _observedPath = null;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([failing, probe], [], token,
            new NodeExecutionOptions { ContinueAfterStageFailure = true });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(1, _probeRuns);
        Assert.Equal(NodeExecutionStatus.Succeeded, probe.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionAcrossStages_RejectsBeforeExecutingNodes()
    {
        var token = TestContext.Current.CancellationToken;
        var first = new ProbeNode { ExecutionStage = 0 };
        var second = new ProbeNode { ExecutionStage = 1 };
        _probeRuns = 0;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([first, second],
            [Connect(first, "Out", second, "In")], token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("execution stages", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(NodeExecutionStatus.Idle, first.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, second.ExecutionStatus);
        Assert.Equal(0, _probeRuns);
    }

    [Fact]
    public async Task ExecuteAsync_SameStagePatchAndDevice_FailsPreflightWithoutRunningNodes()
    {
        var token = TestContext.Current.CancellationToken;
        var (edit, write) = PublishingPair(Path.Combine(_temp.Path, "rejected.prop"));
        var device = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write, device],
            [Connect(edit, "Text", write, "Text")], token,
            new NodeExecutionOptions { Interactions = new AcceptingInteractionHost() });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Idle, edit.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, write.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, device.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteAsync_PatchStageThenDeviceStage_RunsBothStagesInOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var target = Path.Combine(_temp.Path, "staged-before-device.prop");
        var (edit, write) = PublishingPair(target);
        var device = DeviceNode();
        device.ExecutionStage = 1;

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write, device],
            [Connect(edit, "Text", write, "Text")], token,
            new NodeExecutionOptions { Interactions = new AcceptingInteractionHost() });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Succeeded, edit.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, write.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Failed, device.ExecutionStatus);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void InspectStages_PatchAndDeviceInOneStage_ReportsUnsafeStage()
    {
        var patch = PatchNodeViewModel.Create("Image.Text.Edit");
        var device = DeviceNode();

        var stage = Assert.Single(PatchWorkflowPolicy.InspectStages([patch, device]));

        Assert.True(stage.HasPatches);
        Assert.False(stage.Safe);
    }

    [Fact]
    public void InspectStages_PatchThenDeviceStage_KeepsPatchStageSafeAndDeviceStageUnsafe()
    {
        var patch = PatchNodeViewModel.Create("Image.Text.Edit");
        var device = DeviceNode();
        device.ExecutionStage = 1;

        var stages = PatchWorkflowPolicy.InspectStages([patch, device]);

        Assert.Equal(2, stages.Count);
        Assert.Equal(0, stages[0].Stage);
        Assert.True(stages[0].HasPatches);
        Assert.True(stages[0].Safe);
        Assert.Equal(1, stages[1].Stage);
        Assert.False(stages[1].HasPatches);
        Assert.False(stages[1].Safe);
    }

    [Fact]
    public async Task WorkflowFile_SaveAsync_ThenLoadAsync_RoundTripsExecutionStage()
    {
        var token = TestContext.Current.CancellationToken;
        var node = new ProbeNode { ExecutionStage = 2 };
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) };
        Assert.Equal(2, Assert.Single(snapshot.Nodes).ExecutionStage);

        var path = Path.Combine(_temp.Path, "stage.uotannode");
        Assert.True(await WorkflowFile.SaveAsync(path, snapshot, token));
        var restored = Assert.IsType<WorkflowSnapshot>(await WorkflowFile.LoadAsync(path, token));

        Assert.Equal(2, Assert.Single(restored.Nodes).ExecutionStage);
        var runtime = WorkflowRuntimeBuilder.Build(restored, preserveUnknown: false);
        Assert.Empty(runtime.Errors);
        Assert.Equal(2, Assert.Single(runtime.Nodes).ExecutionStage);
    }

    private static (FlowNodeViewModel Edit, FlowNodeViewModel Write) PublishingPair(string target)
    {
        var edit = PatchNodeViewModel.Create("Config.Edit");
        Set(edit, "Format", "FileContexts");
        Set(edit, "Operation", "Add");
        Set(edit, "Text", "# keep\n");
        Set(edit, "Path Expression", "/path");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", target);
        return (edit, write);
    }

    private static FlowNodeViewModel DeviceNode()
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();

    private static void Set(FlowNodeViewModel node, string port, object value)
        => node.Input.Single(input => string.Equals(input.Title, port, StringComparison.Ordinal)).InitialValue = value;

    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input)
        => new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = output,
            TargetInstanceId = target.InstanceId,
            TargetConnector = input
        };

    /// <summary>Node recording its execution and the observed artifact state through the enclosing fixture.</summary>
    private sealed class ProbeNode : FlowNodeViewModel
    {
        public ProbeNode() : base("Probe", NodeCategory.Data)
        {
            TypeKey = ProbeTypeKey;
            Input.Add(new NodeConnectorViewModel { Title = "In", PortId = "in", PortType = NodePortType.Any, MaxConnections = 1 });
            Output.Add(new NodeConnectorViewModel { Title = "Out", PortId = "out", PortType = NodePortType.Any });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            _probeRuns++;
            _publishedFileObserved = _observedPath is not null && File.Exists(_observedPath);
            return Task.CompletedTask;
        }
    }

    /// <summary>Node failing every execution so a stage reports a failure.</summary>
    private sealed class FailingNode : FlowNodeViewModel
    {
        public FailingNode() : base("Failing", NodeCategory.Data) => TypeKey = FailTypeKey;

        public override Task ExecuteAsync(INodeExecutionContext context)
            => throw new InvalidOperationException("expected stage failure");
    }

    /// <summary>Interaction host accepting the destructive-operation confirmation.</summary>
    private sealed class AcceptingInteractionHost : IWorkflowInteractionService
    {
        public bool IsSupported => true;

        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InteractionResult { Outcome = InteractionOutcome.Submitted });
    }

    /// <summary>In-memory log used to construct the execution engine.</summary>
    private sealed class MemoryLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}