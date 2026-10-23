using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Regression coverage for the policies retained by shared entry-point implementations.</summary>
[Collection("Workspace state")]
public sealed class SharedEntryPointTests
{
    [Fact]
    public void ProgressThrottle_PreservesPartitionScopeStatusTimingAndCompletion()
    {
        var throttle = new PartitionProgressThrottle();
        Assert.True(throttle.ShouldReport("boot", 0, "writing", 1000));
        Assert.False(throttle.ShouldReport("boot", 50, "writing", 1099));
        Assert.True(throttle.ShouldReport("boot", 60, "writing", 1100));
        Assert.True(throttle.ShouldReport("system", 0, "writing", 1101));
        Assert.True(throttle.ShouldReport("boot", 60, "verifying", 1102));
        Assert.True(throttle.ShouldReport("boot", 100, "verifying", 1103));
        Assert.True(throttle.ShouldReport("boot", 100, "verifying", 1103));
        Assert.True(throttle.ShouldReport(null, 1, "writing", 1200));
        Assert.False(throttle.ShouldReport("", 2, "writing", 1201));
        Assert.True(new PartitionProgressThrottle().ShouldReport("boot", 0, "verifying", 1104));
    }

    [Fact]
    public void DocumentRefresh_PreservesRepeatedSynchronousNotificationsAndTypedPayload()
    {
        var recipient = new object();
        var received = new List<ActiveDocumentChangedMessage>();
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(recipient, (_, message) => received.Add(message));
        try
        {
            var text = new FileViewModel();
            DocumentNotifications.Publish(text);
            DocumentNotifications.Publish(text);
            Assert.Equal(2, received.Count);
            Assert.All(received, message =>
            {
                Assert.Same(text, message.Document);
                Assert.Same(text, message.ActiveDocument);
                Assert.Null(message.HexDocument);
            });
            var hex = new HexEditorViewModel();
            DocumentNotifications.Publish(hex);
            Assert.Same(hex, received[2].HexDocument);
            Assert.Null(received[2].Document);
            var generic = new Document();
            DocumentNotifications.Publish(generic);
            Assert.Same(generic, received[3].ActiveDocument);
            Assert.Null(received[3].Document);
            DocumentNotifications.Publish(null);
            Assert.Null(received[4].ActiveDocument);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public void SaveCapabilities_PreserveSupportedEditorsAndDirtyState()
    {
        object[] editors = [new FileViewModel { IsDirty = true }, new HexEditorViewModel { IsDirty = true },
            new DtbEditorViewModel { IsDirty = true }, new ManifestEditorViewModel { IsDirty = true },
            new PartitionEditorViewModel { IsDirty = true }];
        Assert.All(editors, editor =>
        {
            Assert.True(DocumentSaveService.CanSave(editor));
            Assert.True(DocumentSaveService.IsDirty(editor));
        });
        Assert.False(DocumentSaveService.CanSave(new DexEditorViewModel()));
        Assert.False(DocumentSaveService.CanSave(new ArscEditorViewModel()));
        Assert.False(DocumentSaveService.IsDirty(new Document { IsModified = true }));
        Assert.False(DocumentSaveService.CanSave(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkflowEndpoints_ExplicitIdentityNeverFallsBackToAnotherNode(bool output)
    {
        var identified = new FlowNodeViewModel("identified", NodeCategory.Data);
        var fallback = new FlowNodeViewModel("fallback", NodeCategory.Data);
        var port = new NodeConnectorViewModel { Title = "Value" };
        (output ? fallback.Output : fallback.Input).Add(port);
        FlowNodeViewModel[] nodes = [identified, fallback];
        var ids = new Dictionary<string, FlowNodeViewModel>(StringComparer.OrdinalIgnoreCase) { ["saved-id"] = identified };
        var cli = WorkflowReconstruction.ResolveEndpoint(nodes, ids, "SAVED-ID", "Value", output);
        Assert.Same(identified, cli.Node);
        Assert.Null(cli.Port);
        var canvas = WorkflowReconstruction.ResolveEndpoint(nodes, ids, null, "Value", output);
        Assert.Same(fallback, canvas.Node);
        Assert.Same(port, canvas.Port);
        Assert.Null(WorkflowReconstruction.ResolveEndpoint(nodes, ids, "missing", "Value", output).Node);
        Assert.Null(WorkflowReconstruction.ResolveEndpoint(nodes, ids, null, "value", output).Node);
    }

    [Fact]
    public void WorkflowInputs_RestoreTypedValuesWithoutChangingRuntimeDataOrFlow()
    {
        var node = new FlowNodeViewModel("inputs", NodeCategory.Data);
        var number = new NodeConnectorViewModel { Title = "Number", PortType = NodePortType.Number, Data = "runtime" };
        var boolean = new NodeConnectorViewModel { Title = "Boolean", PortType = NodePortType.Boolean };
        var flow = new NodeConnectorViewModel { Title = "Flow", PortType = NodePortType.Flow, InitialValue = "keep" };
        node.Input.Add(number);
        node.Input.Add(boolean);
        node.Input.Add(flow);
        WorkflowReconstruction.RestoreInitialValues(node, new() { ["Number"] = "1.25", ["Boolean"] = "true", ["Flow"] = "replace", ["Missing"] = "ignored" });
        Assert.Equal(1.25, number.InitialValue);
        Assert.True(Assert.IsType<bool>(boolean.InitialValue));
        Assert.Equal("runtime", number.Data);
        Assert.Equal("keep", flow.InitialValue);
        WorkflowReconstruction.RestoreInitialValues(node, new() { ["Number"] = "invalid" });
        Assert.Equal("invalid", number.InitialValue);
    }
}