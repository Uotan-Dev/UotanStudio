using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class SplashWindowTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData("zh-CN", "正在创建工作区布局...")]
    [InlineData("en-US", "Creating workspace layout...")]
    public async Task AdvanceAsync_WorkspaceStage_PreservesOriginalLayoutAndUpdatesText(string language, string status)
    {
        await DispatchAsync(async () =>
        {
            var previousLanguage = LanguageService.Instance.CurrentLanguage;
            LanguageService.Instance.ApplyLanguage(language);
            try
            {
                var model = new SplashViewModel("initial");
                var splash = new MainSplashWindow { DataContext = model };
                await model.AdvanceAsync(SplashPhase.Workspace);

                var layout = Assert.IsType<DockPanel>(splash.Content);
                var footer = Assert.IsType<StackPanel>(layout.Children[0]);
                Assert.Equal(2, footer.Children.Count);
                var text = Assert.IsType<TextBlock>(footer.Children[0]);
                Assert.Equal(status, text.Text);
                Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, text.HorizontalAlignment);
                var track = Assert.IsType<Grid>(footer.Children[1]);
                var busy = Assert.IsType<ProgressBar>(track.Children[0]);
                var completed = Assert.IsType<ProgressBar>(track.Children[1]);
                Assert.True(busy.IsIndeterminate);
                Assert.True(busy.IsVisible);
                Assert.False(completed.IsVisible);
                Assert.False(completed.IsIndeterminate);
                Assert.Equal(completed.Maximum, completed.Value);
                Assert.True(model.IsInitializing);
                await model.AdvanceAsync(SplashPhase.Ready);
                Assert.False(busy.IsVisible);
                Assert.True(completed.IsVisible);
                Assert.Equal(completed.Maximum, completed.Value);
            }
            finally
            {
                LanguageService.Instance.ApplyLanguage(previousLanguage);
            }
        });
    }

    [Fact]
    public async Task AdvanceAsync_ReadyTextPublished_CompletedBarIsAlreadyVisibleAndFull()
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            var splash = new MainSplashWindow { DataContext = model };
            var busy = splash.FindControl<ProgressBar>("BusyProgress")!;
            var completed = splash.FindControl<ProgressBar>("CompletedProgress")!;
            var observedReady = false;
            model.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SplashViewModel.StatusText) && !model.IsInitializing)
                {
                    observedReady = true;
                    Assert.False(busy.IsVisible);
                    Assert.True(completed.IsVisible);
                    Assert.False(completed.IsIndeterminate);
                    Assert.Equal(completed.Maximum, completed.Value);
                }
            };

            await model.AdvanceAsync(SplashPhase.Ready);

            Assert.True(observedReady);
            await splash.PresentCompletionAsync();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvanceAsync_Ready_SemiThemeIndicatorFillsEntireTrack(bool dark)
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            var splash = new MainSplashWindow { DataContext = model };
            var content = splash.Content;
            var completed = splash.FindControl<ProgressBar>("CompletedProgress")!;
            splash.Content = null;
            var host = new Window
            {
                Content = content,
                DataContext = model,
                Width = 480,
                Height = 300,
                RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light
            };
            host.Styles.Add(new Semi.Avalonia.SemiTheme());
            try
            {
                host.Show();
                await model.AdvanceAsync(SplashPhase.Ready);
                Dispatcher.UIThread.RunJobs();
                host.UpdateLayout();
                var indicator = Assert.Single(completed.GetVisualDescendants().OfType<Control>(),
                    control => control.Name == "PART_Indicator");
                Assert.True(completed.IsEffectivelyVisible);
                Assert.True(completed.Bounds.Width >= 320);
                Assert.InRange(indicator.Bounds.Width, completed.Bounds.Width - 2, completed.Bounds.Width);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public async Task AdvanceAsync_AllStages_ProgressCompletesOnlyWhenReady()
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            foreach (var phase in Enum.GetValues<SplashPhase>())
            {
                await model.AdvanceAsync(phase);
                Assert.Equal(phase != SplashPhase.Ready, model.IsInitializing);
            }
            Assert.False(model.IsInitializing);
        });
    }

    [Fact]
    public async Task AdvanceAsync_NextSynchronousStage_AllowsRenderPriorityWorkFirst()
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            string? observed = null;
            Dispatcher.UIThread.Post(() => observed = model.StatusText, DispatcherPriority.Render);

            await model.AdvanceAsync(SplashPhase.Window);

            Assert.Equal(LanguageService.Instance.GetString("Splash_Preparing"), observed);
            Assert.True(model.IsInitializing);
        });
    }

    [Fact]
    public async Task AdvanceAsync_BackwardsStage_RejectsWithoutChangingProgress()
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            await model.AdvanceAsync(SplashPhase.Window);
            var status = model.StatusText;
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.AdvanceAsync(SplashPhase.Scanning));
            Assert.Equal(status, model.StatusText);
        });
    }

    [Fact]
    public async Task CreateAsync_StageCallbackFails_PropagatesFailureWithoutCompletingProgress()
    {
        await DispatchAsync(async () =>
        {
            var model = new SplashViewModel("initial");
            await Assert.ThrowsAsync<IOException>(() => ShellComposition.CreateAsync(async phase =>
            {
                await model.AdvanceAsync(phase);
                throw new IOException("Fixture failure before shell allocation.");
            }));
            Assert.True(model.IsInitializing);
        });
    }

    private async Task DispatchAsync(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(action, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}