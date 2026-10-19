using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkflowInteractionPreflightTests
{
    [Theory]
    [InlineData("Workspace.Document.Resolve", false)]
    [InlineData("Workspace.Document.Resolve", true)]
    [InlineData("Workspace.Document.ReadText", false)]
    [InlineData("Workspace.Document.ReadText", true)]
    [InlineData("Workspace.Document.ReplaceText", false)]
    [InlineData("Workspace.Document.ReplaceText", true)]
    public async Task ExecuteAsync_DocumentNodeWithoutHost_RejectsBeforeIndependentEffects(string typeKey, bool nested)
    {
        var effect = new EffectNode();
        var document = CreateDocumentGraph(typeKey, nested);

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync(
            [effect, document], [], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(LanguageService.Instance.GetString("Node_Document_Unsupported"), result.ErrorMessage);
        Assert.False(effect.Executed);
        Assert.Equal(0, result.ExecutedCount);
    }

    [Theory]
    [InlineData("Workspace.Document.Resolve", false)]
    [InlineData("Workspace.Document.Resolve", true)]
    [InlineData("Workspace.Document.ReadText", false)]
    [InlineData("Workspace.Document.ReadText", true)]
    [InlineData("Workspace.Document.ReplaceText", false)]
    [InlineData("Workspace.Document.ReplaceText", true)]
    public void Validate_DocumentNodeWithHost_AcceptsGraph(string typeKey, bool nested)
    {
        var document = CreateDocumentGraph(typeKey, nested);
        var host = new RecordingInteractionHost(InteractionOutcome.Submitted);

        var error = WorkflowInteractionPreflight.Validate([document], host, supportsDocuments: true);

        Assert.Null(error);
        Assert.Empty(host.Requests);
    }

    private static FlowNodeViewModel CreateDocumentGraph(string typeKey, bool nested)
    {
        var document = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        return nested ? Wrap(Wrap(document, "document.inner"), "document.outer") : document;
    }

    [Theory]
    [InlineData(InteractionOutcome.Submitted)]
    [InlineData(InteractionOutcome.Cancelled)]
    public async Task ConfirmDestructiveAsync_UnregisteredNestedPackage_RequestsConsent(InteractionOutcome outcome)
    {
        var destructive = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();
        var inner = Wrap(destructive, "destructive.inner");
        var outer = Wrap(inner, "destructive.outer");
        var host = new RecordingInteractionHost(outcome);
        Assert.False(NodeFactoryRegistrar.TryGetVisualPluginPackage(inner.TypeKey, out _));
        Assert.False(NodeFactoryRegistrar.TryGetVisualPluginPackage(outer.TypeKey, out _));
        Assert.Null(WorkflowInteractionPreflight.Validate([outer], host));

        var error = await WorkflowInteractionPreflight.ConfirmDestructiveAsync([outer], host, TestContext.Current.CancellationToken);

        var request = Assert.Single(host.Requests);
        var displayName = LanguageService.Instance.GetString("Node_Device_Partition_Flash");
        Assert.Equal(displayName == "Node_Device_Partition_Flash" ? destructive.TypeKey : displayName, request.Form.Content.Description);
        if (outcome == InteractionOutcome.Submitted)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotEmpty(error ?? string.Empty);
        }
    }

    [Theory]
    [InlineData(InteractionOutcome.Cancelled)]
    [InlineData(InteractionOutcome.TimedOut)]
    public async Task ExecuteAsync_UnregisteredNestedDestructivePackageRejected_PreventsIndependentEffects(InteractionOutcome outcome)
    {
        var effect = new EffectNode();
        var destructive = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();
        var inner = Wrap(destructive, "rejected.inner");
        var outer = Wrap(inner, "rejected.outer");
        var host = new RecordingInteractionHost(outcome);
        Assert.False(NodeFactoryRegistrar.TryGetVisualPluginPackage(inner.TypeKey, out _));
        Assert.False(NodeFactoryRegistrar.TryGetVisualPluginPackage(outer.TypeKey, out _));

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync(
            [effect, outer], [], TestContext.Current.CancellationToken, new NodeExecutionOptions { Interactions = host });

        Assert.Single(host.Requests);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, result.ExecutedCount);
        Assert.False(effect.Executed);
        Assert.NotEmpty(result.ErrorMessage ?? string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnsupportedInteraction_RejectsBeforeIndependentEffects(bool nested)
    {
        var effect = new EffectNode();
        FlowNodeViewModel interactive = InteractionNodeViewModel.Create("UI.Dialog");
        interactive.Input.Single(port => port.Title == "Mode").InitialValue = "Confirm";
        if (nested)
        {
            interactive = Wrap(Wrap(interactive, "inner"), "outer");
        }

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([effect, interactive], [], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(effect.Executed);
        Assert.Equal(0, result.ExecutedCount);
        Assert.NotEmpty(result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_UiDefinitionsOnly_DoesNotRequireInteractiveHost()
    {
        var content = InteractionTestNodes.Create("UI.Content.Text");

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([content], [], TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
    }

    private static CompositeNodeViewModel Wrap(FlowNodeViewModel node, string suffix)
        => new(new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "test.preflight." + suffix, Name = suffix },
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) }
        });

    private sealed class EffectNode() : FlowNodeViewModel("Effect", NodeCategory.Output)
    {
        internal bool Executed { get; private set; }
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingInteractionHost(InteractionOutcome outcome) : IWorkflowInteractionService
    {
        public bool IsSupported => true;
        internal List<InteractionRequest> Requests { get; } = [];

        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new InteractionResult { Outcome = outcome });
        }
    }

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}