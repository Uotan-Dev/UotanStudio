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
public sealed class FlashViewEdlGptEntryOverridesTests(NodeEditorUiFixture fixture)
{
    private const string LbaLabel = "Primary partition entry array LBA";
    private const string CountLabel = "Partition entry count";
    private const string SizeLabel = "Partition entry size";
    private const string LbaRejection =
        "The primary partition entry array LBA must be 0 or a whole number of at least 2.";
    private const string CountRejection =
        "The partition entry count must be 0 to keep the header value, or a whole number up to 65536.";
    private const string SizeRejection =
        "The partition entry size must be 0 to keep the header value, or a power of two from 128 through 4096.";

    [Fact]
    public async Task EdlGptEntryBoxes_ExpertSwitchOff_HideTheRowsAndIgnoreTheLeftoverValues()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var expert = view.FindControl<CheckBox>("FlashExpertModeBox")!;
            expert.IsChecked = false;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Assert.Equal("0", Box(view, "EdlGptEntryLbaBox").Text);
            Assert.Equal("0", Box(view, "EdlGptEntryCountBox").Text);
            Assert.Equal("0", Box(view, "EdlGptEntrySizeBox").Text);
            foreach (var name in new[]
            {
                "EdlGptEntryLbaLabel", "EdlGptEntryLbaBox", "EdlGptEntryCountLabel", "EdlGptEntryCountBox",
                "EdlGptEntrySizeLabel", "EdlGptEntrySizeBox"
            })
            {
                Assert.False(view.FindControl<Control>(name)!.IsVisible);
            }

            Box(view, "EdlGptEntryLbaBox").Text = "60";
            Box(view, "EdlGptEntryCountBox").Text = "128";
            Box(view, "EdlGptEntrySizeBox").Text = "256";
            Assert.True(view.TryReadEdlGptEntryOverrides(out var lba, out var count, out var size));
            Assert.Null(lba);
            Assert.Null(count);
            Assert.Null(size);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EdlGptEntryBoxes_ExpertSwitchOn_AppearWithTheirLabelsOnTheEdlPane()
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            foreach (var (labelName, boxName, expected) in new[]
            {
                ("EdlGptEntryLbaLabel", "EdlGptEntryLbaBox", LbaLabel),
                ("EdlGptEntryCountLabel", "EdlGptEntryCountBox", CountLabel),
                ("EdlGptEntrySizeLabel", "EdlGptEntrySizeBox", SizeLabel)
            })
            {
                Assert.True(view.FindControl<TextBlock>(labelName)!.IsVisible);
                Assert.True(Box(view, boxName).IsVisible);
                Assert.Equal(expected, view.FindControl<TextBlock>(labelName)!.Text);
            }

            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryReadEdlGptEntryOverrides_BlankOrZeroInput_KeepsTheHeaderDimensions(string value)
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Box(view, "EdlGptEntryLbaBox").Text = value;
            Box(view, "EdlGptEntryCountBox").Text = value;
            Box(view, "EdlGptEntrySizeBox").Text = value;

            Assert.True(view.TryReadEdlGptEntryOverrides(out var lba, out var count, out var size));
            Assert.Null(lba);
            Assert.Null(count);
            Assert.Null(size);
            var log = PeekLog((FlashViewModel)view.DataContext!);
            Assert.DoesNotContain(LbaRejection, log, StringComparison.Ordinal);
            Assert.DoesNotContain(CountRejection, log, StringComparison.Ordinal);
            Assert.DoesNotContain(SizeRejection, log, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TryReadEdlGptEntryOverrides_AllThreeDeclaredValues_ProduceTheOverrides()
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Box(view, "EdlGptEntryLbaBox").Text = "60";
            Box(view, "EdlGptEntryCountBox").Text = "64";
            Box(view, "EdlGptEntrySizeBox").Text = "256";

            Assert.True(view.TryReadEdlGptEntryOverrides(out var lba, out var count, out var size));
            Assert.Equal(60UL, lba);
            Assert.Equal(64u, count);
            Assert.Equal(256u, size);

            Box(view, "EdlGptEntryLbaBox").Text = "2";
            Box(view, "EdlGptEntryCountBox").Text = "1";
            Box(view, "EdlGptEntrySizeBox").Text = "128";
            Assert.True(view.TryReadEdlGptEntryOverrides(out var lowestLba, out var lowestCount, out var lowestSize));
            Assert.Equal(2UL, lowestLba);
            Assert.Equal(1u, lowestCount);
            Assert.Equal(128u, lowestSize);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("99999999999999999999999")]
    public async Task TryReadEdlGptEntryOverrides_EntryArrayLocationThatCannotBeRead_LogsTheLocationRejection(
        string value)
    {
        await WithFlashViewAsync((view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            EnableExpertAndSelectEdl(view, model);
            Box(view, "EdlGptEntryLbaBox").Text = value;

            Assert.False(view.TryReadEdlGptEntryOverrides(out _, out _, out _));
            Assert.Contains(LbaRejection, PeekLog(log), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("65537")]
    [InlineData("99999999999999999999999")]
    public async Task TryReadEdlGptEntryOverrides_EntryCountBeyondTheReaderLimit_LogsTheCountRejection(string value)
    {
        await WithFlashViewAsync((view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            EnableExpertAndSelectEdl(view, model);
            Box(view, "EdlGptEntryCountBox").Text = value;

            Assert.False(view.TryReadEdlGptEntryOverrides(out _, out _, out _));
            Assert.Contains(CountRejection, PeekLog(log), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("127")]
    [InlineData("129")]
    [InlineData("384")]
    [InlineData("4097")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task TryReadEdlGptEntryOverrides_EntrySizeThatIsNotAPowerOfTwoInRange_LogsTheSizeRejection(
        string value)
    {
        await WithFlashViewAsync((view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            EnableExpertAndSelectEdl(view, model);
            Box(view, "EdlGptEntrySizeBox").Text = value;

            Assert.False(view.TryReadEdlGptEntryOverrides(out _, out _, out _));
            Assert.Contains(SizeRejection, PeekLog(log), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TryReadEdlGptEntryOverrides_ExpertSwitchOffWithGarbageInBoxes_StillDefersToTheHeaderValues()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var log = (FlashViewModel)view.DataContext!;
            view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = false;
            Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
            Box(view, "EdlGptEntryLbaBox").Text = "99999999999999999999999";
            Box(view, "EdlGptEntryCountBox").Text = "abc";
            Box(view, "EdlGptEntrySizeBox").Text = "384";

            Assert.True(view.TryReadEdlGptEntryOverrides(out var lba, out var count, out var size));
            Assert.Null(lba);
            Assert.Null(count);
            Assert.Null(size);
            var text = PeekLog(log);
            Assert.DoesNotContain(LbaRejection, text, StringComparison.Ordinal);
            Assert.DoesNotContain(CountRejection, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SizeRejection, text, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    private static void EnableExpertAndSelectEdl(FlashView view, DeviceViewModel model)
    {
        view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = true;
        Select(model, DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "edl", EdlProtocolState.Firehose);
        Box(view, "EdlGptEntryLbaBox").Text = "0";
        Box(view, "EdlGptEntryCountBox").Text = "0";
        Box(view, "EdlGptEntrySizeBox").Text = "0";
    }

    private static TextBox Box(FlashView view, string name) => view.FindControl<TextBox>(name)!;

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
            Serial = "gpt-entry-overrides",
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