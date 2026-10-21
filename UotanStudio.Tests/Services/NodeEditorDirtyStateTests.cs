using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Node editor UI")]
public sealed class NodeEditorDirtyStateTests
{
    [Fact]
    public void AddNode_AfterNodeAdded_IsDirtyAndHasUnsavedChangesBecomeTrue()
    {
        using var editor = new NodeEditorViewModel();
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasUnsavedChanges);

        editor.AddNode(NewNode("first"));

        Assert.True(editor.IsDirty);
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(editor.ActiveDocument.IsDirty);
    }

    [Fact]
    public void MarkSaved_AfterNodeWasAdded_ClearsIsDirtyAndHasUnsavedChanges()
    {
        using var editor = new NodeEditorViewModel();
        editor.AddNode(NewNode("first"));

        editor.MarkSaved();

        Assert.False(editor.IsDirty);
        Assert.False(editor.HasUnsavedChanges);
        Assert.False(editor.ActiveDocument.IsDirty);
    }

    [Fact]
    public void MarkAllSaved_WithTwoDirtyDocuments_ClearsEveryDocument()
    {
        using var editor = new NodeEditorViewModel();
        var first = new NodeProjectDocument("first");
        var second = new NodeProjectDocument("second");
        editor.OpenDocument(first);
        editor.AddNode(NewNode("first-node"));
        editor.OpenDocument(second);
        editor.AddNode(NewNode("second-node"));
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.True(editor.HasUnsavedChanges);

        editor.MarkAllSaved();

        Assert.False(first.IsDirty);
        Assert.False(second.IsDirty);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task LoadFromAsync_AfterDirtyDraft_ReplacesTheCanvasAndClearsDirty()
    {
        using var editor = new NodeEditorViewModel();
        editor.AddNode(NewNode("first"));
        Assert.True(editor.IsDirty);

        await editor.LoadFromAsync(new WorkflowSnapshot());

        Assert.Empty(editor.Nodes);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasUnsavedChanges);
    }

    private static FlowNodeViewModel NewNode(string title) => new(title, NodeCategory.Data);
}