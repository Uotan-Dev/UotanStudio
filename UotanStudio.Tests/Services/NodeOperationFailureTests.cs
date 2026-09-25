using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeOperationFailureTests
{
    [Theory]
    [InlineData("Device.Partition.Flash")]
    [InlineData("Device.Partition.Erase")]
    [InlineData("Device.Power.Reboot")]
    [InlineData("Device.Storage.EraseAll")]
    [InlineData("Device.Ota.Sideload")]
    [InlineData("Firmware.Container.Unpack")]
    [InlineData("Firmware.Container.Pack")]
    [InlineData("Firmware.Container.Extract")]
    [InlineData("Firmware.Cpio.Unpack")]
    [InlineData("Firmware.Cpio.Pack")]
    [InlineData("Image.Super.Unpack")]
    [InlineData("Image.Super.Pack")]
    [InlineData("Image.Sparse.Convert")]
    [InlineData("Build.Dat.Convert")]
    [InlineData("Build.Ota.CreateDelta")]
    public async Task ExecuteAsync_MissingOperationInputs_FailsAndBlocksDependent(string typeKey)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        var dependent = new EffectNode();
        var source = node.Output.First();
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node, dependent],
            [new NodeConnection { SourceInstanceId = node.InstanceId, SourceConnector = source.Title,
                TargetInstanceId = dependent.InstanceId, TargetConnector = "Input" }], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = ConsentingInteractionHost.Instance });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
        Assert.False(dependent.Executed);
    }

    [Fact]
    public async Task ExecuteAsync_DestructiveNodeWithoutInteractiveHost_RejectedBeforeAnyEffect()
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.NotEqual(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Contains("destructive", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Destructive device graphs require explicit consent at preflight; this host grants it without UI.</summary>
    private sealed class ConsentingInteractionHost : IWorkflowInteractionService
    {
        internal static readonly ConsentingInteractionHost Instance = new();
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InteractionResult { Outcome = InteractionOutcome.Submitted });
    }

    private sealed class EffectNode : FlowNodeViewModel
    {
        internal bool Executed { get; private set; }

        internal EffectNode() : base("Dependent effect", NodeCategory.Output)
        {
            Input.Add(new NodeConnectorViewModel { Title = "Input" });
        }

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