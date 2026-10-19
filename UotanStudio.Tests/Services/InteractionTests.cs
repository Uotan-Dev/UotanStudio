using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class InteractionTests
{
    [Theory]
    [InlineData("input-text.json")]
    [InlineData("conditional-form.json")]
    public async Task ExecuteAsync_ExampleComposite_LoadsAndReturnsMockedSubmittedValues(string name)
    {
        NodeFactoryRegistrar.RegisterAll();
        var package = await VisualPluginFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Nodes", "Examples", name), TestContext.Current.CancellationToken);
        var composite = new CompositeNodeViewModel(package);
        var values = name == "input-text.json"
            ? new Dictionary<string, InteractionValue> { ["value"] = new() { Text = "test input" } }
            : new Dictionary<string, InteractionValue> { ["advanced"] = new() { Boolean = true }, ["count"] = new() { Number = 3 } };
        var presenter = new ResultPresenter(new InteractionResult { Outcome = InteractionOutcome.Submitted, Values = values });
        var engine = new NodeExecutionEngine(new TestExecutionContext(presenter));
        var result = await engine.ExecuteAsync([composite], [], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = new QueuedWorkflowInteractionService(presenter) });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(true, composite.Output.Single(port => port.PortId == "submitted").Data);
        if (name == "input-text.json")
        {
            Assert.Equal("test input", composite.Output.Single(port => port.PortId == "value").Data);
        }
        else
        {
            var record = Assert.IsType<InteractionRecord>(composite.Output.Single(port => port.PortId == "values").Data);
            Assert.Equal(3, record.Values["count"].Number);
        }
    }

    [Fact]
    public void BuildNodeDtos_SensitiveDefault_OmitsValueAndMasksPreview()
    {
        var field = InteractionTestNodes.Create("UI.Field.Text");
        field.Input.Single(port => port.Title == "Sensitive").InitialValue = true;
        var secret = field.Input.Single(port => port.Title == "Default");
        secret.InitialValue = "do-not-save";
        secret.Data = "do-not-save";
        var dto = Assert.Single(WorkflowPersistenceService.BuildNodeDtos([field]));
        Assert.False(secret.HasPersistableInitialValue);
        Assert.False(dto.InputInitialValues?.ContainsKey("Default") == true);
        Assert.False(dto.InputInitialValuesByPortId?.ContainsKey(secret.StableId) == true);
        Assert.DoesNotContain("do-not-save", secret.DataPreview, StringComparison.Ordinal);
    }
    [Fact]
    public void Refresh_StaticForm_AutoExposesTypedStableFieldAndPreservesConnectionOnRename()
    {
        var field = InteractionTestNodes.Create("UI.Field.Number");
        field.Input.Single(port => port.Title == "Field ID").InitialValue = "capacity";
        field.Input.Single(port => port.Title == "Label").InitialValue = "Capacity";
        var form = InteractionNodeViewModel.Create("UI.Form.Create");
        var dialog = InteractionNodeViewModel.Create("UI.Dialog");
        FlowNodeViewModel[] nodes = [field, form, dialog];
        NodeConnectionViewModel[] edges =
        [
            new(field.Output[0], form.Input.Single(port => port.Title == "Content")),
            new(form.Output[0], dialog.Input.Single(port => port.Title == "Form"))
        ];
        InteractionSchemaResolver.Refresh(nodes, edges);
        var output = Assert.Single(dialog.Output, port => port.PortId == "field.capacity");
        Assert.Equal(NodePortType.Number, output.PortType);
        Assert.Equal("Capacity", output.LocalizedTitle);
        field.Input.Single(port => port.Title == "Label").InitialValue = "New label";
        InteractionSchemaResolver.Refresh(nodes, edges);
        Assert.Same(output, dialog.Output.Single(port => port.PortId == "field.capacity"));
        Assert.Equal("New label", output.LocalizedTitle);
    }

    [Fact]
    public void ApplyFieldSchema_ConnectedFieldTypeChanges_KeepsEndpointAndBlocksExecution()
    {
        var dialog = InteractionNodeViewModel.Create("UI.Dialog");
        dialog.ApplyFieldSchema([new InteractionFieldSchema("field", "Value", NodePortType.Text)], true, []);
        var output = dialog.Output.Single(port => port.PortId == "field.field");
        var target = new NodeConnectorViewModel { Title = "Target", PortType = NodePortType.Text, IsInput = true };
        NodeConnectionViewModel[] connections = [new(output, target)];
        dialog.ApplyFieldSchema([new InteractionFieldSchema("field", "Value", NodePortType.Number)], true, connections);
        Assert.NotNull(dialog.SchemaError);
        Assert.Same(output, connections[0].Source);
        Assert.Equal(NodePortType.Text, output.PortType);
    }

    [Fact]
    public async Task ExecuteAsync_ProgressWithoutClose_DisposesRunOwnedHandle()
    {
        var feedback = new FeedbackPresenter();
        var engine = new NodeExecutionEngine(new TestExecutionContext(feedback));
        var node = InteractionNodeViewModel.Create("UI.Progress");
        node.Input.Single(port => port.Title == "Mode").InitialValue = "Open";
        var result = await engine.ExecuteAsync([node], [], TestContext.Current.CancellationToken, new NodeExecutionOptions { Interactions = feedback });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(feedback.Handle.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_DialogCancelled_FollowsOnlyCancelBranchAndReportsWait()
    {
        var presenter = new ResultPresenter(new InteractionResult { Outcome = InteractionOutcome.Cancelled });
        var engine = new NodeExecutionEngine(new TestExecutionContext(presenter));
        var node = InteractionNodeViewModel.Create("UI.Dialog");
        node.Input.Single(port => port.Title == "Mode").InitialValue = "Confirm";
        var submitted = new BranchProbe();
        var cancelled = new BranchProbe();
        var waiting = false;
        engine.NodeStatusChanged += (_, e) => waiting |= e.Node.ExecutionStatus == NodeExecutionStatus.WaitingForInput;
        NodeConnection[] edges =
        [
            new() { SourceInstanceId = node.InstanceId, SourceConnector = "Submitted", TargetInstanceId = submitted.InstanceId, TargetConnector = "Flow" },
            new() { SourceInstanceId = node.InstanceId, SourceConnector = "Cancelled", TargetInstanceId = cancelled.InstanceId, TargetConnector = "Flow" }
        ];
        await engine.ExecuteAsync([node, submitted, cancelled], edges, TestContext.Current.CancellationToken, new NodeExecutionOptions { Interactions = presenter });
        Assert.True(waiting);
        Assert.False(submitted.Executed);
        Assert.True(cancelled.Executed);
    }

    [Fact]
    public void Submit_ConditionalRequiredField_ValidatesOnlyActiveFieldsAndUsesStableIds()
    {
        var enabled = new InteractionElement { Id = "advanced", Kind = InteractionElementKind.BooleanField };
        var value = new InteractionElement
        {
            Id = "value",
            Label = "Renamed label",
            Kind = InteractionElementKind.NumberField,
            Required = true,
            Minimum = 1,
            Maximum = 10,
            VisibleWhen = new InteractionCondition { FieldId = "advanced", EqualsValue = new InteractionValue { Boolean = true } }
        };
        var state = new InteractionFormState(Form(enabled, value));
        Assert.Empty(state.Validate());
        Assert.False(state.Submit().Values.ContainsKey("value"));
        state.SetValue("advanced", new InteractionValue { Boolean = true });
        Assert.Equal("Number", state.Validate()["value"]);
        state.SetValue("value", new InteractionValue { Number = 11 });
        Assert.Equal("Range", state.Validate()["value"]);
        state.SetValue("value", new InteractionValue { Number = 5 });
        Assert.Equal(5, state.Submit().Values["value"].Number);
    }

    [Fact]
    public void Construct_DuplicateFieldIdsOrMissingConditions_RejectsBeforePresentation()
    {
        var field = new InteractionElement { Id = "field", Kind = InteractionElementKind.TextField };
        Assert.Throws<InvalidDataException>(() => new InteractionFormState(Form(field, field)));
        Assert.Throws<InvalidDataException>(() => new InteractionFormState(Form(field with
        {
            EnabledWhen = new InteractionCondition { FieldId = "missing", EqualsValue = new InteractionValue { Text = "x" } }
        })));
    }

    [Fact]
    public void Construct_TooDeepOrUnrenderableSchema_RejectsBeforePresentation()
    {
        var nested = new InteractionElement { Id = "leaf", Kind = InteractionElementKind.Text };
        for (var depth = 0; depth < 18; depth++)
        {
            nested = new InteractionElement { Id = "group" + depth, Kind = InteractionElementKind.Column, Children = [nested] };
        }
        Assert.Throws<InvalidDataException>(() => new InteractionFormState(new InteractionForm { Content = nested }));
        Assert.Throws<InvalidDataException>(() => new InteractionFormState(Form(new InteractionElement
        {
            Id = "text",
            Kind = InteractionElementKind.TextField,
            Children = [new InteractionElement { Id = "unrendered", Kind = InteractionElementKind.TextField, Required = true }]
        })));
    }

    [Fact]
    public async Task ShowAsync_PresenterReturnsUnknownField_RejectsInsteadOfLeakingData()
    {
        var presenter = new ResultPresenter(new InteractionResult
        {
            Outcome = InteractionOutcome.Submitted,
            Values = new Dictionary<string, InteractionValue> { ["unexpected"] = new() { Text = "untrusted" } }
        });
        var queue = new QueuedWorkflowInteractionService(presenter);
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.ShowAsync(new InteractionRequest { Form = Form() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Submit_DisabledParent_OmitsDescendantValues()
    {
        var state = new InteractionFormState(Form(
            new InteractionElement { Id = "switch", Kind = InteractionElementKind.BooleanField },
            new InteractionElement
            {
                Id = "group",
                Kind = InteractionElementKind.Group,
                EnabledWhen = new InteractionCondition { FieldId = "switch", EqualsValue = new InteractionValue { Boolean = true } },
                Children = [new InteractionElement { Id = "secret", Kind = InteractionElementKind.TextField, Sensitive = true, Required = true }]
            }));
        Assert.Empty(state.Validate());
        Assert.False(state.Submit().Values.ContainsKey("secret"));
    }

    [Fact]
    public void Submit_WrongValueRepresentation_RejectsTypeMismatch()
    {
        var state = new InteractionFormState(Form(new InteractionElement { Id = "flag", Kind = InteractionElementKind.BooleanField }));
        state.SetValue("flag", new InteractionValue { Text = "yes" });
        Assert.Equal("Type", state.Validate()["flag"]);
        Assert.Throws<InvalidOperationException>(state.Submit);
    }

    [Fact]
    public async Task ShowAsync_Timeout_ClosesPresenterAndReturnsNoData()
    {
        var presenter = new WaitingPresenter();
        var service = new QueuedWorkflowInteractionService(presenter);
        var result = await service.ShowAsync(new InteractionRequest { Form = Form(), Timeout = TimeSpan.FromMilliseconds(20) }, TestContext.Current.CancellationToken);
        Assert.Equal(InteractionOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.Values);
        Assert.Equal(0, presenter.Active);
    }

    [Fact]
    public async Task ShowAsync_WorkflowCancellation_PropagatesAfterPresenterCloses()
    {
        var presenter = new WaitingPresenter();
        var service = new QueuedWorkflowInteractionService(presenter);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = service.ShowAsync(new InteractionRequest { Form = Form() }, cancellation.Token);
        await presenter.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, presenter.Active);
    }

    [Fact]
    public async Task ShowAsync_ConcurrentRequests_SerializesPresenters()
    {
        var presenter = new WaitingPresenter();
        var service = new QueuedWorkflowInteractionService(presenter);
        var request = new InteractionRequest { Form = Form(), Timeout = TimeSpan.FromMilliseconds(20) };
        await Task.WhenAll(service.ShowAsync(request, TestContext.Current.CancellationToken), service.ShowAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, presenter.MaximumActive);
        Assert.Equal(2, presenter.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDialog_DoesNotLeakPreviousSubmittedValues()
    {
        var node = InteractionNodeViewModel.Create("UI.Dialog");
        foreach (var port in node.Input)
        {
            port.Data = port.InitialValue;
        }
        node.Input.Single(port => port.Title == "Form").Data = Form(new InteractionElement { Id = "value", Kind = InteractionElementKind.TextField });
        var presenter = new ResultPresenter(new InteractionResult
        {
            Outcome = InteractionOutcome.Submitted,
            Values = new Dictionary<string, InteractionValue> { ["value"] = new() { Text = "secret" } }
        });
        var context = new TestExecutionContext(presenter);
        await node.ExecuteAsync(context);
        Assert.IsType<InteractionRecord>(node.Output.Single(port => port.Title == "Values").Data);
        presenter.Result = new InteractionResult { Outcome = InteractionOutcome.Cancelled };
        await node.ExecuteAsync(context);
        Assert.Null(node.Output.Single(port => port.Title == "Values").Data);
        Assert.Equal(false, node.Output.Single(port => port.Title == "Submitted").Data);
        Assert.Equal(true, node.Output.Single(port => port.Title == "Cancelled").Data);
    }

    [Fact]
    public async Task ExecuteAsync_ComposedFieldAndForm_PreservesFieldIdentityAcrossLabelChanges()
    {
        var field = InteractionTestNodes.Create("UI.Field.Text");
        foreach (var port in field.Input)
        {
            port.Data = port.InitialValue;
        }
        field.Input.Single(port => port.Title == "Label").Data = "First label";
        var context = new TestExecutionContext(UnsupportedWorkflowInteractionService.Instance);
        await field.ExecuteAsync(context);
        var first = Assert.IsType<InteractionElement>(field.Output[0].Data);
        field.Input.Single(port => port.Title == "Label").Data = "Second label";
        await field.ExecuteAsync(context);
        var second = Assert.IsType<InteractionElement>(field.Output[0].Data);
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Label, second.Label);
    }

    private static InteractionForm Form(params InteractionElement[] fields) => new()
    {
        Content = new InteractionElement { Id = "root", Kind = InteractionElementKind.Column, Children = fields }
    };

    private sealed class WaitingPresenter : IWorkflowInteractionService
    {
        public bool IsSupported => true;
        public int Active { get; private set; }
        public int MaximumActive { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            Active++;
            Calls++;
            MaximumActive = Math.Max(MaximumActive, Active);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The test presenter requires cancellation.");
            }
            finally
            {
                Active--;
            }
        }
    }

    private sealed class ResultPresenter(InteractionResult result) : IWorkflowInteractionService
    {
        public InteractionResult Result { get; set; } = result;
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class BranchProbe : FlowNodeViewModel
    {
        public BranchProbe() : base("Probe", NodeCategory.Flow)
        {
            Input.Add(new NodeConnectorViewModel { Title = "Flow", PortType = NodePortType.Flow });
        }
        public bool Executed { get; private set; }
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FeedbackPresenter : IWorkflowInteractionService, IWorkflowFeedbackService
    {
        public bool IsSupported => true;
        public ProgressHandle Handle { get; } = new();
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IInteractionProgress> OpenProgressAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult<IInteractionProgress>(Handle);
    }

    private sealed class ProgressHandle : IInteractionProgress
    {
        public bool Disposed { get; private set; }
        public Task UpdateAsync(string message, double? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestExecutionContext(IWorkflowInteractionService interactions) : INodeExecutionContext, IUnifiedLog
    {
        public IWorkflowInteractionService Interactions => interactions;
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}