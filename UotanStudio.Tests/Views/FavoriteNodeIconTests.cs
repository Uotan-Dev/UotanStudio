using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FavoriteNodeIconTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task FavoriteNode_FavoriteButton_AppliesThemeClassOnlyWhenFavorited()
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var item = editor.VisibleToolboxCategories.SelectMany(category => category.Items).First();
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1200, Height = 700 };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    // The same item instance is realized in the expanded Favorites/Recent shortcut category and in
                    // its own category, so every matching favorite button must follow the item's favorite state.
                    var buttons = view.GetVisualDescendants().OfType<Button>()
                        .Where(candidate => ReferenceEquals(candidate.DataContext, item)
                            && candidate.Command == item.ToggleFavoriteCommand).ToArray();
                    Assert.NotEmpty(buttons);
                    Assert.All(buttons, button => Assert.DoesNotContain("favorite", button.Classes));

                    item.IsFavorite = true;
                    Dispatcher.UIThread.RunJobs();

                    Assert.All(buttons, button => Assert.Contains("favorite", button.Classes));
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