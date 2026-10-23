using UotanStudio.Projects;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class CompositeBoundaryTests
{
    [Fact]
    public async Task CreateEncapsulation_CompleteTextInputForm_DoesNotRequireOptionalConditions()
    {
        NodeFactoryRegistrar.RegisterAll();
        var field = InteractionTestNodes.Create("UI.Field.Text");
        field.Input.Single(port => port.Title == "Field ID").InitialValue = "value";
        var form = InteractionNodeViewModel.Create("UI.Form.Create");
        var dialog = InteractionNodeViewModel.Create("UI.Dialog");
        var plan = CompositeGraphService.CreateEncapsulation("Input", "tests.input-text", [field, form, dialog],
            [new(field.Output.Single(), form.Input.Single(port => port.Title == "Content")),
                new(form.Output.Single(), dialog.Input.Single(port => port.Title == "Form"))], [field, form, dialog]);
        Assert.DoesNotContain(plan.Composite!.Input, port => port.IsRequired);
        Assert.DoesNotContain(plan.Composite.Input, port => port.PortType == NodePortType.Record);

        var presenter = new SubmittedTextPresenter();
        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync(plan.Nodes, [], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = new QueuedWorkflowInteractionService(presenter) });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, presenter.Calls);
    }

    [Theory]
    [InlineData("UI.Form.Create", "Content")]
    [InlineData("UI.Result.Read", "Values")]
    public void Analyze_MissingRequiredInteractionData_AutomaticallyExposesInput(string typeKey, string title)
    {
        var node = InteractionTestNodes.Create(typeKey);
        var analysis = CompositeBoundaryAnalyzer.Analyze([node], []);

        var required = Assert.Single(analysis.Inputs, port => port.Required);
        Assert.Equal(node.Input.Single(port => port.Title == title).StableId, required.PortId);
    }

    [Fact]
    public void Analyze_ModeConditionalInteractionPorts_AreNotStaticallyRequired()
    {
        foreach (var typeKey in new[] { "UI.Dialog", "UI.Progress" })
        {
            var node = InteractionNodeViewModel.Create(typeKey);
            Assert.DoesNotContain(CompositeBoundaryAnalyzer.Analyze([node], []).Inputs, port => port.Required);
        }
    }

    [Theory]
    [InlineData("UI.Dialog", "Show")]
    [InlineData("UI.Progress", "Update")]
    public async Task ExecuteAsync_ModeNeedsDataButNoneArrives_FailsBeforeAnyInteraction(string typeKey, string mode)
    {
        var node = InteractionNodeViewModel.Create(typeKey);
        node.Input.Single(port => port.Title == "Mode").InitialValue = mode;
        foreach (var port in node.Input)
        {
            port.Data = port.InitialValue;
        }
        var presenter = new SubmittedTextPresenter();

        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = new QueuedWorkflowInteractionService(presenter) });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, presenter.Calls);
    }

    [Fact]
    public void Analyze_SensitiveLiteral_DoesNotExposeDefaultValue()
    {
        var node = Node("password");
        node.Input[0].InitialValue = "secret";
        node.Input[0].IsSensitive = true;
        node.Input[0].ExposeOnComposite = true;

        var port = Assert.Single(CompositeBoundaryAnalyzer.Analyze([node], []).Inputs);

        Assert.Null(port.DefaultValue);
        Assert.True(port.IsSensitive);
    }
    [Fact]
    public async Task ExecuteAsync_InactiveCompositeEntry_SkipsExclusiveBranchAndRetainsActiveJoin()
    {
        var inactive = Node("inactive");
        var active = Node("active");
        var exclusive = Node("exclusive");
        var join = Node("join");
        join.Input.Add(new NodeConnectorViewModel { PortId = "active", Title = "Active", PortType = NodePortType.Text });
        NodeConnection Edge(FlowNodeViewModel source, FlowNodeViewModel target, string input = "Value") => new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = "Value",
            TargetInstanceId = target.InstanceId,
            TargetConnector = input
        };
        var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([inactive, active, exclusive, join],
            [Edge(inactive, exclusive), Edge(inactive, join), Edge(active, join, "Active")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { EntryActivation = new Dictionary<string, bool> { [inactive.InstanceId] = false, [active.InstanceId] = true } });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Skipped, inactive.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, exclusive.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, active.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Succeeded, join.ExecutionStatus);
    }

    [Fact]
    public void Validate_RecursiveEmbeddedPackage_RejectsBeforeSerialization()
    {
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.recursive", Name = "Recursive" }
        };
        package.Graph.Nodes.Add(new NodeDto { InstanceId = "child", TypeKey = package.NodeTypeKey, CompositePackage = package });
        package.Manifest.RequiredNodeTypes.Add(package.NodeTypeKey);

        Assert.NotEmpty(VisualPluginValidation.Validate(package));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Validate_OldPackageVersion_RejectsWithoutAliases(int version)
    {
        var package = new VisualPluginPackage
        {
            FormatVersion = version,
            Manifest = new VisualPluginManifest { Id = "tests.version", Name = "Version" }
        };
        Assert.Contains(VisualPluginValidation.Validate(package), error => error.Contains("Unsupported visual-plugin format", StringComparison.Ordinal));
    }
    [Fact]
    public void Analyze_SameExternalSource_FansOutOneInputWithoutMergingUnrelatedInputs()
    {
        var source = Node("source");
        var other = Node("other");
        var first = Node("first");
        var second = Node("second");
        var third = Node("third");
        var result = CompositeBoundaryAnalyzer.Analyze([first, second, third],
        [new(source.Output[0], first.Input[0]), new(source.Output[0], second.Input[0]), new(other.Output[0], third.Input[0])]);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal(2, result.Inputs[0].GetBindings().Count());
        Assert.Single(result.Inputs[1].GetBindings());
        Assert.Empty(result.Outputs);
        Assert.Equal(3, result.OutputCandidates.Count);
    }

    [Fact]
    public void Analyze_RequiredAndLiteralInputs_OnlyMissingRequiredInputIsAutomatic()
    {
        var required = Node("required", true);
        var literal = Node("literal", true);
        literal.Input[0].InitialValue = "retained";
        var result = CompositeBoundaryAnalyzer.Analyze([required, literal], []);

        Assert.True(Assert.Single(result.Inputs).Required);
        Assert.Equal("retained", Assert.Single(result.InputCandidates).DefaultValue);
        required.Title = "renamed";
        Assert.Equal(result.Inputs[0].Id, CompositeBoundaryAnalyzer.Analyze([literal, required], []).Inputs[0].Id);
    }

    [Fact]
    public void CreateEncapsulation_BoundaryFanout_ExpansionRestoresEdgesWithoutMutatingOriginal()
    {
        NodeFactoryRegistrar.RegisterAll();
        var source = NodeTypeRegistry.Create("File.Binary.Read")!;
        var first = NodeTypeRegistry.Create("File.Binary.Read")!;
        var second = NodeTypeRegistry.Create("File.Binary.Read")!;
        first.Location = new Avalonia.Point(20, 30);
        second.Location = new Avalonia.Point(320, 150);
        var target = Node("target");
        var edges = new List<NodeConnectionViewModel> { new(first.Output[0], target.Input[0]) };
        var plan = CompositeGraphService.CreateEncapsulation("Pair", "tests.pair", [source, first, second, target],
            edges, [first, second]);

        Assert.Equal(3, plan.Nodes.Count);
        Assert.Single(plan.Connections);
        Assert.Same(first.Output[0], edges[0].Source);
        var expanded = CompositeGraphService.CreateExpansion(plan.Nodes, plan.Connections, plan.Composite!);
        Assert.Equal(4, expanded.Nodes.Count);
        Assert.Single(expanded.Connections);
        Assert.Same(target.Input[0], expanded.Connections[0].Target);
        Assert.DoesNotContain(expanded.Nodes, node => node.InstanceId == first.InstanceId);
        var restored = expanded.Nodes.Where(node => !ReferenceEquals(node, source) && !ReferenceEquals(node, target)).ToArray();
        Assert.Equal(300, restored[1].Location.X - restored[0].Location.X);
        Assert.Equal(120, restored[1].Location.Y - restored[0].Location.Y);
    }

    [Fact]
    public void Validate_DuplicateFanoutTarget_RejectsConflictingBindings()
    {
        var node = Node("target", true);
        var package = VisualPluginPackageBuilder.Create("Sample", "tests.sample", VisualPluginKind.CompositeNode, [node], []);
        var port = Assert.Single(package.Inputs);
        port.AdditionalBindings.Add(new VisualPluginPortBinding { NodeInstanceId = port.NodeInstanceId, PortId = port.PortId });

        Assert.Contains(VisualPluginValidation.Validate(package), error => error.Contains("exposed more than once", StringComparison.Ordinal));
    }

    private static FlowNodeViewModel Node(string title, bool required = false)
    {
        var node = new FlowNodeViewModel(title, NodeCategory.Data) { TypeKey = "Tests.Identity.Transform" };
        node.Input.Add(new NodeConnectorViewModel { PortId = "input", Title = "Value", PortType = NodePortType.Text, IsInput = true, IsRequired = required });
        node.Output.Add(new NodeConnectorViewModel { PortId = "output", Title = "Value", PortType = NodePortType.Text });
        return node;
    }

    private sealed class SilentLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }

    private sealed class SubmittedTextPresenter : IWorkflowInteractionService
    {
        public int Calls { get; private set; }
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new InteractionResult
            {
                Outcome = InteractionOutcome.Submitted,
                Values = new Dictionary<string, InteractionValue> { ["value"] = new() { Text = "submitted" } }
            });
        }
    }
}