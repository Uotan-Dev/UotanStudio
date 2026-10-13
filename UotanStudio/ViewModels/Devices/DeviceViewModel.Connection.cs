using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Views.Devices;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
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
        _ => (DeviceProtocolKind)(-1)
    };

    public bool HasInvalidRemoteAddress => !string.IsNullOrEmpty(RemoteAddress) &&
        !DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out _);

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
        DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out _);

    private void NotifyRemoteAddressChanged()
    {
        OnPropertyChanged(nameof(HasInvalidRemoteAddress));
        ConnectRemoteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConnectRemote))]
    private async Task ConnectRemoteAsync()
    {
        if (!CanConnectRemote() ||
            !DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out var address))
        {
            return;
        }
        await ChangeNetworkConnectionAsync(RemoteProtocol, address, null);
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
        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }
        IsConnectionBusy = true;
        try
        {
            await new QrPairingWindow().ShowDialog(owner);
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
        await ChangeNetworkConnectionAsync(device.Protocol, device.Serial, null);
    }

    [RelayCommand(CanExecute = nameof(CanManageSelectedConnection))]
    private async Task DisconnectAsync()
    {
        if (!CanManageSelectedConnection || SelectedDevice is not { } device)
        {
            return;
        }
        await ChangeNetworkConnectionAsync(device.Protocol, device.Serial, device.Device);
    }

    [RelayCommand(CanExecute = nameof(IsConnectionBusy))]
    private void CancelConnection() => _connectionCancellation?.Cancel();

    private static async Task ExecuteNetworkConnectionAsync(DeviceProtocolKind protocol, string address,
        DetectedDevice? disconnect, CancellationToken cancellationToken)
    {
        if (disconnect is null)
        {
            await App.DeviceMonitor.ConnectNetworkDeviceAsync(protocol, address, cancellationToken);
        }
        else if (protocol == DeviceProtocolKind.Hdc)
        {
            await App.DeviceMonitor.HdcDisconnectAsync(disconnect, cancellationToken);
        }
        else
        {
            await App.DeviceMonitor.DisconnectAdbAsync(disconnect, cancellationToken);
        }
    }

    private async Task ChangeNetworkConnectionAsync(DeviceProtocolKind protocol, string address, DetectedDevice? disconnect)
    {
        using var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;
        IsConnectionBusy = true;
        var operation = disconnect is null ? "Connect" : "Disconnect";
        ConnectionStatusText = string.Format(LanguageService.Instance.GetString(
            disconnect is null ? "Device_RemoteConnecting" : "Device_RemoteDisconnecting"), address);
        try
        {
            await _changeNetworkConnection(protocol, address, disconnect, cancellation.Token);
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
                    endpoint => endpoint.Protocol == protocol && endpoint.Serial == address));
                if (connected is not null)
                {
                    connected.SelectedConnection = connected.Connections.First(
                        endpoint => endpoint.Protocol == protocol && endpoint.Serial == address);
                    SelectedDevice = connected;
                }
            }
            ConnectionStatusText = string.Format(LanguageService.Instance.GetString(
                disconnect is null ? "Device_RemoteConnected" : "Device_RemoteDisconnected"), address);
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"{operation} {protocol} {address}: OK");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConnectionStatusText = LanguageService.Instance.GetString("Device_RemoteCanceled");
        }
        catch (Exception ex)
        {
            ConnectionStatusText = string.Format(LanguageService.Instance.GetString("Device_RemoteFailed"), address);
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"{operation} {protocol} {address}: {ex.Message}");
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
            "system" => LanguageService.Instance.GetString("Device_ModeSystem"),
            "bootloader" => LanguageService.Instance.GetString("Device_ModeBootloader"),
            "fastbootd" => LanguageService.Instance.GetString("Device_ModeFastbootd"),
            "recovery" => LanguageService.Instance.GetString("Device_ModeRecovery"),
            "edl" => LanguageService.Instance.GetString("Device_ModeEdl"),
            _ => mode
        };

        var owner = DialogService.GetOwnerWindow();
        if (owner is null) return;
        var confirmed = await DialogService.ConfirmAsync(owner,
            LanguageService.Instance.GetString("Device_RebootConfirmTitle"),
            string.Format(LanguageService.Instance.GetString("Device_RebootConfirmMessage"), modeDisplayName) + $"\n{device.Serial}");
        if (!confirmed)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode}...";
        try
        {
            var ok = await App.DeviceMonitor.RebootAsync(device.Device, mode).ConfigureAwait(true);
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} OK"
                : $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode}: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} {LanguageService.Instance.GetString("UserError_Failed")}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode} failed: {ex.Message}");
        }
    }

}