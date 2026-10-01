using System.Reflection;
using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class ExecutionLogSummaryTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData(false, "Node_Run", "Node_Patch_RunFinished")]
    [InlineData(true, "Node_Patch_Preview", "Node_Patch_PreviewFinished")]
    public async Task ExecuteAsync_CompletedWorkflow_ShowsOnlyStartAndOutcomeSummary(bool preview, string startKey, string outcomeKey)
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(async () =>
            {
                using var editor = new NodeEditorViewModel();
                editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);

                if (preview)
                {
                    await editor.PreviewCommand.ExecuteAsync(null);
                }
                else
                {
                    await editor.RunCommand.ExecuteAsync(null);
                }

                Assert.Equal(
                    [LanguageService.Instance.GetString(startKey), LanguageService.Instance.GetString(outcomeKey)],
                    editor.ExecutionLog);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendExecutionLog_CurrentRunRetainsAllEntries()
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var appendExecutionLog = typeof(NodeEditorViewModel).GetMethod(
                    "AppendExecutionLog",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(appendExecutionLog);
                for (var i = 0; i < 21; i++)
                {
                    appendExecutionLog.Invoke(editor, [$"entry-{i}"]);
                }

                Assert.Equal(21, editor.ExecutionLog.Count);
                Assert.Equal("entry-0", editor.ExecutionLog[0]);
                Assert.Equal("entry-20", editor.ExecutionLog[^1]);
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}