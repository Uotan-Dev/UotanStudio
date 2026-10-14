using Avalonia.Controls;
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
public sealed class FlashViewStageGatingTests(NodeEditorUiFixture fixture)
{

    [Fact]
    public async Task EdlStageText_ReportsObservedProtocolStage()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var stage = view.FindControl<TextBlock>("EdlStageText")!;
            var language = LanguageService.Instance;

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Assert.Equal(language.GetString("Flash_EdlStageFirehose"), stage.Text);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Sahara);
            Assert.Equal(language.GetString("Flash_EdlStageSahara"), stage.Text);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Streaming);
            Assert.Equal(language.GetString("Flash_EdlStageStreaming"), stage.Text);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Unknown);
            Assert.Equal(language.GetString("Flash_EdlStageUnknown"), stage.Text);
        });
    }

    [Fact]
    public async Task MtkExpertGroups_AppearsOnlyWithExpertSwitch()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var legacy = view.FindControl<StackPanel>("MtkLegacyParamsGroup")!;
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            var family = view.FindControl<ComboBox>("MtkProtocolBox")!;

            expert.IsChecked = true;
            family.SelectedIndex = 1;
            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "da", EdlProtocolState.Unknown);
            Dispatcher.UIThread.RunJobs();
            Assert.True(legacy.IsVisible);

            expert.IsChecked = false;
            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "da", EdlProtocolState.Unknown);
            Dispatcher.UIThread.RunJobs();
            Assert.False(legacy.IsVisible);
        });
    }

    [Fact]
    public async Task EdlAllowStreamDloadBox_HidesOnceTheLinkLeftSaharaCommandMode()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var box = view.FindControl<CheckBox>("EdlAllowStreamDloadBox")!;

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Unknown);
            Assert.True(box.IsVisible);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Sahara);
            Assert.True(box.IsVisible);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Assert.False(box.IsVisible);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Streaming);
            Assert.False(box.IsVisible);
        });
    }

    [Fact]
    public async Task MtkHeader_NeverOutlivesTheMtkPane()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var header = view.FindControl<TextBlock>("MtkHeader")!;
            var pane = view.FindControl<StackPanel>("MtkPane")!;

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Sahara);
            Assert.False(pane.IsVisible);
            Assert.Equal(pane.IsVisible, header.IsVisible);

            Select(model, DeviceProtocolKind.Fastboot, DeviceTransportKind.Usb, "fastboot", EdlProtocolState.Unknown);
            Assert.Equal(pane.IsVisible, header.IsVisible);

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "brom", EdlProtocolState.Unknown);
            Assert.True(pane.IsVisible);
            Assert.True(header.IsVisible);
        });
    }

    [Fact]
    public async Task MtkUsbOnlyRow_FollowsTheBootStageInsteadOfTheProtocolKind()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var row = view.FindControl<StackPanel>("MtkUsbOnlyRow")!;

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "brom", EdlProtocolState.Unknown);
            Assert.True(row.IsVisible);

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "preloader", EdlProtocolState.Unknown);
            Assert.True(row.IsVisible);

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "da", EdlProtocolState.Unknown);
            Assert.False(row.IsVisible);

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "logging", EdlProtocolState.Unknown);
            Assert.False(row.IsVisible);
        });
    }

    [Fact]
    public async Task MtkStageDeclaration_OpensThePreloaderPaneWhenDetectionStayedSilent()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var pane = view.FindControl<StackPanel>("MtkPreloaderPane")!;
            var stage = view.FindControl<ComboBox>("MtkStageBox")!;
            var peek = view.FindControl<Button>("MtkPreloaderPeekButton")!;

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, string.Empty, EdlProtocolState.Unknown);
            Assert.False(pane.IsVisible);
            Assert.False(peek.IsEnabled);

            stage.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.True(pane.IsVisible);
            Assert.True(peek.IsEnabled);

            stage.SelectedIndex = 0;
            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "preloader", EdlProtocolState.Unknown);
            Assert.True(pane.IsVisible);
        });
    }

    [Fact]
    public async Task MtkDaActions_StayDisabledOnTheLoggingStage()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var reboot = view.FindControl<Button>("MtkRebootButton")!;
            var powerOff = view.FindControl<Button>("MtkPowerOffButton")!;
            var fullDump = view.FindControl<Button>("MtkFullDumpButton")!;

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "logging", EdlProtocolState.Unknown);
            Assert.False(reboot.IsEnabled);
            Assert.False(powerOff.IsEnabled);
            Assert.False(fullDump.IsEnabled);

            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "da", EdlProtocolState.Unknown);
            Assert.True(reboot.IsEnabled);
            Assert.True(powerOff.IsEnabled);
            Assert.True(fullDump.IsEnabled);
        });
    }

    [Fact]
    public async Task EdlFactoryResetButton_StaysDisabledUntilTheFirehoseStageIsReported()
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            var button = view.FindControl<Button>("EdlFactoryResetButton")!;

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Sahara);
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled);

            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEnabled);
        });
    }

    [Fact]
    public async Task MtkLegacyParamsGroup_NeedsExpertSwitchAndTheLegacyFamily()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var group = view.FindControl<StackPanel>("MtkLegacyParamsGroup")!;
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            var family = view.FindControl<ComboBox>("MtkProtocolBox")!;

            expert.IsChecked = true;
            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "brom", EdlProtocolState.Unknown);
            family.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.False(group.IsVisible);

            family.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(group.IsVisible);

            expert.IsChecked = false;
            Select(model, DeviceProtocolKind.Mtk, DeviceTransportKind.Usb, "brom", EdlProtocolState.Unknown);
            family.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.False(group.IsVisible);
        });
    }

    private static void Select(
        DeviceViewModel model,
        DeviceProtocolKind protocol,
        DeviceTransportKind transport,
        string state,
        EdlProtocolState edlProtocol)
    {
        model.SelectedDevice = new DeviceInfo(new DetectedDevice
        {
            Key = $"{protocol}-{transport}-{state}-{model.SelectedDevice?.Device.Key?.Length ?? 0}",
            Serial = "stage-gating",
            Protocol = protocol,
            Transport = transport,
            State = state,
            EdlProtocol = edlProtocol
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static string PeekLog(FlashViewModel model)
    {
        model.FlushPendingLog();
        return model.LogText;
    }

    private static async Task WaitForLogAsync(FlashViewModel model, string expected)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
            if (PeekLog(model).Contains(expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Contains(expected, PeekLog(model), StringComparison.Ordinal);
    }

    private async Task WithFlashViewAsync(Action<FlashView, DeviceViewModel> body)
        => await WithFlashViewAsync((view, model) =>
        {
            body(view, model);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task TryReadEdlBundleTuning_ExpertOverrides_ProduceChunkAndPagesValues()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            var chunk = view.FindControl<TextBox>("EdlMaxPayloadBox")!;
            var pages = view.FindControl<TextBox>("EdlPagesPerBlockBox")!;
            expert.IsChecked = true;
            chunk.Text = "2097152";
            pages.Text = "64";

            Assert.True(view.TryReadEdlBundleTuning(out var tuning));
            Assert.Equal(2097152L, tuning.MaxProgramChunkBytes);
            Assert.Equal(64L, tuning.NandPagesPerBlock);

            pages.Text = "0";
            Assert.True(view.TryReadEdlBundleTuning(out var zeroPages));
            Assert.Null(zeroPages.NandPagesPerBlock);

            pages.Text = string.Empty;
            Assert.True(view.TryReadEdlBundleTuning(out var blankPages));
            Assert.Null(blankPages.NandPagesPerBlock);

            chunk.Text = string.Empty;
            Assert.True(view.TryReadEdlBundleTuning(out var blankChunk));
            Assert.Null(blankChunk.MaxProgramChunkBytes);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TryReadEdlBundleTuning_ExpertSwitchOff_IgnoresLeftoverBoxValues()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            var chunk = view.FindControl<TextBox>("EdlMaxPayloadBox")!;
            var pages = view.FindControl<TextBox>("EdlPagesPerBlockBox")!;
            expert.IsChecked = false;
            chunk.Text = "1000";
            pages.Text = "65536";

            Assert.True(view.TryReadEdlBundleTuning(out var tuning));
            Assert.Null(tuning.MaxProgramChunkBytes);
            Assert.Null(tuning.NandPagesPerBlock);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("EdlMaxPayloadBox", "1000")]
    [InlineData("EdlMaxPayloadBox", "511")]
    [InlineData("EdlMaxPayloadBox", "16777728")]
    [InlineData("EdlMaxPayloadBox", "not-a-number")]
    [InlineData("EdlPagesPerBlockBox", "65536")]
    [InlineData("EdlPagesPerBlockBox", "-1")]
    public async Task TryReadEdlBundleTuning_OverrideOutsideAcceptedWindow_RejectsBeforeFlashing(
        string boxName, string value)
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            var box = view.FindControl<TextBox>(boxName)!;
            box.Text = value;

            Assert.False(view.TryReadEdlBundleTuning(out _));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EdlSkipResponseBox_AppearsOnlyWithExpertSwitchAndBeforeTheLoaderIsUp()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var box = view.FindControl<CheckBox>("EdlSkipResponseBox")!;
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            expert.IsChecked = false;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Assert.False(box.IsVisible);

            expert.IsChecked = true;
            foreach (var (stage, expected) in new[]
            {
                (EdlProtocolState.Sahara, true),
                (EdlProtocolState.Unknown, true),
                (EdlProtocolState.Firehose, false),
                (EdlProtocolState.Streaming, false)
            })
            {
                Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", stage);
                Assert.Equal(expected, box.IsVisible);
            }

            return Task.CompletedTask;
        });
    }

    private async Task WithFlashViewAsync(Func<FlashView, DeviceViewModel, Task> body)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
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
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}