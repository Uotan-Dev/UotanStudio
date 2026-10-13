using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewEdlStreamingNandHandlerTests(NodeEditorUiFixture fixture)
{
    private const string MarkBadRefusal = "Marking a NAND block bad requires explicit confirmation.";
    private const string UnmarkBadRefusal = "Un-marking a NAND block bad requires explicit confirmation.";
    private const string CodewordRefusal =
        "Pulsing the streaming NAND controller for a codeword read requires explicit confirmation.";
    private const string FlashModeRefusal = "Entering streaming flash mode requires explicit confirmation.";
    private const string PartitionTableRefusal =
        "Writing the streaming partition table requires explicit confirmation.";
    private const string TrustedModeRefusal = "Streaming secure-mode negotiation is trusted-mode only";
    private const string LayoutRejection =
        "Enter a block index of zero or more, a scan count of at least one, and non-negative layout values.";
    private const string TableMissingRejection = "Choose the partition table file first.";
    private const string EndpointAbsence = "The selected device endpoint is no longer present.";

    private static readonly string[] ReadOnlyButtonNames = ["EdlNandRegistersButton", "EdlNandReadMapButton"];
    private static readonly string[] ModeButtonNames = ["EdlNandFlashModeButton", "EdlNandSecureModeButton"];

    [Theory]
    [InlineData("EdlNandMarkBadButton", MarkBadRefusal)]
    [InlineData("EdlNandUnmarkBadButton", UnmarkBadRefusal)]
    [InlineData("EdlNandCodewordButton", CodewordRefusal)]
    [InlineData("EdlNandFlashModeButton", FlashModeRefusal)]
    [InlineData("EdlNandSecureModeButton", TrustedModeRefusal)]
    public async Task EdlNandWriteButton_UncheckedConfirmation_LogsTheRefusalWithoutOpeningASession(
        string buttonName, string refusal)
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            ArrangeStreamingLayout(view, model, confirmed: false, trusted: false);
            var button = view.FindControl<Button>(buttonName)!;
            var cancel = view.FindControl<Button>("CancelFlashButton")!;
            Assert.True(button.IsEnabled);
            Assert.False(cancel.IsEnabled);
            Assert.False(Check(view, "EdlNandConfirmBox").IsChecked == true);
            Assert.False(Check(view, "EdlNandTrustedBox").IsChecked == true);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForLogAsync(log, refusal, token);

            var text = PeekLog(log);
            Assert.Contains(refusal, text, StringComparison.Ordinal);
            Assert.DoesNotContain(EndpointAbsence, text);
            Assert.DoesNotContain(LayoutRejection, text);
            Assert.False(log.IsBusy);
            Assert.False(cancel.IsEnabled);
        });
    }

    [Theory]
    [InlineData("EdlNandBaseBox", "abc")]
    [InlineData("EdlNandBaseBox", "-1")]
    [InlineData("EdlNandPagesBox", "1.5")]
    [InlineData("EdlNandPagesBox", "-4")]
    [InlineData("EdlNandSectorsBox", "99999999999999999999999")]
    [InlineData("EdlNandBadLenBox", " ")]
    [InlineData("EdlNandBlockBox", "-1")]
    [InlineData("EdlNandBlockCountBox", "0")]
    [InlineData("EdlNandBlockCountBox", "abc")]
    public async Task EdlNandMarkBadButton_LayoutInputThatIsNotAnUnsignedInteger_LogsTheParseRejection(
        string boxName, string value)
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            ArrangeStreamingLayout(view, model, confirmed: true, trusted: true);
            var cancel = view.FindControl<Button>("CancelFlashButton")!;
            Box(view, boxName).Text = value;

            view.FindControl<Button>("EdlNandMarkBadButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForLogAsync(log, LayoutRejection, token);

            var text = PeekLog(log);
            Assert.Contains(LayoutRejection, text, StringComparison.Ordinal);
            Assert.DoesNotContain(MarkBadRefusal, text);
            Assert.DoesNotContain(EndpointAbsence, text);
            Assert.False(log.IsBusy);
            Assert.False(cancel.IsEnabled);
        });
    }

    [Theory]
    [InlineData("EdlNandBaseBox", "abc")]
    [InlineData("EdlNandPagesBox", "-1")]
    [InlineData("EdlNandSectorsBox", "1.5")]
    [InlineData("EdlNandBadLenBox", "99999999999999999999999")]
    public async Task EdlNandReadOnlyButton_LayoutThatCannotBeParsed_LogsTheParseRejectionWithoutContactingTheTarget(
        string boxName, string value)
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            ArrangeStreamingLayout(view, model, confirmed: true, trusted: true);
            Box(view, boxName).Text = value;

            foreach (var name in ReadOnlyButtonNames)
            {
                Assert.True(view.FindControl<Button>(name)!.IsEnabled);
                view.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            await WaitForLogAsync(log, LayoutRejection, token);

            var text = PeekLog(log);
            Assert.Contains(LayoutRejection, text, StringComparison.Ordinal);
            Assert.DoesNotContain(EndpointAbsence, text);
            Assert.DoesNotContain(CodewordRefusal, text);
            Assert.False(log.IsBusy);
        });
    }

    [Theory]
    [InlineData(EdlProtocolState.Sahara, true)]
    [InlineData(EdlProtocolState.Firehose, true)]
    [InlineData(EdlProtocolState.Unknown, true)]
    [InlineData(EdlProtocolState.Streaming, false)]
    public async Task EdlNandModeButton_SessionOutsideStreamingDload_AppendsNothingAndKeepsCancelButtonDisabled(
        EdlProtocolState stage, bool selected)
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            if (selected)
            {
                SelectEdl(model, stage);
            }
            else
            {
                model.SelectedDevice = null;
                Dispatcher.UIThread.RunJobs();
            }

            var group = view.FindControl<StackPanel>("EdlStreamingNandGroup")!;
            var cancel = view.FindControl<Button>("CancelFlashButton")!;
            Assert.False(group.IsVisible);
            var before = PeekLog(log);

            foreach (var name in ModeButtonNames)
            {
                var button = view.FindControl<Button>(name)!;
                Assert.False(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            await SettleAsync(token);

            Assert.Equal(before, PeekLog(log));
            Assert.False(log.IsBusy);
            Assert.False(cancel.IsEnabled);
        });
    }

    [Fact]
    public async Task EdlNandRegistersButton_ConfirmedReadOnlyReadOnAnUnavailableEndpoint_AppendsTheEndpointError()
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            ArrangeStreamingLayout(view, model, confirmed: true, trusted: true);
            var button = view.FindControl<Button>("EdlNandRegistersButton")!;
            var cancel = view.FindControl<Button>("CancelFlashButton")!;
            Assert.True(button.IsEnabled);
            Assert.True(Check(view, "EdlNandConfirmBox").IsChecked == true);
            Assert.False(PeekLog(log).Contains(EndpointAbsence, StringComparison.Ordinal));

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForLogAsync(log, EndpointAbsence, token);

            var text = PeekLog(log);
            Assert.Contains(EndpointAbsence, text, StringComparison.Ordinal);
            Assert.DoesNotContain(MarkBadRefusal, text);
            Assert.DoesNotContain(UnmarkBadRefusal, text);
            Assert.DoesNotContain(CodewordRefusal, text);
            Assert.DoesNotContain(FlashModeRefusal, text);
            Assert.DoesNotContain(TrustedModeRefusal, text);
            Assert.DoesNotContain(PartitionTableRefusal, text);
            Assert.DoesNotContain(LayoutRejection, text);
            Dispatcher.UIThread.RunJobs();
            Assert.False(log.IsBusy);
            Assert.False(cancel.IsEnabled);
        });
    }

    [Fact]
    public async Task EdlNandSendTableButton_NoChosenTableFile_StaysDisabledAndAppendsTheChooseHint()
    {
        var token = TestContext.Current.CancellationToken;
        await WithFlashViewAsync(async (view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            ArrangeStreamingLayout(view, model, confirmed: true, trusted: true);
            var send = view.FindControl<Button>("EdlNandSendTableButton")!;
            var markBad = view.FindControl<Button>("EdlNandMarkBadButton")!;
            var cancel = view.FindControl<Button>("CancelFlashButton")!;
            Assert.True(string.IsNullOrEmpty(view.FindControl<TextBlock>("EdlNandTableText")!.Text));
            Assert.False(send.IsEnabled);
            Assert.True(markBad.IsEnabled);

            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForLogAsync(log, TableMissingRejection, token);

            var text = PeekLog(log);
            Assert.Contains(TableMissingRejection, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PartitionTableRefusal, text);
            Assert.DoesNotContain(EndpointAbsence, text);
            Assert.False(log.IsBusy);
            Assert.False(cancel.IsEnabled);
        });
    }

    [Theory]
    [InlineData(true, EdlProtocolState.Streaming, true)]
    [InlineData(true, EdlProtocolState.Firehose, false)]
    [InlineData(true, EdlProtocolState.Sahara, false)]
    [InlineData(false, EdlProtocolState.Streaming, false)]
    public async Task EdlStreamingNandGroup_ExpertSwitchAndObservedStage_ShowTheGroupOnlyOnStreamingDload(
        bool expert, EdlProtocolState stage, bool expected)
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = expert;
            SelectEdl(model, stage);
            Assert.Equal(expected, view.FindControl<StackPanel>("EdlStreamingNandGroup")!.IsVisible);
            return Task.CompletedTask;
        });
    }

    private static void ArrangeStreamingLayout(
        FlashView view, DeviceViewModel model, bool confirmed, bool trusted)
    {
        view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
        SelectEdl(model, EdlProtocolState.Streaming);
        Assert.True(view.FindControl<StackPanel>("EdlStreamingNandGroup")!.IsVisible);
        Box(view, "EdlNandPartitionBox").Text = "userdata";
        Box(view, "EdlNandBlockBox").Text = "0";
        Box(view, "EdlNandBlockCountBox").Text = "8";
        Box(view, "EdlNandBaseBox").Text = "4096";
        Box(view, "EdlNandPagesBox").Text = "64";
        Box(view, "EdlNandSectorsBox").Text = "8";
        Box(view, "EdlNandBadLenBox").Text = "4";
        Check(view, "EdlNandBadInSpareBox").IsChecked = false;
        Check(view, "EdlNandOverrideBox").IsChecked = false;
        Check(view, "EdlNandConfirmBox").IsChecked = confirmed;
        Check(view, "EdlNandTrustedBox").IsChecked = trusted;
        Assert.True(Box(view, "EdlNandBaseBox").IsEnabled);
        Assert.True(Box(view, "EdlNandBlockCountBox").IsEnabled);
        Assert.True(Check(view, "EdlNandConfirmBox").IsEnabled);
        Dispatcher.UIThread.RunJobs();
    }

    private static void SelectEdl(DeviceViewModel model, EdlProtocolState stage)
    {
        model.SelectedDevice = new DeviceInfo(new DetectedDevice
        {
            Key = $"edl-{stage}-{model.SelectedDevice?.Device.Key?.Length ?? 0}",
            Serial = "streaming-nand",
            Protocol = DeviceProtocolKind.Edl,
            Transport = DeviceTransportKind.Usb,
            State = "edl",
            EdlProtocol = stage
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox Box(FlashView view, string name) => view.FindControl<TextBox>(name)!;

    private static CheckBox Check(FlashView view, string name) => view.FindControl<CheckBox>(name)!;

    private static string PeekLog(FlashViewModel model)
    {
        model.FlushPendingLog();
        return model.LogText;
    }

    private static async Task SettleAsync(CancellationToken token)
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50, token);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForLogAsync(FlashViewModel model, string expected, CancellationToken token)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25, token);
            if (PeekLog(model).Contains(expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Contains(expected, PeekLog(model), StringComparison.Ordinal);
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