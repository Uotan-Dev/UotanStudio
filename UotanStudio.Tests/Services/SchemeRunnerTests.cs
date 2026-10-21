using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Scheme runner: stage ordering, declared input and output passing, failure stop, resume and reporting.
/// <para>Scheme 运行器：阶段顺序、声明输入输出传递、失败停止、续跑与报告。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class SchemeRunnerTests : IDisposable
{
    private const string EchoTypeKey = "Test.Scheme.Echo";
    private const string FailTypeKey = "Test.Scheme.Fail";

    private static int _echoRuns;

    private readonly TempDirectory _temp = new();

    public SchemeRunnerTests()
    {
        NodeFactoryRegistrar.RegisterAll();
        NodeTypeRegistry.Register(EchoTypeKey, () => new EchoNode());
        NodeTypeRegistry.Register(FailTypeKey, () => new FailingNode());
    }

    public void Dispose()
    {
        NodeTypeRegistry.Unregister(EchoTypeKey);
        NodeTypeRegistry.Unregister(FailTypeKey);
        _temp.Dispose();
    }

    [Fact]
    public async Task RunAsync_TwoStages_PassesDeclaredOutputIntoTheNextStage()
    {
        var token = TestContext.Current.CancellationToken;
        var first = new EchoNode();
        var second = new EchoNode();
        var prepare = Stage("prepare", first, "text");
        prepare.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "greeting",
            NodeInstanceId = first.InstanceId,
            PortId = "in"
        });
        var forward = Stage("forward", second, "result");
        forward.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "text",
            SourceStageId = "prepare",
            SourceOutput = "text",
            NodeInstanceId = second.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "pass-through",
            Name = "Pass through",
            Inputs = [new SchemeInputDefinition { Id = "greeting", Name = "Greeting", Default = "hello" }],
            Stages = [prepare, forward]
        };
        _echoRuns = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Stages.Count);
        Assert.Equal(2, result.ExecutedStageCount);
        Assert.Equal(2, _echoRuns);
        Assert.Equal("hello", result.Stages[0].Outputs["text"]);
        Assert.Equal("hello", result.Stages[1].Outputs["result"]);
        Assert.Null(result.FailedStageId);
    }

    [Fact]
    public async Task RunAsync_RequiredInputMissing_FailsBeforeAnyStageRuns()
    {
        var token = TestContext.Current.CancellationToken;
        var stage = Stage("prepare", new EchoNode(), "text");
        var package = new SchemePackage
        {
            Id = "requires-input",
            Name = "Requires input",
            Inputs = [new SchemeInputDefinition { Id = "image", Name = "Image", Type = SchemeInputType.Path, Required = true }],
            Stages = [stage]
        };
        _echoRuns = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Stages);
        Assert.Contains("image", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, _echoRuns);
    }

    [Fact]
    public async Task RunAsync_StageFailure_StopsTheRunAndLeavesLaterStagesNotRun()
    {
        var token = TestContext.Current.CancellationToken;
        var failing = new FailingNode();
        var laterEcho = new EchoNode();
        var package = new SchemePackage
        {
            Id = "stop-on-failure",
            Name = "Stop on failure",
            Stages =
            [
                Stage("prepare", new EchoNode(), "text"),
                new SchemeStage
                {
                    Id = "fail",
                    Kind = SchemeStageKind.Patch,
                    Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([failing]) }
                },
                Stage("after", laterEcho, "result")
            ]
        };
        _echoRuns = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("fail", result.FailedStageId);
        Assert.Equal(3, result.Stages.Count);
        Assert.Equal(SchemeStageStatus.Executed, result.Stages[0].Status);
        Assert.Equal(SchemeStageStatus.Failed, result.Stages[1].Status);
        Assert.Equal(SchemeStageStatus.NotRun, result.Stages[2].Status);
        Assert.Equal(1, _echoRuns);
        Assert.False(string.IsNullOrEmpty(result.Stages[1].ErrorMessage));
    }

    [Fact]
    public async Task RunAsync_ContinueFailurePolicy_RunsLaterStagesAndReportsPartialSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        var failing = new FailingNode();
        var package = new SchemePackage
        {
            Id = "continue-on-failure",
            Name = "Continue on failure",
            Stages =
            [
                Stage("prepare", new EchoNode(), "text"),
                new SchemeStage
                {
                    Id = "fail",
                    Kind = SchemeStageKind.Device,
                    FailurePolicy = SchemeStageFailurePolicy.Continue,
                    Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([failing]) }
                },
                Stage("after", new EchoNode(), "result")
            ]
        };
        _echoRuns = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.PartiallySucceeded, result.Status);
        Assert.Equal("fail", result.FailedStageId);
        Assert.Equal(SchemeStageStatus.Executed, result.Stages[2].Status);
        Assert.Equal(2, _echoRuns);
        Assert.Contains("fail", result.DeviceStageIds);
    }

    [Fact]
    public async Task RunAsync_ResumeFromStage_ReusesCompletedStageAndKeepsItsArtifacts()
    {
        var token = TestContext.Current.CancellationToken;
        var artifact = Path.Combine(_temp.Path, "published.prop");
        var second = new EchoNode();
        var forward = Stage("forward", second, "result");
        forward.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "text",
            SourceStageId = "prepare",
            SourceOutput = "text",
            NodeInstanceId = second.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "resume",
            Name = "Resume",
            Stages = [Stage("prepare", new EchoNode(), "text"), forward]
        };
        var completed = new SchemeStageResult
        {
            StageId = "prepare",
            Kind = SchemeStageKind.Patch,
            Status = SchemeStageStatus.Executed,
            ArtifactPaths = [artifact],
            Outputs = new Dictionary<string, object?> { ["text"] = "from-earlier-run" }
        };
        _echoRuns = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            ResumeFromStageId = "forward",
            CompletedStages = [completed]
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(SchemeStageStatus.Reused, result.Stages[0].Status);
        Assert.Equal(SchemeStageStatus.Executed, result.Stages[1].Status);
        Assert.Equal("from-earlier-run", result.Stages[1].Outputs["result"]);
        Assert.Contains(artifact, result.ArtifactPaths);
        Assert.Equal(1, result.ExecutedStageCount);
        Assert.Equal(1, _echoRuns);
    }

    [Fact]
    public async Task RunAsync_ProjectWorkflowReference_ResolvesThroughTheDelegate()
    {
        var token = TestContext.Current.CancellationToken;
        var external = new EchoNode();
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([external]) };
        var package = new SchemePackage
        {
            Id = "referenced",
            Name = "Referenced",
            Stages =
            [
                new SchemeStage
                {
                    Id = "external",
                    Kind = SchemeStageKind.Verify,
                    ProjectWorkflowName = "shared-workflow",
                    Outputs = [new SchemeStageOutputBinding { OutputId = "result", NodeInstanceId = external.InstanceId, PortId = "out" }]
                }
            ]
        };
        var runner = new SchemeRunner(new MemoryLog());

        var resolved = await runner.RunAsync(package, new SchemeRunOptions
        {
            ProjectWorkflowResolver = name => string.Equals(name, "shared-workflow", StringComparison.Ordinal) ? snapshot : null
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, resolved.Status);
        Assert.Equal(SchemeStageStatus.Executed, Assert.Single(resolved.Stages).Status);

        var missing = await runner.RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.Failed, missing.Status);
        Assert.Contains("shared-workflow", missing.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DeviceStage_IsReportedWithoutRollback()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "device-report",
            Name = "Device report",
            Stages =
            [
                new SchemeStage
                {
                    Id = "flash",
                    Kind = SchemeStageKind.Device,
                    Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([new EchoNode()]) }
                }
            ]
        };

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, cancellationToken: token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["flash"], result.DeviceStageIds);
        Assert.Empty(result.ArtifactPaths);
    }

    [Fact]
    public async Task RunAsync_SchemeWithoutStages_FailsTheContractValidation()
    {
        var package = new SchemePackage { Id = "empty", Name = "Empty" };

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(
            package, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("stages", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(result.Stages);
    }

    private static SchemeStage Stage(string id, FlowNodeViewModel node, string outputId)
    {
        var stage = new SchemeStage
        {
            Id = id,
            Kind = SchemeStageKind.Generic,
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) }
        };
        stage.Outputs.Add(new SchemeStageOutputBinding
        {
            OutputId = outputId,
            NodeInstanceId = node.InstanceId,
            PortId = "out"
        });
        return stage;
    }

    /// <summary>Node echoing its input value to the output port and counting executions.</summary>
    private sealed class EchoNode : FlowNodeViewModel
    {
        public EchoNode() : base("Echo", NodeCategory.Data)
        {
            TypeKey = EchoTypeKey;
            Input.Add(new NodeConnectorViewModel { Title = "In", PortId = "in", PortType = NodePortType.Any, MaxConnections = 1 });
            Output.Add(new NodeConnectorViewModel { Title = "Out", PortId = "out", PortType = NodePortType.Any });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            _echoRuns++;
            WriteOutput("Out", ReadInput("In"));
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

    /// <summary>In-memory log used to construct the scheme runner.</summary>
    private sealed class MemoryLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}