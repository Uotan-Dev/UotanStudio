using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class MainViewStartupTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Constructor_DefaultWorkspace_DoesNotAllocateInactivePages()
    {
        await fixture.Session.Dispatch(() =>
        {
            var created = 0;
            var view = new MainView(_ =>
            {
                created++;
                return new TextBox();
            });
            Assert.Equal(4, view.FindControl<TabbedPage>("MainTabs")!.SelectedIndex);
            Assert.Equal(0, created);
            foreach (var name in new[] { "FlashTab", "DeliveryTab", "NodeEditorTab", "SettingsTab" })
            {
                Assert.Null(view.FindControl<ContentPage>(name)!.Content);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(3, "FlashTab")]
    [InlineData(5, "DeliveryTab")]
    [InlineData(6, "NodeEditorTab")]
    [InlineData(7, "SettingsTab")]
    public async Task SelectPage_Revisited_CreatesOnceAndRetainsContent(int index, string name)
    {
        await fixture.Session.Dispatch(() =>
        {
            List<int> created = [];
            var view = new MainView(selected =>
            {
                created.Add(selected);
                return new TextBox { Text = "initial" };
            });
            var tabs = view.FindControl<TabbedPage>("MainTabs")!;
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
            var content = Assert.IsType<TextBox>(view.FindControl<ContentPage>(name)!.Content);
            content.Text = "unsaved state";
            tabs.SelectedIndex = 4;
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([index], created);
            Assert.Same(content, view.FindControl<ContentPage>(name)!.Content);
            Assert.Equal("unsaved state", content.Text);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NavigateMessage_AttachedWindow_CreatesAndReusesRequestedPage()
    {
        await fixture.Session.Dispatch(() =>
        {
            List<int> created = [];
            var view = new MainView(index =>
            {
                created.Add(index);
                return new TextBox();
            });
            var window = new Window { Content = view, Width = 960, Height = 720 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(created);
                WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
                Dispatcher.UIThread.RunJobs();
                var page = view.FindControl<ContentPage>("NodeEditorTab")!;
                var content = Assert.IsType<TextBox>(page.Content);
                WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
                Dispatcher.UIThread.RunJobs();
                WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
                Dispatcher.UIThread.RunJobs();
                Assert.Same(content, page.Content);
                Assert.Equal([6], created);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelectPage_FailedCreation_CanRetryWithoutCachingFailure()
    {
        await fixture.Session.Dispatch(() =>
        {
            var attempts = 0;
            var view = new MainView(_ =>
            {
                if (++attempts == 1)
                {
                    throw new IOException("Fixture page construction failed.");
                }
                return new TextBox();
            });
            var tabs = view.FindControl<TabbedPage>("MainTabs")!;
            Assert.Throws<IOException>(() => tabs.SelectedIndex = 3);
            Assert.Null(view.FindControl<ContentPage>("FlashTab")!.Content);
            tabs.SelectedIndex = 4;
            tabs.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<TextBox>(view.FindControl<ContentPage>("FlashTab")!.Content);
            Assert.Equal(2, attempts);
        }, TestContext.Current.CancellationToken);
    }
}