using AvaloniaEdit;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class WorkspaceDocumentNodeTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task ExecuteAsync_OpenUnsavedDocument_ChangesBufferWithoutSaving()
    {
        var token = TestContext.Current.CancellationToken;
        using var temporary = new UotanStudio.Tests.Comm.TestDoubles.TempDirectory();
        var path = Path.Combine(temporary.Path, "document.txt");
        await File.WriteAllTextAsync(path, "disk", token);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var workspace = new WorkspaceViewModel();
                var dock = workspace.FindDocumentsPanePublic()!;
                var document = new FileViewModel { Id = "test-document", FilePath = path, Text = "unsaved" };
                dock.VisibleDockables!.Add(document);
                dock.ActiveDockable = document;
                var resolve = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Workspace.Document.Resolve").Factory();
                var edit = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Config.Text.Edit").Factory();
                edit.Input.Single(port => port.Title == "Find").InitialValue = "unsaved";
                edit.Input.Single(port => port.Title == "Replacement").InitialValue = "node edit";
                var replace = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Workspace.Document.ReplaceText").Factory();

                var result = await new NodeExecutionEngine(new SilentLog()).ExecuteAsync([resolve, edit, replace],
                    [new NodeConnection
                    {
                        SourceInstanceId = resolve.InstanceId, SourceConnector = "Document",
                        TargetInstanceId = replace.InstanceId, TargetConnector = "Document"
                    }, new NodeConnection
                    {
                        SourceInstanceId = resolve.InstanceId, SourceConnector = "Text",
                        TargetInstanceId = edit.InstanceId, TargetConnector = "Text"
                    }, new NodeConnection
                    {
                        SourceInstanceId = edit.InstanceId, SourceConnector = "Text",
                        TargetInstanceId = replace.InstanceId, TargetConnector = "Text"
                    }], token, new NodeExecutionOptions { Documents = new WorkspaceWorkflowDocuments(workspace) });

                Assert.Equal(ExecutionStatus.Succeeded, result.Status);
                Assert.Equal("unsaved", resolve.Output.Single(port => port.Title == "Text").Data);
                Assert.Equal("node edit", document.Text);
                Assert.True(document.IsDirty);
            }, token);
        }
        await dispatch;
        Assert.Equal("disk", await File.ReadAllTextAsync(path, token));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("closed")]
    [InlineData("readonly")]
    [InlineData("cancelled")]
    public async Task ReplaceAsync_InvalidatedSnapshot_PreservesDocument(string condition)
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var workspace = new WorkspaceViewModel();
                var dock = workspace.FindDocumentsPanePublic()!;
                var document = new FileViewModel { Id = "document", Text = "original" };
                dock.VisibleDockables!.Add(document);
                dock.ActiveDockable = document;
                var service = new WorkspaceWorkflowDocuments(workspace);
                var snapshot = await service.ResolveAsync("document", token);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                switch (condition)
                {
                    case "changed":
                        document.Text = "user edit";
                        break;
                    case "closed":
                        dock.VisibleDockables.Remove(document);
                        break;
                    case "readonly":
                        document.Editor = new TextEditor { Text = "original", IsReadOnly = true };
                        break;
                    case "cancelled":
                        cancellation.Cancel();
                        break;
                }

                if (condition == "cancelled")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplaceAsync(snapshot, "replacement", cancellation.Token));
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceAsync(snapshot, "replacement", cancellation.Token));
                }

                Assert.Equal(condition == "changed" ? "user edit" : "original", document.Text);
            }, token);
        }
        await dispatch;
    }

    [Fact]
    public async Task ReplaceAsync_EditorAttached_CreatesSingleUndoStep()
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var workspace = new WorkspaceViewModel();
                var dock = workspace.FindDocumentsPanePublic()!;
                var editor = new TextEditor { Text = "before" };
                editor.Document.UndoStack.ClearAll();
                var document = new FileViewModel { Id = "document", Text = "before", Editor = editor };
                editor.TextChanged += (_, _) => document.Text = editor.Text;
                dock.VisibleDockables!.Add(document);
                dock.ActiveDockable = document;
                var service = new WorkspaceWorkflowDocuments(workspace);
                var snapshot = await service.ResolveAsync(string.Empty, token);

                await service.ReplaceAsync(snapshot, "after", token);
                editor.Undo();

                Assert.Equal("before", document.Text);
                Assert.False(editor.CanUndo);
            }, token);
        }
        await dispatch;
    }

    [Fact]
    public async Task ReadAsync_SelectionChanges_KeepsOriginalDocumentIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var workspace = new WorkspaceViewModel();
                var dock = workspace.FindDocumentsPanePublic()!;
                var first = new FileViewModel { Id = "first", Text = "first text" };
                var second = new FileViewModel { Id = "second", Text = "second text" };
                dock.VisibleDockables!.Add(first);
                dock.VisibleDockables.Add(second);
                dock.ActiveDockable = first;
                var service = new WorkspaceWorkflowDocuments(workspace);
                var snapshot = await service.ResolveAsync(string.Empty, token);
                dock.ActiveDockable = second;
                first.Text = "latest text";

                var refreshed = await service.ReadAsync(snapshot, token);

                Assert.Equal("latest text", refreshed.Text);
                Assert.Same(first, refreshed.Handle);
                await service.ReplaceAsync(refreshed, string.Empty, token);
                Assert.Empty(first.Text);
                Assert.Equal("second text", second.Text);
            }, token);
        }
        await dispatch;
    }

    private sealed class SilentLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}