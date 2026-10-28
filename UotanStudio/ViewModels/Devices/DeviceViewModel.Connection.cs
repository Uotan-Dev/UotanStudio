using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
    /// <summary>
    /// Window service used by connection commands that open host dialogs; the composition root assigns
    /// the desktop implementation when the shell starts.
    /// <para>连接命令打开宿主对话框时使用的窗口服务；Shell 启动时由组合根赋予桌面实现。</para>
    /// </summary>
    internal static IWindowService Windows { get; set; } = NullWindowService.Instance;

    private CancellationTokenSource? _connectionCancellation;

    public string RemoteAddress
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                NotifyRemoteAddressChanged();
            }
        }
    } = string.Empty;

    public int RemoteProtocolIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                NotifyRemoteAddressChanged();
            }
        }
    }

    private DeviceProtocolKind RemoteProtocol => RemoteProtocolIndex switch
    {
        0 => DeviceProtocolKind.Adb,
        1 => DeviceProtocolKind.Hdc,
        2 => DeviceProtocolKind.Fastboot,
        3 => DeviceProtocolKind.Fastboot,
        _ => (DeviceProtocolKind)(-1)
    };

    private DeviceTransportKind RemoteProtocolTransport => RemoteProtocolIndex == 3
        ? DeviceTransportKind.Udp
        : DeviceTransportKind.Tcp;

    public bool HasInvalidRemoteAddress => !string.IsNullOrEmpty(RemoteAddress) &&
        !DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out _,
            RemoteProtocolTransport);

    public bool IsConnectionBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanManageSelectedConnection));
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();
                ConnectRemoteCommand.NotifyCanExecuteChanged();
                OpenQrPairingCommand.NotifyCanExecuteChanged();
                CancelConnectionCommand.NotifyCanExecuteChanged();
                foreach (var device in Devices)
                {
                    device.NotifyConnectionAvailabilityChanged();
                }
            }
        }
    }

    public string ConnectionStatusText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool CanManageSelectedConnection => !IsConnectionBusy && SelectedDevice?.CanManageConnection == true;

    private bool CanConnectRemote() => !IsConnectionBusy &&
        DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out _,
            RemoteProtocolTransport);

    private void NotifyRemoteAddressChanged()
    {
        OnPropertyChanged(nameof(HasInvalidRemoteAddress));
        ConnectRemoteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConnectRemote))]
    private async Task ConnectRemoteAsync()
    {
        if (!CanConnectRemote() ||
            !DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out var address,
                RemoteProtocolTransport))
        {
            return;
        }
        await ChangeNetworkConnectionAsync(RemoteProtocol, address, null, RemoteProtocolTransport);
    }

    /// <summary>
    /// Opens the QR wireless pairing window; pairing and remote connections are mutually
    /// exclusive, so the entry is disabled while busy.
    /// <para>打开扫码无线配对窗口；配对流程与远程连接互斥，忙碌时禁用入口。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenQrPairing))]
    private async Task OpenQrPairingAsync()
    {
        if (IsConnectionBusy)
        {
            return;
        }
        if (!Windows.HasOwnerWindow)
        {
            return;
        }
        IsConnectionBusy = true;
        try
        {
            await Windows.ShowQrPairingAsync();
        }
        finally
        {
            IsConnectionBusy = false;
        }
    }

    private bool CanOpenQrPairing() => !IsConnectionBusy;

    [RelayCommand(CanExecute = nameof(CanManageSelectedConnection))]
    private async Task ConnectAsync()
    {
        if (!CanManageSelectedConnection || SelectedDevice is not { } device)
        {
            return;
        }
        await ChangeNetworkConnectionAsync(device.Protocol, device.Serial, null, device.Device.Transport);
    }

    [RelayCommand(CanExecute = nameof(CanManageSelectedConnection))]
    private async Task DisconnectAsync()
    {
        if (!CanManageSelectedConnection || SelectedDevice is not { } device)
        {
            return;
        }
        await ChangeNetworkConnectionAsync(device.Protocol, device.Serial, device.Device, device.Device.Transport);
    }

    [RelayCommand(CanExecute = nameof(IsConnectionBusy))]
    private void CancelConnection() => _connectionCancellation?.Cancel();

    private static async Task ExecuteNetworkConnectionAsync(DeviceProtocolKind protocol, string address,
        DetectedDevice? disconnect, DeviceTransportKind transport, CancellationToken cancellationToken)
    {
        if (disconnect is null)
        {
            await App.DeviceMonitor.ConnectNetworkDeviceAsync(protocol, address, cancellationToken, transport);
        }
        else if (protocol == DeviceProtocolKind.Hdc)
        {
            await App.DeviceMonitor.HdcDisconnectAsync(disconnect, cancellationToken);
        }
        else if (protocol == DeviceProtocolKind.Fastboot)
        {
            await App.DeviceMonitor.DisconnectFastbootAsync(disconnect, cancellationToken);
        }
        else
        {
            await App.DeviceMonitor.DisconnectAdbAsync(disconnect, cancellationToken);
        }
    }

    private async Task ChangeNetworkConnectionAsync(DeviceProtocolKind protocol, string address,
        DetectedDevice? disconnect, DeviceTransportKind transport = DeviceTransportKind.Tcp)
    {
        using var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;
        IsConnectionBusy = true;
        var operation = disconnect is null ? "Connect" : "Disconnect";
        ConnectionStatusText = string.Format(_localizer.GetString(
            disconnect is null ? "Device_RemoteConnecting" : "Device_RemoteDisconnecting"), address);
        try
        {
            await _changeNetworkConnection(protocol, address, disconnect, transport, cancellation.Token);
            if (_monitorDevices)
            {
                if (_refreshTask is { IsCompleted: false })
                {
                    await _refreshTask.WaitAsync(cancellation.Token);
                }
                await App.DeviceMonitor.ForceRefreshAsync();
                await RefreshDevicesAsync().WaitAsync(cancellation.Token);
            }
            if (disconnect is null)
            {
                var connected = Devices.FirstOrDefault(entry => entry.Connections.Any(
                    endpoint => endpoint.Protocol == protocol && endpoint.Serial == address &&
                        endpoint.Transport == transport));
                if (connected is not null)
                {
                    connected.SelectedConnection = connected.Connections.First(
                        endpoint => endpoint.Protocol == protocol && endpoint.Serial == address &&
                            endpoint.Transport == transport);
                    SelectedDevice = connected;
                }
            }
            ConnectionStatusText = string.Format(_localizer.GetString(
                disconnect is null ? "Device_RemoteConnected" : "Device_RemoteDisconnected"), address);
            _log.Write(LogLevel.Info, "DeviceManager", $"{operation} {protocol} {address}: OK");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConnectionStatusText = _localizer.GetString("Device_RemoteCanceled");
        }
        catch (Exception ex)
        {
            ConnectionStatusText = string.Format(_localizer.GetString("Device_RemoteFailed"), address);
            _log.Write(LogLevel.Error, "DeviceManager", $"{operation} {protocol} {address}: {ex.Message}");
        }
        finally
        {
            _connectionCancellation = null;
            IsConnectionBusy = false;
            if (_monitorDevices)
            {
                await App.DeviceMonitor.ForceRefreshAsync();
            }
        }
    }
    /// <summary>
    /// Whether the selected device can reboot into fastbootd: ADB uses <c>reboot fastboot</c>,
    /// Fastboot uses <c>reboot-fastboot</c>, while EDL/RKDL/HDC have no such mode.
    /// <para>是否可重启选中设备到 fastbootd。ADB 走 <c>reboot fastboot</c>，Fastboot 走
    /// <c>reboot-fastboot</c>；EDL/RKDL/HDC 没有该模式。</para>
    /// </summary>
    public bool CanRebootToFastbootd => SelectedDevice?.Protocol
        is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot;

    /// <summary>
    /// Asynchronously reboots the selected device to the specified mode
    /// (system/bootloader/fastbootd/recovery/edl).
    /// <para>异步重启选中设备到指定模式（system/bootloader/fastbootd/recovery/edl）。</para>
    /// </summary>
    /// <param name="mode">重启模式（system/bootloader/fastbootd/recovery/edl）。</param>
    [RelayCommand]
    private async Task RebootAsync(string? mode)
    {
        var device = SelectedDevice;
        if (device == null || string.IsNullOrEmpty(mode))
        {
            return;
        }

        var modeDisplayName = mode switch
        {
            "system" => _localizer.GetString("Device_ModeSystem"),
            "bootloader" => _localizer.GetString("Device_ModeBootloader"),
            "fastbootd" => _localizer.GetString("Device_ModeFastbootd"),
            "recovery" => _localizer.GetString("Device_ModeRecovery"),
            "edl" => _localizer.GetString("Device_ModeEdl"),
            _ => mode
        };

        var owner = DialogService.GetOwnerWindow();
        if (owner is null) return;
        var confirmed = await DialogService.ConfirmAsync(owner,
            _localizer.GetString("Device_RebootConfirmTitle"),
            string.Format(_localizer.GetString("Device_RebootConfirmMessage"), modeDisplayName) + $"\n{device.Serial}");
        if (!confirmed)
        {
            return;
        }

        StatusText = $"{_localizer.GetString("Device_Reboot")} {device.Serial} -> {mode}...";
        try
        {
            var ok = await App.DeviceMonitor.RebootAsync(device.Device, mode).ConfigureAwait(true);
            StatusText = ok
                ? $"{_localizer.GetString("Device_Reboot")} {device.Serial} -> {mode} OK"
                : $"{_localizer.GetString("Device_Reboot")} {device.Serial} -> {mode} FAIL";
            _log.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode}: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Device_Reboot")} {device.Serial} -> {mode} {_localizer.GetString("UserError_Failed")}";
            _log.Write(LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode} failed: {ex.Message}");
        }
    }

}