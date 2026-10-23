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

public sealed class CompositeInteractionWaitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_ParallelChildDialogs_KeepsParentWaitingUntilLastChildEnds(bool cancel, bool nested)
    {
        NodeFactoryRegistrar.RegisterAll();
        var parallel = FlowControlNodeViewModel.CreateParallelNode();
        var first = InteractionNodeViewModel.Create("UI.Dialog");
        var second = InteractionNodeViewModel.Create("UI.Dialog");
        first.Input.Single(port => port.Title == "Mode").InitialValue = "Confirm";
        second.Input.Single(port => port.Title == "Mode").InitialValue = "Confirm";
        first.Input.Single(port => port.Title == "Title").InitialValue = "first";
        second.Input.Single(port => port.Title == "Title").InitialValue = "second";
        NodeConnectionViewModel[] edges =
        [
            new(parallel.Output.Single(port => port.Title == "Branch A"), first.Input.Single(port => port.Title == "Flow")),
            new(parallel.Output.Single(port => port.Title == "Branch B"), second.Input.Single(port => port.Title == "Flow"))
        ];
        var package = VisualPluginPackageBuilder.Create("Wait test", "tests.wait-propagation", VisualPluginKind.CompositeNode,
            [parallel, first, second], edges);
        var composite = new CompositeNodeViewModel(package);
        if (nested)
        {
            var outer = VisualPluginPackageBuilder.Create("Outer wait test", "tests.outer-wait", VisualPluginKind.CompositeNode, [composite], []);
            composite = new CompositeNodeViewModel(outer);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presenter = new ControlledPresenter();
        var context = new WaitContext(presenter, cancellation.Token, first.InstanceId);
        var run = composite.ExecuteAsync(context);
        try
        {
            await presenter.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, context.ActiveWaits);
            Assert.Equal(1, context.Starts);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            else
            {
                presenter.First.TrySetResult(new InteractionResult { Outcome = InteractionOutcome.Submitted });
                await context.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(1, context.ActiveWaits);
                Assert.Equal(0, context.Stops);
                presenter.Second.TrySetResult(new InteractionResult { Outcome = InteractionOutcome.Submitted });
                await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            Assert.Equal(0, context.ActiveWaits);
            Assert.Equal(1, context.Stops);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class ControlledPresenter : IWorkflowInteractionService
    {
        private int _started;
        public bool IsSupported => true;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InteractionResult> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InteractionResult> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) == 2)
            {
                BothStarted.TrySetResult();
            }
            return await (request.Form.Title == "first" ? First.Task : Second.Task).WaitAsync(cancellationToken);
        }
    }

    private sealed class WaitContext(IWorkflowInteractionService interactions, CancellationToken token, string firstId) : INodeExecutionContext, IUnifiedLog
    {
        private int _active;
        private int _starts;
        private int _stops;
        public int ActiveWaits => Volatile.Read(ref _active);
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public TaskCompletionSource FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWorkflowInteractionService Interactions => interactions;
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken => token;
        public IDisposable BeginUserInteractionWait()
        {
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _starts);
            return new InteractionWaitScope(() =>
            {
                Interlocked.Decrement(ref _active);
                Interlocked.Increment(ref _stops);
            });
        }
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message)
        {
            if (source == "Engine" && message == $"Node '{firstId}' completed")
            {
                FirstCompleted.TrySetResult();
            }
        }
        public void Write(LogLevel level, string source, string format, params object[] args) => Write(level, source, string.Format(format, args));
        public void SetMinimumLevel(LogLevel level) { }
    }
}