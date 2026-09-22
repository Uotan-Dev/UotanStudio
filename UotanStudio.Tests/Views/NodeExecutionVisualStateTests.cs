using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class NodeExecutionVisualStateTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task NodeExecutionStatus_SucceedsWhileUnselected_UpdatesCanvasBorderImmediately()
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
                editor.AddNode(node);

                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1200, Height = 700 };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    var canvasNode = Assert.Single(view.GetVisualDescendants()
                        .OfType<NodifyM.Avalonia.Controls.Node>(), item => item.DataContext == node);
                    Assert.False(node.IsSelected);

                    node.ExecutionStatus = NodeExecutionStatus.Succeeded;
                    Dispatcher.UIThread.RunJobs();

                    Assert.Same(node.NodeBorderBrush, canvasNode.BorderBrush);
                }
                finally
                {
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
