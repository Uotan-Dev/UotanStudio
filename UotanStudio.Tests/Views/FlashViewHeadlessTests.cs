using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewHeadlessTests(NodeEditorUiFixture fixture)
{
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
    [InlineData(DeviceProtocolKind.Fastboot, false, false, false, false, true)]
    [InlineData(DeviceProtocolKind.Rkdl, false, false, false, true, true)]
    [InlineData(DeviceProtocolKind.Rkdl, false, false, false, false, false)]
    [InlineData(DeviceProtocolKind.Rkdl, true, false, false, false, false)]
    [InlineData(DeviceProtocolKind.Adb, true, false, false, false, true)]
    [InlineData(DeviceProtocolKind.Adb, false, false, false, false, false)]
    [InlineData(DeviceProtocolKind.Edl, true, true, true, false, true)]
    [InlineData(DeviceProtocolKind.Edl, true, true, false, false, false)]
    [InlineData(DeviceProtocolKind.Edl, true, false, true, false, false)]
    [InlineData(DeviceProtocolKind.Edl, false, true, true, false, false)]
    [InlineData(DeviceProtocolKind.Hdc, true, true, true, true, false)]
    public void FlashModeAllowed_ProtocolMatrix_MatchesTransportCapability(
        DeviceProtocolKind mode, bool flatBin, bool hasEdlEntries, bool hasProgrammer,
        bool hasRockchipParameter, bool expected)
        => Assert.Equal(expected, FlashView.FlashModeAllowed(mode, flatBin, hasEdlEntries, hasProgrammer, hasRockchipParameter));

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
}