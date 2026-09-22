using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class CompositeCollapseTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task CollapseExpandedComposite_AfterExpansion_RestoresOriginalCompositeAndSupportsRedo()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var first = NodeTypeRegistry.Create("Data.Value.Constant")!;
            var second = NodeTypeRegistry.Create("Data.Value.Constant")!;
            editor.AddNode(first);
            editor.AddNode(second);
            editor.SetSelection([first, second]);
            editor.EncapsulateSelection("Pair", editor.AnalyzeSelection());

            var composite = Assert.IsType<CompositeNodeViewModel>(Assert.Single(editor.Nodes));
            editor.ExpandSelectedComposite();

            Assert.True(editor.CanCollapseExpandedComposite);
            Assert.Equal(2, editor.Nodes.Count);

            editor.CollapseExpandedComposite();

            Assert.False(editor.CanCollapseExpandedComposite);
            Assert.Same(composite, Assert.Single(editor.Nodes));

            editor.Redo();

            Assert.True(editor.CanCollapseExpandedComposite);
            Assert.Equal(2, editor.Nodes.Count);
        });
    }

    [Fact]
    public async Task CollapseExpandedComposite_AfterSubsequentEdit_IsUnavailable()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
            editor.AddNode(node);
            editor.SetSelection([node]);
            editor.EncapsulateSelection("Single", editor.AnalyzeSelection());
            editor.ExpandSelectedComposite();
            editor.AddNode(NodeTypeRegistry.Create("Data.Value.Constant")!);

            Assert.False(editor.CanCollapseExpandedComposite);
        });
    }

    [Fact]
    public async Task CollapseExpandedCompositeButton_AfterExpansion_BecomesVisible()
    {
        await Dispatch(() =>
        {
            using var editor = new NodeEditorViewModel();
            var node = NodeTypeRegistry.Create("Data.Value.Constant")!;
            editor.AddNode(node);
            editor.SetSelection([node]);
            editor.EncapsulateSelection("Single", editor.AnalyzeSelection());

            var view = new NodeEditorView { DataContext = editor };
            var window = new Window { Content = view, Width = 1200, Height = 700 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var button = view.FindControl<Button>("CollapseCompositeButton")!;
                Assert.False(button.IsVisible);

                editor.ExpandSelectedComposite();
                Dispatcher.UIThread.RunJobs();

                Assert.True(button.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private async Task Dispatch(Action action)
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                action();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
