using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Comm.Protocols.Edl.Auth;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware.Architecture;
using UotanStudio.Tests.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewHeadlessTests(NodeEditorUiFixture fixture)
{
    private static GptPartition Partition(int index, string name, ulong firstLba, ulong lastLba)
        => new(index, Guid.Empty, Guid.Empty, firstLba, lastLba, 0, name, 0, 0);

    [Fact]
    public async Task PackagePathLostFocus_LocalImage_LoadsSelectablePartition()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    var pathBox = view.FindControl<TextBox>("PackagePathBox");
                    Assert.NotNull(pathBox);
                    Assert.False(view.FindControl<CheckBox>("ShowAllPartitionsBox")!.IsChecked == true);
                    model.PackagePath = image;
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (model.Partitions.Count == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);

                    var partition = Assert.Single(model.Partitions);
                    Assert.Equal("boot", partition.Name);
                    Assert.Equal(image, partition.FilePath);
                    Assert.True(partition.CanSelect);
                    partition.IsSelected = true;
                    Assert.Equal(1, model.SelectedCount);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagePathLostFocus_UppercaseSuperEmpty_UsesWipeStep()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "SUPER_EMPTY.img"),
                FlashSuperEmptyServiceTests.EmptyMetadata(), TestContext.Current.CancellationToken);
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.Session.Dispatch(() =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    model.PackagePath = root;
                    view.FindControl<TextBox>("PackagePathBox")!.RaiseEvent(
                        new RoutedEventArgs(InputElement.LostFocusEvent));

                    var entry = Assert.Single(model.Partitions);
                    Assert.Equal("wipe_super", entry.Step?.Command);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PackagePathLostFocus_MissingThenCreated_RetriesSamePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "boot.img");
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    var pathBox = view.FindControl<TextBox>("PackagePathBox")!;
                    model.PackagePath = image;
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (model.LogText.Length == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    Assert.NotEmpty(model.LogText);
                    Assert.Empty(model.Partitions);

                    await File.WriteAllBytesAsync(image, [1, 2, 3], TestContext.Current.CancellationToken);
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    until = DateTime.UtcNow.AddSeconds(5);
                    while (model.Partitions.Count == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    Assert.Equal("boot", Assert.Single(model.Partitions).Name);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Fastboot, false, false, false, true)]
    [InlineData(DeviceProtocolKind.Rkdl, false, false, true, true)]
    [InlineData(DeviceProtocolKind.Rkdl, false, false, false, false)]
    [InlineData(DeviceProtocolKind.Rkdl, true, false, false, false)]
    [InlineData(DeviceProtocolKind.Adb, true, false, false, true)]
    [InlineData(DeviceProtocolKind.Adb, false, false, false, false)]
    [InlineData(DeviceProtocolKind.Edl, true, true, false, true)]
    [InlineData(DeviceProtocolKind.Edl, true, false, false, false)]
    [InlineData(DeviceProtocolKind.Edl, false, true, false, false)]
    [InlineData(DeviceProtocolKind.Hdc, true, true, true, false)]
    public void FlashModeAllowed_ProtocolMatrix_MatchesTransportCapability(
        DeviceProtocolKind mode, bool flatBin, bool hasProgrammer,
        bool hasRockchipParameter, bool expected)
        => Assert.Equal(expected, FlashView.FlashModeAllowed(mode, flatBin, hasProgrammer, hasRockchipParameter));

    [Fact]
    public void FlashPartitionEntry_StateTransitions_RaiseIsRunningNotifications()
    {
        var entry = new FlashPartitionEntry { Name = "boot" };
        Assert.False(entry.IsRunning);
        Assert.Equal(-1, entry.Percent);
        var runningNotifications = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashPartitionEntry.IsRunning))
            {
                runningNotifications++;
            }
        };
        entry.State = FlashRowState.Running;
        Assert.True(entry.IsRunning);
        entry.State = FlashRowState.Succeeded;
        Assert.False(entry.IsRunning);
        Assert.Equal(2, runningNotifications);
    }

    [Fact]
    public async Task EdlPackageWithMultipleLoaders_OfferEveryCandidateForExplicitChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "system.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "prog_ufs_firehose_sm8250.elf"), [3]);
            File.WriteAllBytes(Path.Combine(root, "prog_firehose_256kb.elf"), [4]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"), """
                <data><program filename="system.bin" label="system_a" start_sector="8"
                                num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"
                                physical_partition_number="0" /></data>
                """);
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    var pathBox = view.FindControl<TextBox>("PackagePathBox");
                    pathBox!.Text = root;
                    model.PackagePath = root;
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    var until = DateTime.UtcNow.AddSeconds(8);
                    while (model.Partitions.Count == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);

                    var picker = view.FindControl<ComboBox>("EdlProgrammerPickBox");
                    Assert.NotNull(picker);
                    Assert.True(picker!.IsVisible,
                        $"rows={model.Partitions.Count} log={model.LogText}");
                    Assert.Equal(
                        ["prog_firehose_256kb.elf", "prog_ufs_firehose_sm8250.elf"],
                        picker.Items.Cast<object?>().Select(item => item?.ToString()));
                    Assert.Equal(-1, picker.SelectedIndex);
                    picker.SelectedIndex = 1;
                    Assert.Equal(1, picker.SelectedIndex);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MtkAutoMatchCheckBox_DisablesCountedLoaderEntrySelection()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var autoMatch = view.FindControl<CheckBox>("MtkAutoMatchBox");
                var entryBox = view.FindControl<ComboBox>("MtkEntryIndexBox");
                var baudBox = view.FindControl<ComboBox>("MtkBaudBox");
                Assert.NotNull(autoMatch);
                Assert.NotNull(entryBox);
                Assert.NotNull(baudBox);
                Assert.Equal(115200, baudBox!.SelectedItem);
                entryBox!.ItemsSource = new[] { "entry 0" };
                entryBox.SelectedIndex = 0;
                Assert.True(entryBox.IsEnabled);

                autoMatch!.IsChecked = true;
                view.UpdateLayout();
                Assert.False(entryBox.IsEnabled);

                autoMatch.IsChecked = false;
                view.UpdateLayout();
                Assert.True(entryBox.IsEnabled);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MtkProtocolSelector_NonXFlashFamilies_RejectAttachingAnXFlashAgent()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var protocolBox = view.FindControl<ComboBox>("MtkProtocolBox");
                var attachBox = view.FindControl<CheckBox>("MtkAttachAgentBox");
                var portBox = view.FindControl<ComboBox>("MtkPortBox");
                Assert.NotNull(protocolBox);
                Assert.NotNull(attachBox);
                Assert.NotNull(portBox);
                Assert.Equal(3, protocolBox!.Items.Count);
                Assert.Equal(0, protocolBox.SelectedIndex);

                portBox!.ItemsSource = new[] { "COM1" };
                portBox.SelectedIndex = 0;
                attachBox!.IsChecked = true;
                view.UpdateLayout();
                Assert.True(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));

                protocolBox.SelectedIndex = 1;
                view.UpdateLayout();
                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));

                protocolBox.SelectedIndex = 2;
                view.UpdateLayout();
                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));

                protocolBox.SelectedIndex = 0;
                view.UpdateLayout();
                Assert.True(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EdlAuthKindSelector_ProjectVerification_ExposesCatalogueAndDerivesProjectName()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var kindBox = view.FindControl<ComboBox>("EdlAuthKindBox");
                var modelBox = view.FindControl<ComboBox>("EdlOnePlusModelBox");
                var projectBox = view.FindControl<TextBox>("EdlProjectIdBox");
                Assert.NotNull(kindBox);
                Assert.NotNull(modelBox);
                Assert.NotNull(projectBox);
                Assert.Equal(5, kindBox!.Items.Count);
                Assert.Equal(0, kindBox.SelectedIndex);
                Assert.False(modelBox!.IsVisible);
                Assert.True(projectBox!.IsEnabled);

                kindBox.SelectedIndex = 2;
                view.UpdateLayout();
                Assert.True(modelBox.IsVisible);
                Assert.False(projectBox.IsEnabled);
                Assert.Equal(EdlOnePlusProjectCatalog.ProjectIds.Count, modelBox.Items.Count);

                var lemonade = 0;
                for (var index = 0; index < EdlOnePlusProjectCatalog.ProjectIds.Count; index++)
                {
                    if (EdlOnePlusProjectCatalog.ProjectIds[index] == "19825")
                    {
                        lemonade = index;
                    }
                }

                modelBox.SelectedIndex = lemonade;
                view.UpdateLayout();
                Assert.Equal("0898dcd6", projectBox.Text);

                kindBox.SelectedIndex = 1;
                view.UpdateLayout();
                Assert.False(modelBox.IsVisible);
                Assert.True(projectBox.IsEnabled);
                kindBox.SelectedIndex = 3;
                view.UpdateLayout();
                Assert.False(modelBox.IsVisible);
                Assert.True(projectBox.IsEnabled);
                Assert.NotEqual(kindBox.Items[2]?.ToString(), kindBox.Items[3]?.ToString());
                Assert.False(string.IsNullOrWhiteSpace(kindBox.Items[3]?.ToString()));

                kindBox.SelectedIndex = 4;
                view.UpdateLayout();
                Assert.False(modelBox.IsVisible);
                Assert.False(projectBox.IsEnabled);
                Assert.True(view.FindControl<Button>("ChooseEdlXiaomiAuthFileButton")!.IsVisible);
                Assert.False(string.IsNullOrWhiteSpace(kindBox.Items[4]?.ToString()));
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EdlTuningControls_DefaultsAndAdvancedButtons_ArePresentInEdlPane()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var baud = view.FindControl<ComboBox>("EdlSerialBaudBox")!;
                    var lun = view.FindControl<ComboBox>("EdlLunBox")!;
                    Assert.Equal([115200, 921600, 460800, 230400], (int[])baud.ItemsSource!);
                    Assert.Equal(115200, baud.SelectedItem);
                    Assert.Equal(0, baud.SelectedIndex);
                    Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], (int[])lun.ItemsSource!);
                    Assert.Equal(0, lun.SelectedItem);
                    var pane = view.FindControl<StackPanel>("EdlPane")!;
                    var poke = view.FindControl<Button>("EdlPokeButton")!;
                    var memcpy = view.FindControl<Button>("EdlMemcpyButton")!;
                    Assert.Contains(pane, poke.GetVisualAncestors(), ReferenceEqualityComparer.Instance);
                    Assert.Contains(pane, memcpy.GetVisualAncestors(), ReferenceEqualityComparer.Instance);
                }
                finally
                {
                    window.Close();
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void FormatPartitionTable_WithinCap_WritesOneLinePerPartition()
    {
        var partitions = new[]
        {
            Partition(0, "boot", 34, 65),
            Partition(1, "system", 66, 2000),
            Partition(2, "userdata", 2001, 4000)
        };

        var lines = FlashView.FormatPartitionTable(partitions, 64, "{0}@{1}+{2}", "more {0}");

        Assert.Equal(["boot@34+32", "system@66+1935", "userdata@2001+2000"], lines);
    }

    [Fact]
    public void FormatPartitionTable_AboveCap_StopsAndReportsTheRemainder()
    {
        var partitions = new[]
        {
            Partition(0, "boot", 34, 65),
            Partition(1, "system", 66, 2000),
            Partition(2, "userdata", 2001, 4000)
        };

        var lines = FlashView.FormatPartitionTable(partitions, 2, "{0}", "more {0}");

        Assert.Equal(["boot", "system", "more 1"], lines);
    }

    [Fact]
    public void FormatPartitionTable_EmptyTable_ProducesNoLines()
        => Assert.Empty(FlashView.FormatPartitionTable([], 64, "{0}", "more {0}"));

    [Fact]
    public void FormatPartitionTable_InvalidBounds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FlashView.FormatPartitionTable([Partition(0, "boot", 1, 2)], 0, "{0}", "more {0}"));
        Assert.Throws<ArgumentNullException>(() =>
            FlashView.FormatPartitionTable(null!, 64, "{0}", "more {0}"));
    }

    [Fact]
    public async Task InitialVisibility_HidesProtocolSpecificPanesUntilTheirPreconditionsHold()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();

                Assert.False(view.FindControl<StackPanel>("MtkPane")!.IsVisible);
                Assert.False(view.FindControl<StackPanel>("EdlPane")!.IsVisible);
                Assert.False(view.FindControl<StackPanel>("BundleActionRow")!.IsVisible);
                Assert.False(view.FindControl<Button>("FlashEdlBundleButton")!.IsVisible);
                Assert.False(view.FindControl<TextBlock>("EdlCapabilityText")!.IsVisible);
                Assert.True(view.FindControl<Button>("FlashSelectedButton")!.IsVisible);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadEdlBundleTuning_UnchangedControls_ReportsAutomaticTuning()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);

                Assert.True(view.TryReadEdlBundleTuning(out var tuning));
                Assert.Null(tuning.OverrideSectorSize);
                Assert.Null(tuning.Slot);
                Assert.Equal(0, tuning.WipeFirstSectors);
                Assert.Equal(string.Empty, model.LogText);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadEdlBundleTuning_SelectedControls_ProducesTheMatchingTuning()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                view.FindControl<ComboBox>("EdlSectorSizeBox")!.SelectedIndex = 3;
                view.FindControl<ComboBox>("EdlSlotBox")!.SelectedIndex = 2;
                view.FindControl<TextBox>("EdlWipeFirstBox")!.Text = "16";

                Assert.True(view.TryReadEdlBundleTuning(out var tuning));
                Assert.Equal((long?)4096, tuning.OverrideSectorSize);
                Assert.Equal((int?)2, tuning.Slot);
                Assert.Equal(16L, tuning.WipeFirstSectors);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public async Task TryReadEdlBundleTuning_InvalidLeadingWipe_LogsAndFails(string text)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);
                view.FindControl<TextBox>("EdlWipeFirstBox")!.Text = text;

                Assert.False(view.TryReadEdlBundleTuning(out _));
                Assert.Contains(LanguageService.Instance.GetString("Flash_EdlWipeFirstInvalid"),
                    model.LogText, StringComparison.Ordinal);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadMtkSelection_WithoutSerialPort_LogsAndFails()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);
                view.FindControl<ComboBox>("MtkPortBox")!.SelectedItem = null;

                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.Contains(LanguageService.Instance.GetString("Flash_MtkPortRequired"),
                    model.LogText, StringComparison.Ordinal);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadMtkSelection_WithoutLoaderAndAttach_LogsAndFails()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);
                var portBox = view.FindControl<ComboBox>("MtkPortBox")!;
                portBox.ItemsSource = new[] { "COM99" };
                portBox.SelectedItem = "COM99";

                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.Contains(LanguageService.Instance.GetString("Flash_MtkDaRequired"),
                    model.LogText, StringComparison.Ordinal);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadMtkSelection_WithoutSelectedBaud_LogsAndFails()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);
                var portBox = view.FindControl<ComboBox>("MtkPortBox")!;
                portBox.ItemsSource = new[] { "COM99" };
                portBox.SelectedItem = "COM99";
                view.FindControl<CheckBox>("MtkAttachAgentBox")!.IsChecked = true;
                view.FindControl<ComboBox>("MtkBaudBox")!.SelectedIndex = -1;

                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.Contains(LanguageService.Instance.GetString("Flash_MtkBaudRequired"),
                    model.LogText, StringComparison.Ordinal);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadMtkSelection_WithPortAndAttachAgent_RequiresNoLoaderFile()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var portBox = view.FindControl<ComboBox>("MtkPortBox")!;
                portBox.ItemsSource = new[] { "COM99" };
                portBox.SelectedItem = "COM99";
                view.FindControl<CheckBox>("MtkAttachAgentBox")!.IsChecked = true;

                var ok = view.TryReadMtkSelection(out var useSerial, out var usbDevice, out var port,
                    out var baudRate, out var attach, out var autoMatch);

                Assert.True(ok);
                Assert.True(useSerial);
                Assert.Null(usbDevice);
                Assert.Equal("COM99", port);
                Assert.True(baudRate > 0);
                Assert.True(attach);
                Assert.False(autoMatch);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PackagePath_ScatterWithImagelessEntry_ListsFlashAndEraseRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "boot.img"), [1, 2, 3],
                TestContext.Current.CancellationToken);
            var scatter = Path.Combine(root, "scatter.xml");
            await File.WriteAllTextAsync(scatter, """
                - general: MTK_PLATFORM_CFG
                  info:
                  - config_version: V2.1.0
                    platform: MT6785
                    project: test
                    storage: EMMC
                    boot_channel: test
                    block_size: 0x20000
                - partition_index: SYS2
                  partition_name: boot
                  file_name: boot.img
                  is_download: true
                  type: NORMAL_ROM
                  linear_start_addr: 20480
                  physical_start_addr: 20480
                  partition_size: 2048
                  region: EMMC_USER
                  storage: HW_STORAGE_EMMC
                  boundary_check: true
                  is_reserved: false
                  operation_type: UPDATE
                  is_upgradable: true
                  empty_boot_needed: false
                  reserve: 0x00
                - partition_index: SYS3
                  partition_name: userdata
                  file_name: NONE
                  is_download: false
                  type: EXT4_IMG
                  linear_start_addr: 40960
                  physical_start_addr: 40960
                  partition_size: 65536
                  region: EMMC_USER
                  storage: HW_STORAGE_EMMC
                  boundary_check: true
                  is_reserved: false
                  operation_type: UPDATE
                  is_upgradable: true
                  empty_boot_needed: false
                  reserve: 0x00
                """, TestContext.Current.CancellationToken);
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    var pathBox = view.FindControl<TextBox>("PackagePathBox")!;
                    model.PackagePath = scatter;
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (model.Partitions.Count == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);

                    var boot = Assert.Single(model.Partitions, row => row.Name == "boot");
                    var userdata = Assert.Single(model.Partitions, row => row.Name == "userdata");
                    Assert.Equal("flash", boot.Command);
                    Assert.Equal("erase", userdata.Command);
                    Assert.True(userdata.CanSelect);
                    Assert.Equal(string.Empty, userdata.FilePath);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(MtkDaProtocol.XFlash, MtkXFlashShutdownMode.PowerOff, null)]
    [InlineData(MtkDaProtocol.XFlash, MtkXFlashShutdownMode.NormalBoot, null)]
    [InlineData(MtkDaProtocol.Legacy, MtkXFlashShutdownMode.PowerOff, null)]
    [InlineData(MtkDaProtocol.Legacy, MtkXFlashShutdownMode.Fastboot, null)]
    [InlineData(MtkDaProtocol.Xml, MtkXFlashShutdownMode.NormalBoot, null)]
    [InlineData(MtkDaProtocol.Xml, MtkXFlashShutdownMode.Fastboot, null)]
    [InlineData(MtkDaProtocol.Xml, MtkXFlashShutdownMode.PowerOff, "Flash_MtkXmlPowerOffUnsupported")]
    public void MtkPowerRequestRefusalKey_FamilyMatrix_RefusesOnlyTheXmlPowerOff(
        MtkDaProtocol family, MtkXFlashShutdownMode mode, string? expected)
        => Assert.Equal(expected, FlashView.MtkPowerRequestRefusalKey(family, mode));

    [Theory]
    [InlineData(MtkDaProtocol.XFlash, true)]
    [InlineData(MtkDaProtocol.Legacy, true)]
    [InlineData(MtkDaProtocol.Xml, false)]
    public void FamilyUsesEmiParameters_XmlFamily_DropsTheXFlashDramResource(MtkDaProtocol family, bool expected)
        => Assert.Equal(expected, FlashView.FamilyUsesEmiParameters(family));

    [Fact]
    public async Task MtkProtocolSelector_ThreeFamilies_PublishesLocalizedItemsInOrderWithXFlashDefault()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage("en-US");
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var protocolBox = view.FindControl<ComboBox>("MtkProtocolBox")!;
                    var storageBox = view.FindControl<ComboBox>("MtkStorageKindBox")!;
                    var language = LanguageService.Instance;
                    Assert.Equal(
                        [
                            language.GetString("Flash_MtkProtocolXFlash"),
                            language.GetString("Flash_MtkProtocolLegacy"),
                            language.GetString("Flash_MtkProtocolXml")
                        ],
                        protocolBox.Items.Cast<object?>().Select(item => (item as ContentControl)?.Content?.ToString()));
                    Assert.Equal(0, protocolBox.SelectedIndex);
                    Assert.Equal(["eMMC", "UFS"], storageBox.Items.Cast<object?>().Select(item => item?.ToString()));
                    Assert.Equal(0, storageBox.SelectedIndex);
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0", "16", true, 0, 16)]
    [InlineData(" 4 ", " 1 ", true, 4, 1)]
    [InlineData("0", "0", false, 0, 0)]
    [InlineData("-1", "8", false, 0, 0)]
    [InlineData("0", "-8", false, 0, 0)]
    [InlineData("0x4", "8", false, 0, 0)]
    [InlineData("", "8", false, 0, 0)]
    [InlineData("8", null, false, 0, 0)]
    public void TryReadEfuseWindow_BoxTexts_AcceptOnlyANonNegativeStartAndAPositiveCount(
        string? start, string? count, bool expected, int expectedStart, int expectedCount)
    {
        var parsed = FlashView.TryReadEfuseWindow(start, count, out var startIndex, out var cellCount);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(expectedStart, startIndex);
            Assert.Equal(expectedCount, cellCount);
        }
    }

    [Theory]
    [InlineData("60000", true, 60000)]
    [InlineData(" 1 ", true, 1)]
    [InlineData("86400000", true, 86400000)]
    [InlineData("0", false, 0)]
    [InlineData("86400001", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("60s", false, 0)]
    [InlineData("", false, 0)]
    public void TryReadEdlAuthTimeout_BoxText_KeepsTheDocumentedMillisecondWindow(
        string? text, bool expected, int expectedMs)
    {
        var parsed = FlashView.TryReadEdlAuthTimeout(text, out var timeoutMs);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(expectedMs, timeoutMs);
        }
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("B", "b")]
    [InlineData(" 1 ", "1")]
    [InlineData("0", "0")]
    [InlineData("2", null)]
    [InlineData("c", null)]
    [InlineData("10", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeEdlSlot_BoxText_ForwardsOnlyTheDocumentedSpellings(
        string? text, string? expected)
        => Assert.Equal(expected, FlashView.NormalizeEdlSlot(text));

    [Theory]
    [InlineData("en-US", "Repair partition table", "Collect memory dump (Sahara debug mode)")]
    [InlineData("zh-CN", "修复分区表", "采集内存转储（Sahara 调试模式）")]
    public async Task EdlStorageMaintenanceButtons_InBothLanguages_PublishLocalizedLabels(
        string language, string repairLabel, string dumpLabel)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var repair = view.FindControl<Button>("EdlRepairGptButton")!;
                    var dump = view.FindControl<Button>("EdlRamDumpButton")!;
                    Assert.Equal(repairLabel, repair.Content?.ToString());
                    Assert.Equal(dumpLabel, dump.Content?.ToString());
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ReadExploitFamily_SelectorIndex_NamesOnlyWhatTheUserPicked()
    {
        var families = Enum.GetValues<MtkExploitFamily>();

        Assert.Null(FlashView.ReadExploitFamily(0));
        Assert.Null(FlashView.ReadExploitFamily(-1));
        Assert.Equal(families[0], FlashView.ReadExploitFamily(1));
        Assert.Equal(families[^1], FlashView.ReadExploitFamily(families.Length));
        Assert.Equal(MtkExploitFamily.Kamakiri, FlashView.ReadExploitFamily(
            Array.IndexOf(families, MtkExploitFamily.Kamakiri) + 1));
    }

    [Theory]
    [InlineData("Frames", "0", "8", true)]
    [InlineData("Frames", " 12 ", " 4 ", true)]
    [InlineData("ByteRange", "512", "1024", true)]
    [InlineData("Frames", "-1", "8", false)]
    [InlineData("Frames", "0", "0", false)]
    [InlineData("ByteRange", "0", "-8", false)]
    [InlineData("ByteRange", "abc", "8", false)]
    [InlineData("ByteRange", null, "8", false)]
    [InlineData("Frames", "0", "2147483648", false)]
    [InlineData("FullArea", "junk", "junk", true)]
    public void TryReadRpmbShape_ModeAndTexts_MapsOnlyWhatTheShapeNeeds(
        string modeName, string? offset, string? length, bool expected)
    {
        var mode = Enum.Parse<MtkRpmbCaptureMode>(modeName);
        var parsed = FlashView.TryReadRpmbShape(mode, offset, length, out var frames, out var byteOffset,
            out var byteLength);

        Assert.Equal(expected, parsed);
        if (mode == MtkRpmbCaptureMode.Frames && expected)
        {
            Assert.Equal(long.Parse(offset!.Trim(), CultureInfo.InvariantCulture), frames.StartFrame);
            Assert.Equal(int.Parse(length!.Trim(), CultureInfo.InvariantCulture), frames.FrameCount);
            Assert.Equal(0, byteOffset);
            Assert.Equal(0, byteLength);
        }
        else if (mode == MtkRpmbCaptureMode.ByteRange && expected)
        {
            Assert.Equal(long.Parse(offset!.Trim(), CultureInfo.InvariantCulture), byteOffset);
            Assert.Equal(long.Parse(length!.Trim(), CultureInfo.InvariantCulture), byteLength);
        }
    }

    [Theory]
    [InlineData("en-US", "Run BootROM bypass", "Read RPMB area", "Kamakiri (watchdog overflow)",
        "Allow volatile security-control register writes (required by every bypass family)")]
    [InlineData("zh-CN", "运行 BootROM 绕过", "读取 RPMB 区域", "Kamakiri（看门狗溢出）",
        "允许易失安全控制寄存器写入（所有绕过家族都需要）")]
    public async Task MtkUsbOnlyActions_LocalizedControls_FollowTheLanguageAndEnumOrder(
        string language, string bypassLabel, string rpmbLabel, string familyItem, string volatileLabel)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var bypass = view.FindControl<Button>("MtkBypassButton")!;
                    var rpmb = view.FindControl<Button>("MtkRpmbReadButton")!;
                    var row = view.FindControl<StackPanel>("MtkUsbOnlyRow")!;
                    var families = view.FindControl<ComboBox>("MtkExploitFamilyBox")!;
                    var volatileBox = view.FindControl<CheckBox>("MtkExploitVolatileBox")!;
                    Assert.Equal(bypassLabel, bypass.Content?.ToString());
                    Assert.Equal(rpmbLabel, rpmb.Content?.ToString());
                    Assert.Equal(volatileLabel, volatileBox.Content?.ToString());
                    Assert.Contains(bypass, row.Children);
                    Assert.Contains(rpmb, row.Children);
                    Assert.Equal(familyItem, families.Items[1]?.ToString());
                    Assert.Equal(0, families.SelectedIndex);
                    Assert.Equal(Enum.GetNames<MtkExploitFamily>().Length, families.Items.Count - 1);
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0", true, 0u)]
    [InlineData(" 3 ", true, 3u)]
    [InlineData("255", true, 255u)]
    [InlineData("-1", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData("abc", false, 0u)]
    [InlineData("3.5", false, 0u)]
    [InlineData("4294967296", false, 0u)]
    public void TryReadSecCfgPartition_TextForms_AcceptsOnlyPlainNonNegativeNumbers(
        string? text, bool expected, uint expectedId)
    {
        var parsed = FlashView.TryReadSecCfgPartition(text, out var partitionId);

        Assert.Equal(expected, parsed);
        Assert.Equal(expected ? expectedId : 0u, partitionId);
    }

    [Theory]
    [InlineData("en-US", "Dump user area")]
    [InlineData("zh-CN", "整用户区转储")]
    public async Task MtkFullDumpControl_LocalizedLabelAndPane_MatchTheChosenLanguage(
        string language, string dumpLabel)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var dump = view.FindControl<Button>("MtkFullDumpButton")!;
                    Assert.Equal(dumpLabel, dump.Content?.ToString());
                    var pane = view.FindControl<StackPanel>("MtkPane")!;
                    Assert.Contains(dump, pane.Children);
                    Assert.DoesNotContain(dump, view.FindControl<StackPanel>("EdlPane")!.Children);
                    Assert.True(
                        pane.Children.IndexOf(dump) <
                        pane.Children.IndexOf(view.FindControl<Button>("MtkSecCfgReadButton")!),
                        "The read-only user-area dump has to stay with the storage group, above the security controls.");
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en-US", "Read secure-boot configuration", "Write secure-boot lock state", "Lock (state 1)",
        "Unlock (state 3)", "Read eFuse cell range", "Preview OEM key submission")]
    [InlineData("zh-CN", "读取安全启动配置", "写入安全启动锁定状态", "上锁（状态 1）",
        "解锁（状态 3）", "读取 eFuse 单元区间", "预览 OEM 密钥提交内容")]
    public async Task MtkSecurityControls_LocalizedLabelsAndDefaults_MatchTheChosenLanguage(
        string language, string readLabel, string applyLabel, string firstIntent, string secondIntent,
        string efuseRangeLabel, string previewLabel)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var read = view.FindControl<Button>("MtkSecCfgReadButton")!;
                    var apply = view.FindControl<Button>("MtkSecCfgApplyButton")!;
                    var intents = view.FindControl<ComboBox>("MtkSecCfgLockBox")!;
                    var partition = view.FindControl<TextBox>("MtkSecCfgPartitionBox")!;
                    Assert.Equal(readLabel, read.Content?.ToString());
                    Assert.Equal(applyLabel, apply.Content?.ToString());
                    Assert.Equal("0", partition.Text);
                    Assert.Equal([firstIntent, secondIntent], intents.Items.Cast<object?>().Select(item => item?.ToString()));
                    Assert.Equal(0, intents.SelectedIndex);
                    var pane = view.FindControl<StackPanel>("MtkPane")!;
                    Assert.Contains(read, pane.Children);
                    Assert.Contains(apply, pane.Children);
                    Assert.NotNull(view.FindControl<Button>("MtkEfusePublicKeyButton"));
                    Assert.Equal(efuseRangeLabel,
                        view.FindControl<Button>("MtkEfuseRangeButton")!.Content?.ToString());
                    Assert.Equal(previewLabel,
                        view.FindControl<Button>("MtkOemKeyPreviewButton")!.Content?.ToString());
                    Assert.Equal("0", view.FindControl<TextBox>("MtkEfuseStartBox")!.Text);
                    Assert.Equal("16", view.FindControl<TextBox>("MtkEfuseCountBox")!.Text);
                    var mtkPane = view.FindControl<StackPanel>("MtkPane")!;
                    Assert.Contains(
                        mtkPane.GetVisualAncestors(),
                        ancestor => ReferenceEquals(ancestor, view.FindControl<ScrollViewer>("PageScroll")));
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0x10000", "64", true, 0x10000u, 64)]
    [InlineData("10000", "4", true, 0x10000u, 4)]
    [InlineData(" 0xABCD ", " 16 ", true, 0xABCDu, 16)]
    [InlineData("fffffff", "1048576", true, 0xFFFFFFFu, 1048576)]
    [InlineData("0x", "4", false, 0u, 0)]
    [InlineData("", "4", false, 0u, 0)]
    [InlineData("zz", "4", false, 0u, 0)]
    [InlineData("10", "0", false, 0u, 0)]
    [InlineData("10", "-4", false, 0u, 0)]
    [InlineData("10", "abc", false, 0u, 0)]
    [InlineData("10", "0x10", false, 0u, 0)]
    public void TryReadMemoryWindow_EvaluatesAddressAndLengthShapes(string? addressText, string? lengthText,
        bool accepted, uint expectedAddress, int expectedLength)
    {
        var parsed = FlashView.TryReadMemoryWindow(addressText, lengthText, out var address, out var lengthBytes);

        Assert.Equal(accepted, parsed);
        Assert.Equal(accepted ? expectedAddress : 0u, address);
        Assert.Equal(accepted ? expectedLength : 0, lengthBytes);
    }

    [Fact]
    public void FormatMemoryWindow_PadsTheRowAndMarksUnprintableBytes()
    {
        var text = FlashView.FormatMemoryWindow(0x1000, new byte[] { 0x41, 0x20, 0x00 });

        Assert.Equal($"0x00001000 41 20 00{new string(' ', 40)}A .", text);
    }

    [Fact]
    public void FormatMemoryWindow_SplitsEverySixteenBytesIntoItsOwnRow()
    {
        var bytes = new byte[20];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(0x41 + index);
        }

        var lines = FlashView.FormatMemoryWindow(0x2000, bytes).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("0x00002000 ", lines[0]);
        Assert.StartsWith("0x00002010 ", lines[1]);
    }

    [Fact]
    public async Task MtkMemoryControls_AreInTheMtkPaneAndStartWithoutConfirmation()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var pane = view.FindControl<StackPanel>("MtkPane")!;
                    foreach (var name in new[]
                    {
                        "MtkMemoryAddressBox", "MtkMemoryLengthBox", "MtkMemoryRegisterBox", "MtkMemoryPeekButton",
                        "MtkPokeDataBox", "MtkPokeConfirmBox", "MtkPokeButton", "MtkBromDumpButton"
                    })
                    {
                        var control = view.FindControl<Control>(name)!;
                        Assert.NotNull(control);
                        Assert.Contains(pane, control.GetVisualAncestors(), ReferenceEqualityComparer.Instance);
                    }

                    Assert.Equal("0x10000", view.FindControl<TextBox>("MtkMemoryAddressBox")!.Text);
                    Assert.Equal("64", view.FindControl<TextBox>("MtkMemoryLengthBox")!.Text);
                    Assert.False(view.FindControl<CheckBox>("MtkMemoryRegisterBox")!.IsChecked.GetValueOrDefault());
                    Assert.False(view.FindControl<CheckBox>("MtkPokeConfirmBox")!.IsChecked.GetValueOrDefault());
                    Assert.True(string.IsNullOrEmpty(view.FindControl<TextBox>("MtkPokeDataBox")!.Text));
                }
                finally
                {
                    window.Close();
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BuildOemKeyRequest_SoftwareClassWithoutSejBase_RefusesBeforeAnySubmission()
    {
        Assert.Throws<ArgumentException>(() =>
            FlashView.BuildOemKeyRequest(
                Array.IndexOf(Enum.GetValues<MtkOemKeyKind>(), MtkOemKeyKind.SoftwareOneTimeProgramKey),
                new string('A', 64), null));
    }

    [Fact]
    public void BuildOemKeyRequest_MalformedKeyAndBase_TextFailuresSurfaceAsTheirOwnTypes()
    {
        var replayIndex = Array.IndexOf(Enum.GetValues<MtkOemKeyKind>(), MtkOemKeyKind.ReplayProtectedAreaKey);
        Assert.Throws<ArgumentException>(() => FlashView.BuildOemKeyRequest(replayIndex, "ABC", null));
        Assert.Throws<FormatException>(() => FlashView.BuildOemKeyRequest(replayIndex, new string('B', 64), "zzz"));
    }

    [Fact]
    public void BuildOemKeyRequest_ReplayClassWithoutBase_AcceptsTheExactKeyLength()
    {
        var kinds = Enum.GetValues<MtkOemKeyKind>();

        var request = FlashView.BuildOemKeyRequest(
            Array.IndexOf(kinds, MtkOemKeyKind.ReplayProtectedAreaKey), "  " + new string('C', 64) + " ", null);

        Assert.Equal(MtkOemKeyKind.ReplayProtectedAreaKey, request.Kind);
        Assert.Null(request.SejBaseAddress);
        Assert.Equal(32, request.DecodeKey().Length);
        Assert.All(request.DecodeKey(), value => Assert.Equal((byte)0xCC, value));
    }

    [Theory]
    [InlineData("en-US", "Program OEM key", "OEM key class",
        new[] { "SEJ software one-time-program key", "RPMB key" })]
    [InlineData("zh-CN", "写入 OEM 密钥", "OEM 密钥类别",
        new[] { "SEJ 软件一次性编程密钥", "RPMB 密钥" })]
    public async Task MtkOemKeyControls_LocalizedLabelsAndKindItems_MatchTheChosenLanguage(
        string language, string programLabel, string kindLabel, string[] kindItems)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var program = view.FindControl<Button>("MtkOemKeyProgramButton")!;
                    var kinds = view.FindControl<ComboBox>("MtkOemKeyKindBox")!;
                    var key = view.FindControl<TextBox>("MtkOemKeyHexBox")!;
                    Assert.Equal(programLabel, program.Content?.ToString());
                    Assert.Equal(kindLabel, LanguageService.Instance.GetString("Flash_MtkOemKeyKind"));
                    Assert.Equal(kindItems, kinds.Items.Cast<object?>().Select(item => item?.ToString()));
                    Assert.Equal(0, kinds.SelectedIndex);
                    Assert.True(string.IsNullOrEmpty(key.Text));
                    var pane = view.FindControl<StackPanel>("MtkPane")!;
                    Assert.Contains(program, pane.Children);
                    Assert.Contains(key, pane.Children);
                    Assert.NotNull(view.FindControl<TextBox>("MtkOemKeySejBaseBox"));
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task FlashViewResourceKeys_EveryReferencedKey_ResolvesToTranslatedText(string language)
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory,
                out var repositoryRoot))
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the view sources " +
                "cannot be read.");
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "FlashView.axaml", "FlashView.axaml.cs" })
        {
            var text = await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot!, "UotanStudio", "Views", "Shell", file),
                TestContext.Current.CancellationToken);
            foreach (Match match in Regex.Matches(text, "Flash_[A-Za-z0-9]+"))
            {
                keys.Add(match.Value);
            }
        }

        Assert.True(keys.Count > 100);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                try
                {
                    foreach (var key in keys)
                    {
                        Assert.NotEqual(key, LanguageService.Instance.GetString(key));
                    }
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en-US", "Choose UFS provisioning profile", "Preview UFS provisioning plan",
        "Provision UFS logical units", "Reset the UFS device")]
    [InlineData("zh-CN", "选择 UFS provisioning 描述符文件", "预览 UFS provisioning 计划",
        "建立 UFS 逻辑单元", "复位 UFS 设备")]
    public async Task EdlUfsProvisioningControls_LocalizedLabels_AreGroupedInsideTheEdlPane(
        string language, string chooseLabel, string previewLabel, string provisionLabel, string resetLabel)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var pane = view.FindControl<StackPanel>("EdlPane")!;
                    var choose = view.FindControl<Button>("EdlChooseUfsProfileButton")!;
                    var preview = view.FindControl<Button>("EdlUfsPreviewButton")!;
                    var provision = view.FindControl<Button>("EdlUfsProvisionButton")!;
                    var reset = view.FindControl<Button>("EdlUfsResetButton")!;
                    Assert.Equal(chooseLabel, choose.Content?.ToString());
                    Assert.Equal(previewLabel, preview.Content?.ToString());
                    Assert.Equal(provisionLabel, provision.Content?.ToString());
                    Assert.Equal(resetLabel, reset.Content?.ToString());
                    Assert.Contains(choose, pane.Children);
                    Assert.Contains(preview, pane.Children);
                    Assert.Contains(provision, pane.Children);
                    Assert.Contains(reset, pane.Children);
                    Assert.NotNull(view.FindControl<TextBlock>("EdlUfsProfileText"));
                }
                finally
                {
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }

                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryReadMtkSelection_LegacyFamilyWithUfs_RefusesWithoutAskingForALoader()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new FlashView();
                var model = Assert.IsType<FlashViewModel>(view.DataContext);
                var portBox = view.FindControl<ComboBox>("MtkPortBox")!;
                portBox.ItemsSource = new[] { "COM99" };
                portBox.SelectedItem = "COM99";
                var protocolBox = view.FindControl<ComboBox>("MtkProtocolBox")!;
                var storageBox = view.FindControl<ComboBox>("MtkStorageKindBox")!;
                var language = LanguageService.Instance;
                var refusal = language.GetString("Flash_MtkLegacyStorageUnsupported");

                protocolBox.SelectedIndex = 1;
                storageBox.SelectedIndex = 1;
                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.Contains(refusal, model.LogText, StringComparison.Ordinal);

                protocolBox.SelectedIndex = 2;
                var xmlStart = model.LogText.Length;
                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.DoesNotContain(refusal, model.LogText[xmlStart..], StringComparison.Ordinal);
                Assert.Contains(language.GetString("Flash_MtkDaRequired"), model.LogText[xmlStart..],
                    StringComparison.Ordinal);

                storageBox.SelectedIndex = 0;
                protocolBox.SelectedIndex = 1;
                var emmcStart = model.LogText.Length;
                Assert.False(view.TryReadMtkSelection(out _, out _, out _, out _, out _, out _));
                Assert.DoesNotContain(refusal, model.LogText[emmcStart..], StringComparison.Ordinal);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildMtkFamilyPlan_SelectedFamily_PopulatesOnlyThatFamilyStepsAndRegion()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "boot.img"), new byte[600],
                TestContext.Current.CancellationToken);
            var scatter = Path.Combine(root, "scatter.xml");
            await File.WriteAllTextAsync(scatter, """
                - general: MTK_PLATFORM_CFG
                  info:
                  - config_version: V2.1.0
                    platform: MT6785
                    project: test
                    storage: EMMC
                    boot_channel: test
                    block_size: 0x20000
                - partition_index: SYS2
                  partition_name: boot
                  file_name: boot.img
                  is_download: true
                  type: NORMAL_ROM
                  linear_start_addr: 20480
                  physical_start_addr: 20480
                  partition_size: 2048
                  region: EMMC_USER
                  storage: HW_STORAGE_EMMC
                  boundary_check: true
                  is_reserved: false
                  operation_type: UPDATE
                  is_upgradable: true
                  empty_boot_needed: false
                  reserve: 0x00
                - partition_index: SYS3
                  partition_name: userdata
                  file_name: NONE
                  is_download: false
                  type: EXT4_IMG
                  linear_start_addr: 40960
                  physical_start_addr: 40960
                  partition_size: 65536
                  region: EMMC_USER
                  storage: HW_STORAGE_EMMC
                  boundary_check: true
                  is_reserved: false
                  operation_type: UPDATE
                  is_upgradable: true
                  empty_boot_needed: false
                  reserve: 0x00
                """, TestContext.Current.CancellationToken);
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    var view = new FlashView();
                    var model = Assert.IsType<FlashViewModel>(view.DataContext);
                    var pathBox = view.FindControl<TextBox>("PackagePathBox")!;
                    model.PackagePath = scatter;
                    pathBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (model.Partitions.Count == 0 && DateTime.UtcNow < until)
                        await Task.Delay(20, TestContext.Current.CancellationToken);

                    var boot = Assert.Single(model.Partitions, row => row.Name == "boot");
                    var userdata = Assert.Single(model.Partitions, row => row.Name == "userdata");
                    var protocolBox = view.FindControl<ComboBox>("MtkProtocolBox")!;

                    protocolBox.SelectedIndex = 0;
                    var xFlash = view.BuildMtkFamilyPlan(false, [boot], true)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(MtkDaProtocol.XFlash, xFlash.Family);
                    Assert.Equal("SYS2", Assert.Single(xFlash.ScatterSteps).PartitionIndex);
                    Assert.Empty(xFlash.LegacySteps);
                    Assert.Empty(xFlash.XmlSteps);
                    Assert.Empty(xFlash.XFlashEraseSteps);
                    Assert.Equal(8u, xFlash.PhysicalPartition);
                    Assert.Equal((byte)0, xFlash.LegacyRegion);
                    Assert.Null(xFlash.XmlRegion);

                    protocolBox.SelectedIndex = 1;
                    var legacy = view.BuildMtkFamilyPlan(false, [boot], true)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(MtkDaProtocol.Legacy, legacy.Family);
                    var legacyStep = Assert.Single(legacy.LegacySteps);
                    Assert.Equal("boot", legacyStep.PartitionName);
                    Assert.Equal(1024, legacyStep.Length);
                    Assert.True(legacyStep.PadSourceTail);
                    Assert.Empty(legacy.ScatterSteps);
                    Assert.Empty(legacy.XmlSteps);
                    Assert.Equal((byte)8, legacy.LegacyRegion);
                    Assert.Null(legacy.XmlRegion);

                    protocolBox.SelectedIndex = 2;
                    var xml = view.BuildMtkFamilyPlan(false, [boot], true)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(MtkDaProtocol.Xml, xml.Family);
                    var xmlStep = Assert.Single(xml.XmlSteps);
                    Assert.Equal("boot", xmlStep.PartitionName);
                    Assert.Equal(131072, xmlStep.Length);
                    Assert.True(xmlStep.PadSourceTail);
                    Assert.Empty(xml.ScatterSteps);
                    Assert.Empty(xml.LegacySteps);
                    Assert.Equal((byte)0, xml.LegacyRegion);
                    Assert.Equal("EMMC-USER", xml.XmlRegion);

                    protocolBox.SelectedIndex = 0;
                    var xFlashErase = view.BuildMtkFamilyPlan(true, [boot, userdata], false)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(2, xFlashErase.XFlashEraseSteps.Count);
                    Assert.All(xFlashErase.XFlashEraseSteps, step =>
                        Assert.Equal(MtkFlashStepKind.Erase, step.Kind));
                    Assert.Empty(xFlashErase.ScatterSteps);

                    protocolBox.SelectedIndex = 1;
                    var legacyErase = view.BuildMtkFamilyPlan(true, [userdata], false)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(65536, Assert.Single(legacyErase.LegacySteps).Length);

                    protocolBox.SelectedIndex = 2;
                    var xmlErase = view.BuildMtkFamilyPlan(true, [userdata], false)
                        ?? throw new InvalidOperationException(model.LogText);
                    Assert.Equal(131072, Assert.Single(xmlErase.XmlSteps).Length);
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}