using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkflowInteractionPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnsupportedInteraction_RejectsBeforeIndependentEffects(bool nested)
    {
        var effect = new EffectNode();
        FlowNodeViewModel interactive = InteractionNodeViewModel.Create("UI.Dialog.Confirm");
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
        var content = InteractionNodeViewModel.Create("UI.Content.Text");

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

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}