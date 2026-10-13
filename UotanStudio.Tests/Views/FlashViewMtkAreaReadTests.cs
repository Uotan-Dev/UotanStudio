using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewMtkAreaReadTests(NodeEditorUiFixture fixture)
{
    private const string AreaRejection =
        "Enter an absolute offset of zero or more and a byte length of at least one.";

    [Theory]
    [InlineData("abc", "4096")]
    [InlineData("-1", "4096")]
    [InlineData("0", "0")]
    [InlineData("0", "-1")]
    [InlineData("0", "abc")]
    [InlineData("", "4096")]
    public async Task OnMtkReadAreaClick_RangeThatIsNotAnAbsoluteWindow_LogsTheRejectionWithoutOpeningPicker(
        string offset, string length)
    {
        await WithFlashViewAsync((view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            SelectMtkDa(model);
            var offsetBox = view.FindControl<TextBox>("MtkAreaOffsetBox")!;
            var lengthBox = view.FindControl<TextBox>("MtkAreaLengthBox")!;
            var button = view.FindControl<Button>("MtkReadAreaButton")!;
            Assert.Equal("0", offsetBox.Text);
            Assert.Equal("4096", lengthBox.Text);
            Assert.True(offsetBox.IsEnabled);
            Assert.True(lengthBox.IsEnabled);
            Assert.True(button.IsEnabled);
            offsetBox.Text = offset;
            lengthBox.Text = length;

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(AreaRejection, PeekLog(log), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MtkAreaReadControls_AreHiddenWhileExpertModeIsOff()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var group = view.FindControl<StackPanel>("MtkRawReadGroup")!;
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            expert.IsChecked = true;
            SelectMtkDa(model);
            Assert.True(group.IsVisible);
            Assert.True(view.FindControl<TextBox>("MtkAreaOffsetBox")!.IsEnabled);
            Assert.True(view.FindControl<Button>("MtkReadAreaButton")!.IsEnabled);

            expert.IsChecked = false;
            expert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(group.IsVisible);
            return Task.CompletedTask;
        });
    }

    private static void SelectMtkDa(DeviceViewModel model)
    {
        model.SelectedDevice = new DeviceInfo(new DetectedDevice
        {
            Key = $"mtk-da-{model.SelectedDevice?.Device.Key?.Length ?? 0}",
            Serial = "area-read",
            Protocol = DeviceProtocolKind.Mtk,
            Transport = DeviceTransportKind.Usb,
            State = "da"
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static string PeekLog(FlashViewModel model)
    {
        model.FlushPendingLog();
        return model.LogText;
    }

    private async Task WithFlashViewAsync(Func<FlashView, DeviceViewModel, Task> body)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage("en-US");
                var model = DeviceViewModel.EnsureInstance();
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    return body(view, model);
                }
                finally
                {
                    model.SelectedDevice = null;
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}