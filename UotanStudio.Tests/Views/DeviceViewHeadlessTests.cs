using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Converters;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using UotanStudio.Views.Devices;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class DeviceViewHeadlessTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData("en-US", "Unknown")]
    [InlineData("zh-CN", "未知")]
    public async Task EdlProtocol_Changes_UpdatesVisibleState(string language, string unknown)
    {
        await DispatchAsync(() =>
        {
            using var model = new DeviceViewModel(monitorDevices: false);
            var previousLanguage = LanguageService.Instance.CurrentLanguage;
            LanguageService.Instance.ApplyLanguage(language);
            var info = new DeviceInfo(Endpoint(DeviceProtocolKind.Edl, DeviceTransportKind.Usb, "usb:edl"), model);
            model.Devices.Add(info);
            model.SelectedDevice = info;
            var view = CreateView(model);
            var window = new Window { Content = view, Width = 960, Height = 800 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == unknown && text.IsEffectivelyVisible);
                foreach (var state in new[] { EdlProtocolState.Sahara, EdlProtocolState.Firehose })
                {
                    info.EdlProtocol = state;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == state.ToString() && text.IsEffectivelyVisible);
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
                        text => text.Text == unknown && text.IsEffectivelyVisible);
                    Assert.False(model.ConnectCommand.CanExecute(null));
                    Assert.False(model.DisconnectCommand.CanExecute(null));
                }
            }
            finally
            {
                window.Close();
                LanguageService.Instance.ApplyLanguage(previousLanguage);
            }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(DeviceProtocolKind.Adb, "adb:usb", "ADB server")]
    [InlineData(DeviceProtocolKind.Hdc, "hdc:usb", "HDC server")]
    [InlineData(DeviceProtocolKind.Adb, "usb:direct", "ADB")]
    [InlineData(DeviceProtocolKind.Hdc, "usb:direct", "HDC")]
    public async Task ConnectionCommands_UsbSelected_DisablesToolbarAndContextMenu(
        DeviceProtocolKind protocol, string key, string label)
    {
        await DispatchAsync(async () =>
        {
            using var model = new DeviceViewModel(monitorDevices: false);
            var endpoint = Endpoint(protocol, DeviceTransportKind.Usb, key);
            var device = new DeviceInfo(endpoint, model);
            model.Devices.Add(device);
            model.SelectedDevice = device;
            var view = CreateView(model);
            var window = new Window { Content = view, Width = 960, Height = 720 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.False(model.ConnectCommand.CanExecute(null));
                Assert.False(model.DisconnectCommand.CanExecute(null));
                Assert.Null(view.FindControl<Button>("ConnectDeviceButton"));
                Assert.False(view.FindControl<Button>("DisconnectDeviceButton")!.IsEffectivelyEnabled);
                var entry = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                    border => border.Classes.Contains("DeviceEntryCard"));
                var menu = Assert.IsType<ContextMenu>(entry.ContextMenu);
                menu.Open(entry);
                Dispatcher.UIThread.RunJobs();
                Assert.False(Assert.IsType<MenuItem>(menu.Items[0]).IsEffectivelyEnabled);
                Assert.False(Assert.IsType<MenuItem>(menu.Items[1]).IsEffectivelyEnabled);
                menu.Close();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == label);

                await model.ConnectCommand.ExecuteAsync(null);
                await model.DisconnectCommand.ExecuteAsync(null);

                Assert.Equal("device", endpoint.State);
                Assert.Empty(model.ConnectionStatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SelectedConnection_ChangesPhysicalTransport_UpdatesCommandAvailability()
    {
        await DispatchAsync(() =>
        {
            using var model = new DeviceViewModel(monitorDevices: false);
            var usb = Endpoint(DeviceProtocolKind.Adb, DeviceTransportKind.Usb, "adb:usb");
            var tcp = Endpoint(DeviceProtocolKind.Hdc, DeviceTransportKind.Tcp, "hdc:192.0.2.1:8710");
            var info = new DeviceInfo([usb, tcp], model);
            model.Devices.Add(info);
            model.SelectedDevice = info;
            var changes = 0;
            model.ConnectCommand.CanExecuteChanged += (_, _) => changes++;

            Assert.False(model.ConnectCommand.CanExecute(null));
            info.SelectedConnection = tcp;

            Assert.True(model.ConnectCommand.CanExecute(null));
            Assert.True(model.DisconnectCommand.CanExecute(null));
            Assert.Equal("HDC server", info.Mode);
            info.SelectedConnection = usb;
            Assert.False(model.ConnectCommand.CanExecute(null));
            Assert.False(model.DisconnectCommand.CanExecute(null));
            Assert.Equal(2, changes);
            model.SelectedDevice = null;
            Assert.False(model.ConnectCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(960, "en-US")]
    [InlineData(1280, "zh-CN")]
    [InlineData(900, "en-US")]
    [InlineData(880, "zh-CN")]
    [InlineData(840, "en-US")]
    [InlineData(800, "zh-CN")]
    [InlineData(720, "zh-CN")]
    [InlineData(560, "zh-CN")]
    public async Task RemoteForm_NoDevices_ValidatesAddressAndFitsViewport(int width, string language)
    {
        await DispatchAsync(() =>
        {
            using var model = new DeviceViewModel(monitorDevices: false);
            var previousLanguage = LanguageService.Instance.CurrentLanguage;
            LanguageService.Instance.ApplyLanguage(language);
            var view = CreateView(model);
            var window = new Window { Content = view, Width = width, Height = 800 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var remote = view.FindControl<Button>("ConnectRemoteButton")!;
                var address = view.FindControl<TextBox>("RemoteAddressBox")!;
                Assert.True(view.FindControl<Border>("RemoteConnectionPanel")!.IsVisible);
                Assert.False(remote.IsEffectivelyEnabled);
                Assert.False(model.ConnectCommand.CanExecute(null));
                Assert.False(model.HasInvalidRemoteAddress);

                address.Text = "192.0.2.1:5555";
                Dispatcher.UIThread.RunJobs();
                Assert.True(remote.IsEffectivelyEnabled);
                Assert.Equal("192.0.2.1:5555", model.RemoteAddress);
                address.Text = "192.0.2.1:65536";
                Dispatcher.UIThread.RunJobs();
                Assert.False(remote.IsEffectivelyEnabled);
                Assert.True(view.FindControl<TextBlock>("RemoteAddressError")!.IsVisible);
                address.Text = "long-device-hostname.example:5555";
                Assert.True(model.ConnectRemoteCommand.CanExecute(null));
                view.FindControl<ComboBox>("RemoteProtocolBox")!.SelectedIndex = 1;
                Assert.False(model.ConnectRemoteCommand.CanExecute(null));
                address.Text = "192.0.2.1:8710";
                Assert.True(model.ConnectRemoteCommand.CanExecute(null));
                window.UpdateLayout();

                var addressOrigin = address.TranslatePoint(default, view)!.Value;
                var buttonOrigin = remote.TranslatePoint(default, view)!.Value;
                Assert.True(address.Bounds.Width > 100);
                Assert.True(addressOrigin.X + address.Bounds.Width <= buttonOrigin.X);
                var panel = view.FindControl<Border>("RemoteConnectionPanel")!;
                var actions = view.FindControl<StackPanel>("DeviceActionsPanel")!;
                var panelOrigin = panel.TranslatePoint(default, view)!.Value;
                var actionsOrigin = actions.TranslatePoint(default, view)!.Value;
                Assert.Equal(0, Grid.GetRow(panel));
                Assert.Equal(1, Grid.GetColumn(panel));
                Assert.InRange(panel.Bounds.Width, 312, 400);
                Assert.Equal(actionsOrigin.Y, panelOrigin.Y);
                Assert.True(panelOrigin.X >= actionsOrigin.X + actions.Bounds.Width);
                Assert.True(actions.Bounds.Height <= 33);
                var toolbar = view.FindControl<Grid>("DeviceToolbarGrid")!;
                var scroll = view.FindControl<ScrollViewer>("DeviceToolbarScrollViewer")!;
                Assert.True(toolbar.Bounds.Width >= scroll.Bounds.Width);
                if (width >= 840)
                {
                    Assert.True(panelOrigin.X + panel.Bounds.Width <= view.Bounds.Width - 19);
                    Assert.True(panelOrigin.X + panel.Bounds.Width >= view.Bounds.Width - 21);
                }
                Assert.Null(view.FindControl<Button>("ConnectDeviceButton"));
                var main = view.FindControl<Grid>("DeviceContentGrid")!;
                Assert.Equal(width < 720 ? 1 : 2, main.ColumnDefinitions.Count);
                Assert.Equal(width < 720 ? 1 : 0, Grid.GetRow(view.FindControl<Grid>("DeviceDetailPanel")!));
            }
            finally
            {
                window.Close();
                LanguageService.Instance.ApplyLanguage(previousLanguage);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Toolbar_ResizedAndDeviceChanged_StaysOnOneRowWithoutLosingAddress()
    {
        await DispatchAsync(() =>
        {
            using var model = new DeviceViewModel(monitorDevices: false);
            model.RemoteAddress = "192.0.2.1:5555";
            var view = CreateView(model);
            var window = new Window { Content = view, Width = 880, Height = 800 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var panel = view.FindControl<Border>("RemoteConnectionPanel")!;
                var actions = view.FindControl<StackPanel>("DeviceActionsPanel")!;
                Assert.Equal(0, Grid.GetRow(panel));
                foreach (var width in new[] { 560, 840, 880, 1280, 720, 960 })
                {
                    window.Width = width;
                    foreach (var protocol in new[] { DeviceProtocolKind.Hdc, DeviceProtocolKind.Adb })
                    {
                        model.SelectedDevice = new DeviceInfo(Endpoint(protocol, DeviceTransportKind.Usb, "usb:device"), model);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        var panelOrigin = panel.TranslatePoint(default, view)!.Value;
                        var actionsOrigin = actions.TranslatePoint(default, view)!.Value;
                        Assert.Equal(0, Grid.GetRow(panel));
                        Assert.Equal(actionsOrigin.Y, panelOrigin.Y);
                        Assert.True(actionsOrigin.X + actions.Bounds.Width <= panelOrigin.X);
                        Assert.True(actions.Bounds.Height <= 33);
                        Assert.InRange(panel.Bounds.Width, 312, 400);
                        Assert.Equal("192.0.2.1:5555", model.RemoteAddress);
                    }
                }
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ConnectRemoteCommand_CanceledOperation_DisablesConflictingCommandsAndRestoresForm()
    {
        await DispatchAsync(async () =>
        {
            var invoked = 0;
            using var model = new DeviceViewModel(monitorDevices: false, async (protocol, address, disconnect, token) =>
            {
                invoked++;
                Assert.Equal(DeviceProtocolKind.Adb, protocol);
                Assert.Equal("192.0.2.1:5555", address);
                Assert.Null(disconnect);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            var endpoint = Endpoint(DeviceProtocolKind.Adb, DeviceTransportKind.Tcp, "adb:network");
            var info = new DeviceInfo(endpoint, model);
            model.Devices.Add(info);
            model.SelectedDevice = info;
            model.RemoteAddress = "192.0.2.1:5555";
            var view = CreateView(model);
            var window = new Window { Content = view, Width = 960, Height = 720 };
            try
            {
                window.Show();
                var pending = model.ConnectRemoteCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                Assert.True(model.IsConnectionBusy);
                Assert.False(info.CanManageConnection);
                Assert.False(model.ConnectCommand.CanExecute(null));
                Assert.False(model.DisconnectCommand.CanExecute(null));
                Assert.False(model.ConnectRemoteCommand.CanExecute(null));
                Assert.True(model.CancelConnectionCommand.CanExecute(null));
                Assert.False(view.FindControl<TextBox>("RemoteAddressBox")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<ComboBox>("RemoteProtocolBox")!.IsEffectivelyEnabled);
                await model.DisconnectCommand.ExecuteAsync(null);
                Assert.Equal(1, invoked);

                model.CancelConnectionCommand.Execute(null);
                await pending;

                Assert.False(model.IsConnectionBusy);
                Assert.True(info.CanManageConnection);
                Assert.True(model.ConnectRemoteCommand.CanExecute(null));
                Assert.False(model.CancelConnectionCommand.CanExecute(null));
                Assert.Equal("device", endpoint.State);
                Assert.Equal("192.0.2.1:5555", model.RemoteAddress);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DisconnectCommand_ServerFailure_PreservesDeviceStateAndAllowsRetry()
    {
        await DispatchAsync(async () =>
        {
            var previousLanguage = LanguageService.Instance.CurrentLanguage;
            LanguageService.Instance.ApplyLanguage("en-US");
            try
            {
                var endpoint = Endpoint(DeviceProtocolKind.Hdc, DeviceTransportKind.Tcp, "hdc:network");
                using var model = new DeviceViewModel(monitorDevices: false, (protocol, address, disconnect, _) =>
                {
                    Assert.Equal(DeviceProtocolKind.Hdc, protocol);
                    Assert.Equal(endpoint.Serial, address);
                    Assert.Same(endpoint, disconnect);
                    return Task.FromException(new IOException("Rejected by fixture"));
                });
                var info = new DeviceInfo(endpoint, model);
                model.Devices.Add(info);
                model.SelectedDevice = info;

                await model.DisconnectCommand.ExecuteAsync(null);

                Assert.Equal("device", endpoint.State);
                Assert.Same(info, model.SelectedDevice);
                Assert.False(model.IsConnectionBusy);
                Assert.True(model.DisconnectCommand.CanExecute(null));
                Assert.Contains("failed", model.ConnectionStatusText);
                Assert.DoesNotContain("Rejected by fixture", model.ConnectionStatusText);
            }
            finally
            {
                LanguageService.Instance.ApplyLanguage(previousLanguage);
            }
        });
    }

    private static DetectedDevice Endpoint(DeviceProtocolKind protocol, DeviceTransportKind transport, string key) => new()
    {
        Key = key,
        Serial = transport == DeviceTransportKind.Tcp ? "192.0.2.1:8710" : "usb-device",
        Protocol = protocol,
        Transport = transport,
        State = "device"
    };

    private static DeviceView CreateView(DeviceViewModel model)
        => new() { DataContext = model };

    private async Task DispatchAsync(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                var resources = new ResourceDictionary
                {
                    ["StringNotNullOrEmptyConverter"] = new StringNotNullOrEmptyConverter(),
                    ["CardBackground"] = Colors.White,
                    ["BorderForegroundColor"] = Colors.Gray
                };
                Application.Current!.Resources.MergedDictionaries.Add(resources);
                try
                {
                    await action();
                }
                finally
                {
                    Application.Current.Resources.MergedDictionaries.Remove(resources);
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}