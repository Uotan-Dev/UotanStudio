using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace state")]
public sealed class ProjectOperationsDirtyStateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly NodeEditorViewModel _editor = new();
    private readonly ProjectWorkspaceCoordinator _workspace;
    private readonly ProjectOperationsService _projects;

    public ProjectOperationsDirtyStateTests()
    {
        ProjectContextService.Instance.CloseProject();
        ImageViewModel.EnsureInstance().Images.Clear();
        _workspace = new ProjectWorkspaceCoordinator { NodeEditor = _editor };
        _projects = new ProjectOperationsService(_workspace);
    }

    [Fact]
    public async Task AutoSave_SuccessfulTick_ClearsEveryNodeDocumentDirtyFlag()
    {
        OpenDirtyDocument();

        await _projects.AutoSaveAsync(persistProject: false);

        Assert.False(_editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task AutoSave_WhileAnotherOperationOwnsTheGate_LeavesDirtyFlagsSet()
    {
        OpenDirtyDocument();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = _projects.ExecuteFileOperationAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            await _projects.AutoSaveAsync(persistProject: false);
            Assert.True(_editor.HasUnsavedChanges);
        }
        finally
        {
            release.TrySetResult();
        }
        await operation;

        await _projects.AutoSaveAsync(persistProject: false);

        Assert.False(_editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAs_SuccessfulPublication_ClearsEveryNodeDocumentDirtyFlag()
    {
        OpenDirtyDocument();
        var target = Path.Combine(_temp.Path, "copy.uotan");

        await _projects.SaveAsAsync(target);

        Assert.Equal(target, _projects.CurrentProjectPath);
        Assert.False(_editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveAs_FailedPublication_LeavesDirtyFlagsSet()
    {
        OpenDirtyDocument();
        var target = Path.Combine(_temp.Path, "no-extension");

        await Assert.ThrowsAsync<ArgumentException>(() => _projects.SaveAsAsync(target));

        Assert.True(_editor.HasUnsavedChanges);
    }

    private void OpenDirtyDocument()
    {
        _editor.OpenDocument(new NodeProjectDocument("flow"));
        _editor.MarkDirty();
        Assert.True(_editor.HasUnsavedChanges);
    }

    public void Dispose()
    {
        ProjectContextService.Instance.CloseProject();
        ImageViewModel.EnsureInstance().Images.Clear();
        _editor.Dispose();
        _temp.Dispose();
    }
}