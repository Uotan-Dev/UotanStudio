using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchGateTests
{
    [Theory]
    [InlineData("preflight")]
    [InlineData("fail-fast")]
    [InlineData("zero-loop")]
    [InlineData("cancelled")]
    public async Task Execute_UnreachedComposite_ReportsWithoutInvokingPackage(string scenario)
    {
        var invoked = false;
        var composite = new CompositeNodeViewModel(new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.unreached", Name = "Unreached" }
        }, () =>
        {
            invoked = true;
            return [];
        });
        var controller = scenario == "zero-loop"
            ? (FlowNodeViewModel)FlowControlNodeViewModel.CreateLoopNode()
            : PatchNodeViewModel.Create("Patch.Assert");
        if (scenario == "zero-loop")
        {
            controller.Input.Single(port => port.Title == "Count").InitialValue = 0;
        }
        var options = new NodeExecutionOptions
        {
            FailurePolicy = ExecutionFailurePolicy.FailFast,
            ValidatePackage = scenario == "preflight" ? () => ["Unavailable dependency"] : null
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (scenario == "cancelled")
        {
            await cancellation.CancelAsync();
        }
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([controller, composite],
            [Connect(controller, scenario == "zero-loop" ? "Body" : "Completed", composite, "Flow")],
            cancellation.Token, options);

        var row = Assert.Single(result.CompositeExecutions);
        Assert.False(invoked);
        Assert.Equal(NodeExecutionStatus.Skipped, row.Status);
        Assert.Equal(scenario == "preflight" ? UotanStudio.Services.Patching.CompositeSkipReason.PreflightRejected
            : UotanStudio.Services.Patching.CompositeSkipReason.NotReached, row.SkipReason);
        Assert.Equal(UotanStudio.Services.Patching.CompositeApplicability.None, row.Applicability);
    }

    [Fact]
    public async Task Execute_UpstreamFailure_ReportsCompositeSkipWithoutInvokingPackage()
    {
        var failure = PatchNodeViewModel.Create("Patch.Assert");
        var invoked = false;
        var composite = new CompositeNodeViewModel(new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.skipped", Name = "Skipped" }
        }, () =>
        {
            invoked = true;
            return [];
        });
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([failure, composite],
            [Connect(failure, "Completed", composite, "Flow")], TestContext.Current.CancellationToken);

        var row = Assert.Single(result.CompositeExecutions);
        Assert.False(invoked);
        Assert.Equal(NodeExecutionStatus.Skipped, row.Status);
        Assert.Equal(UotanStudio.Services.Patching.CompositeSkipReason.UpstreamFailure, row.SkipReason);
        Assert.Equal(UotanStudio.Services.Patching.CompositeApplicability.None, row.Applicability);
        Assert.False(result.CompositeReportTruncated);
    }

    [Theory]
    [InlineData(true, true, false, UotanStudio.Services.Patching.CompositeApplicability.Applicable)]
    [InlineData(true, false, true, UotanStudio.Services.Patching.CompositeApplicability.AlreadyApplied)]
    [InlineData(false, false, false, UotanStudio.Services.Patching.CompositeApplicability.NotApplicable)]
    [InlineData(true, false, false, UotanStudio.Services.Patching.CompositeApplicability.Conflict)]
    public async Task Execute_NestedComposite_RecordsEnclosingInvocation(bool compatible, bool original, bool desired,
        UotanStudio.Services.Patching.CompositeApplicability expected)
    {
        NodeFactoryRegistrar.RegisterAll();
        var start = Gate(compatible, original, desired);
        var inner = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.parent-evidence-inner", Name = "Inner", RequiredNodeTypes = ["Patch.ApplyGate"] },
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([start]) }
        };
        NodeFactoryRegistrar.RegisterVisualPlugin(inner);
        try
        {
            var innerNode = new CompositeNodeViewModel(inner);
            var outer = new VisualPluginPackage
            {
                Kind = VisualPluginKind.CompositeNode,
                Manifest = new VisualPluginManifest { Id = "tests.parent-evidence-outer", Name = "Outer", RequiredNodeTypes = [inner.NodeTypeKey] },
                Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([innerNode]) }
            };
            var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([new CompositeNodeViewModel(outer)], [],
                TestContext.Current.CancellationToken);

            var conflict = expected == UotanStudio.Services.Patching.CompositeApplicability.Conflict;
            Assert.Equal(conflict ? ExecutionStatus.PartiallySucceeded : ExecutionStatus.Succeeded, result.Status);
            if (conflict)
            {
                Assert.Equal(UotanStudio.Services.Patching.PatchRunOutcome.Conflict, result.PatchOutcome);
            }
            Assert.Equal(2, result.CompositeExecutions.Count);
            var parent = result.CompositeExecutions[0];
            var child = result.CompositeExecutions[1];
            Assert.Equal(outer.Manifest.Id, parent.PackageId);
            Assert.Null(parent.ParentInvocation);
            Assert.Equal(inner.Manifest.Id, child.PackageId);
            Assert.Equal(parent.Invocation, child.ParentInvocation);
            Assert.All(result.CompositeExecutions, row =>
            {
                Assert.Equal(conflict ? NodeExecutionStatus.Failed : NodeExecutionStatus.Succeeded, row.Status);
                Assert.Equal(expected, row.Applicability);
            });
        }
        finally
        {
            NodeFactoryRegistrar.UnregisterVisualPlugin(inner.NodeTypeKey);
        }
    }

    [Fact]
    public async Task Execute_ReusedGraphAfterConditionEdit_DoesNotReusePreviousApproval()
    {
        using var temp = new TempDirectory();
        var gate = Gate(true, true, false);
        var firstTarget = Path.Combine(temp.Path, "first");
        var secondTarget = Path.Combine(temp.Path, "second");
        var write = Writer(firstTarget);
        var engine = new NodeExecutionEngine(new Log());
        var connections = new[] { Connect(gate, "Apply", write, "Flow") };
        var token = TestContext.Current.CancellationToken;
        var first = await engine.ExecuteAsync([gate, write], connections, token);
        Assert.Equal(ExecutionStatus.Succeeded, first.Status);
        Assert.True(File.Exists(firstTarget));
        gate.Input.Single(port => port.Title == "Compatible").InitialValue = false;
        write.Input.Single(port => port.Title == "Output Path").InitialValue = secondTarget;

        var second = await engine.ExecuteAsync([gate, write], connections, token);

        Assert.Equal(ExecutionStatus.Succeeded, second.Status);
        Assert.False(File.Exists(secondTarget));
        Assert.Equal(NodeExecutionStatus.Skipped, write.ExecutionStatus);
        Assert.All(write.Output, output => Assert.Null(output.Data));
        Assert.Equal("NotApplicable", gate.Output.Single(output => output.Title == "Applicability").Data);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    public async Task Execute_GatedWrite_OnlyAppliesOriginalCompatibleState(bool compatible, bool original, bool desired, bool applied)
    {
        using var temp = new TempDirectory();
        var gate = Gate(compatible, original, desired);
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, write],
            [Connect(gate, "Apply", write, "Flow")], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(applied, File.Exists(target));
        Assert.Equal(applied ? NodeExecutionStatus.Succeeded : NodeExecutionStatus.Skipped, write.ExecutionStatus);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("skip")]
    [InlineData("join")]
    [InlineData("bypass")]
    [InlineData("data")]
    public async Task Execute_UnguardedWrite_RejectsBeforeAnyNodeExecutes(string wiring)
    {
        using var temp = new TempDirectory();
        var gate = Gate(true, true, false);
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);
        var start = FlowControlNodeViewModel.CreateStartNode();
        var connections = new List<NodeConnection>();
        if (wiring is "join" or "bypass" or "data")
        {
            connections.Add(Connect(gate, "Apply", write, wiring == "data" ? "Text" : "Flow"));
        }
        if (wiring is "skip" or "join")
        {
            connections.Add(Connect(gate, "Skip", write, "Flow"));
        }
        if (wiring == "bypass")
        {
            connections.Add(Connect(start, start.Output.Single().Title, write, "Flow"));
        }

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, start, write], connections,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
        Assert.Equal(NodeExecutionStatus.Idle, gate.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, write.ExecutionStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Theory]
    [InlineData("Patch.ImageWriteText")]
    [InlineData("Patch.ImageApplyTexts")]
    [InlineData("Patch.ImageSetAttributes")]
    [InlineData("Patch.ImageSetXattr")]
    [InlineData("Patch.ImageDeleteXattr")]
    public async Task Execute_UnguardedImageWrite_RejectsBeforeAnyNodeExecutes(string type)
    {
        var gate = Gate(true, true, false);
        var write = PatchNodeViewModel.Create(type);
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, write], [],
            TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Idle, gate.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, write.ExecutionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_GateInsideController_RejectsUnsupportedControlNesting(bool parallel)
    {
        using var temp = new TempDirectory();
        var loop = parallel ? FlowControlNodeViewModel.CreateParallelNode() : FlowControlNodeViewModel.CreateLoopNode();
        var gate = Gate(false, false, false);
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([loop, gate, write],
            [Connect(loop, parallel ? "Branch A" : "Body", gate, "Flow"), Connect(gate, "Apply", write, "Flow")], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
        Assert.Equal(NodeExecutionStatus.Idle, gate.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Idle, write.ExecutionStatus);
    }

    [Fact]
    public async Task Execute_GateBeforeLoop_SkipsLoopBodySafely()
    {
        using var temp = new TempDirectory();
        var loop = FlowControlNodeViewModel.CreateLoopNode();
        var gate = Gate(false, false, false);
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, loop, write],
            [Connect(gate, "Apply", loop, loop.Input.First(port => port.PortType == NodePortType.Flow).Title),
                Connect(loop, "Body", write, "Flow")], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.False(File.Exists(target));
        Assert.Equal(NodeExecutionStatus.Skipped, write.ExecutionStatus);
    }

    [Fact]
    public async Task Execute_UnguardedCompositeMutation_IsDetectedRecursively()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.gate-inner", Name = "Inner", RequiredNodeTypes = ["Patch.WriteText"] },
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([write]) }
        };
        var composite = new CompositeNodeViewModel(package);
        var gate = Gate(true, true, false);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, composite], [], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_CompositeWithInvocationFlow_RespectsOuterGate(bool apply)
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var target = Path.Combine(temp.Path, "target");
        var write = Writer(target);
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.gate-public-flow", Name = "Inner", RequiredNodeTypes = ["Patch.WriteText"] },
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([write]) }
        };
        var composite = new CompositeNodeViewModel(package);
        var gate = Gate(apply, true, false);

        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, composite],
            [Connect(gate, "Apply", composite, "Flow")], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(apply, File.Exists(target));
        if (apply)
        {
            var invocation = Assert.Single(result.CompositeExecutions);
            Assert.Equal("tests.gate-public-flow", invocation.PackageId);
            Assert.Equal(NodeExecutionStatus.Succeeded, invocation.Status);
            Assert.Equal(composite.InstanceId, invocation.InstanceId);
        }
        else
        {
            var skipped = Assert.Single(result.CompositeExecutions);
            Assert.Equal(NodeExecutionStatus.Skipped, skipped.Status);
            Assert.Equal(UotanStudio.Services.Patching.CompositeSkipReason.Condition, skipped.SkipReason);
            Assert.Equal(UotanStudio.Services.Patching.CompositeApplicability.None, skipped.Applicability);
        }
    }

    private static PatchNodeViewModel Gate(bool compatible, bool original, bool desired)
    {
        var node = PatchNodeViewModel.Create("Patch.ApplyGate");
        node.Input.Single(port => port.Title == "Compatible").InitialValue = compatible;
        node.Input.Single(port => port.Title == "Original State Matches").InitialValue = original;
        node.Input.Single(port => port.Title == "Desired State Matches").InitialValue = desired;
        return node;
    }

    private static PatchNodeViewModel Writer(string path)
    {
        var node = PatchNodeViewModel.Create("Patch.WriteText");
        node.Input.Single(port => port.Title == "Output Path").InitialValue = path;
        node.Input.Single(port => port.Title == "Text").InitialValue = "changed";
        return node;
    }

    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = output,
        TargetInstanceId = target.InstanceId,
        TargetConnector = input
    };

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}
