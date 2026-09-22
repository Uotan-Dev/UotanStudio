using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Interactions;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class InteractionViewTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task ApplyLanguage_InteractionModule_ResolvesEveryNodeAndDialogActionInBothLanguages()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var language = LanguageService.Instance;
                var original = language.CurrentLanguage;
                language.Initialize();
                try
                {
                    foreach (var culture in new[] { "en-US", "zh-CN" })
                    {
                        language.ApplyLanguage(culture);
                        foreach (var typeKey in InteractionNodeViewModel.TypeKeys)
                        {
                            var key = "Node_" + typeKey.Replace('.', '_');
                            Assert.NotEqual(key, language.GetString(key));
                        }
                        Assert.Equal(culture == "zh-CN" ? "提交" : "Submit", language.GetString("Interaction_Submit"));
                        Assert.Equal(culture == "zh-CN" ? "取消" : "Cancel", language.GetString("Interaction_Cancel"));
                    }
                }
                finally
                {
                    language.ApplyLanguage(original);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    [Fact]
    public async Task Render_ConditionalField_UpdatesVisibilityAndSubmitValidation()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var state = new InteractionFormState(new InteractionForm
                {
                    SubmitLabel = "Submit",
                    CancelLabel = "Cancel",
                    Content = new InteractionElement
                    {
                        Id = "root",
                        Kind = InteractionElementKind.Column,
                        Children =
                        [
                            new InteractionElement { Id = "advanced", Kind = InteractionElementKind.BooleanField, Label = "Advanced" },
                            new InteractionElement
                            {
                                Id = "value", Kind = InteractionElementKind.TextField, Label = "Value", Required = true,
                                VisibleWhen = new InteractionCondition { FieldId = "advanced", EqualsValue = new InteractionValue { Boolean = true } }
                            }
                        ]
                    }
                });
                var view = new InteractionFormView(state);
                var window = new Window { Content = view, Width = 600, Height = 400 };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    var submit = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Submit"));
                    Assert.True(submit.IsEnabled);
                    var field = Assert.Single(view.GetVisualDescendants().OfType<TextBox>());
                    Assert.False(field.IsEffectivelyVisible);
                    var toggle = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>());
                    toggle.IsChecked = true;
                    Assert.True(field.IsEffectivelyVisible);
                    Assert.False(submit.IsEnabled);
                    field.Text = "test";
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(submit.IsEnabled);
                    Assert.Equal("test", state.Submit().Values["value"].Text);
                }
                finally
                {
                    window.Close();
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    [Fact]
    public async Task ShowAsync_EmbeddedCancellation_RestoresHostContentBeforeCompleting()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.DispatchAsync(async () =>
            {
                var previous = new TextBlock { Text = "Previous" };
                var host = new ContentControl { Content = previous };
                var window = new Window { Content = host };
                window.Show();
                try
                {
                    var service = new AvaloniaWorkflowInteractionService(embeddedHost: host);
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var result = service.ShowAsync(new InteractionRequest
                    {
                        Form = new InteractionForm { Content = new InteractionElement { Id = "message", Kind = InteractionElementKind.Text, Label = "Hello" } }
                    }, cancellation.Token);
                    await Dispatcher.UIThread.InvokeAsync(() => Assert.IsType<InteractionFormView>(host.Content));
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
                    Assert.Same(previous, host.Content);
                }
                finally
                {
                    window.Close();
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10), token);
    }
}