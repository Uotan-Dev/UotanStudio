using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Routing;
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

    /// <summary>
    /// Gets or sets the manually entered ADB pairing code; when it is present, the remote address is the
    /// pairing <c>ip:port</c> shown by the phone and the connect command pairs before connecting.
    /// <para>获取或设置手动输入的 ADB 配对码；填写后，远程地址即手机显示的配对 <c>ip:port</c>，
    /// 连接命令会先完成配对再连接。</para>
    /// </summary>
    public string RemotePairingCode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasPairingCode));
                NotifyRemoteAddressChanged();
            }
        }
    } = string.Empty;

    /// <summary>
    /// Gets whether the remote panel will pair with the entered code before connecting.
    /// <para>获取远程面板是否会在连接前使用已输入的配对码完成配对。</para>
    /// </summary>
    public bool HasPairingCode => IsRemoteAdb && !string.IsNullOrWhiteSpace(RemotePairingCode);

    /// <summary>
    /// Gets whether the selected remote protocol supports ADB pairing controls.
    /// <para>获取当前远程协议是否支持 ADB 配对控件。</para>
    /// </summary>
    public bool IsRemoteAdb => RemoteProtocolIndex == 0;

    public int RemoteProtocolIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                NotifyRemoteAddressChanged();
                OnPropertyChanged(nameof(IsRemoteAdb));
                OnPropertyChanged(nameof(HasPairingCode));
                OpenQrPairingCommand.NotifyCanExecuteChanged();
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
        (!DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out _,
             RemoteProtocolTransport) ||
         (HasPairingCode && !DeviceMonitorService.TryNormalizePairingEndpoint(RemoteAddress, out _, out _)));

    public bool IsConnectionBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanManageSelectedConnection));
                OnPropertyChanged(nameof(IsDeviceOperationBusy));
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

    /// <summary>
    /// Gets whether device discovery or a connection operation is active.
    /// <para>获取设备扫描或连接操作是否正在进行。</para>
    /// </summary>
    public bool IsDeviceOperationBusy => IsRefreshing || IsConnectionBusy;

    public bool CanManageSelectedConnection => !IsConnectionBusy && SelectedDevice?.CanManageConnection == true;

    private bool CanConnectRemote() => !IsConnectionBusy && !string.IsNullOrWhiteSpace(RemoteAddress);

    private void NotifyRemoteAddressChanged()
    {
        OnPropertyChanged(nameof(HasInvalidRemoteAddress));
        ConnectRemoteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConnectRemote))]
    private async Task ConnectRemoteAsync()
    {
        if (!CanConnectRemote())
        {
            return;
        }
        if (!DeviceMonitorService.TryNormalizeNetworkAddress(RemoteProtocol, RemoteAddress, out var address,
                RemoteProtocolTransport) ||
            (HasPairingCode && !DeviceMonitorService.TryNormalizePairingEndpoint(RemoteAddress, out _, out _)))
        {
            await _showDeviceError(_localizer.GetString("Device_RemoteInvalidAddress"));
            return;
        }
        await ChangeNetworkConnectionAsync(RemoteProtocol, address, null, RemoteProtocolTransport,
            RemotePairingCode);
    }

    /// <summary>
    /// Opens the QR wireless pairing window; pairing and remote connections are mutually
    /// exclusive, so the entry is disabled while busy.
    /// <para>打开扫码无线配对窗口；配对流程与远程连接互斥，忙碌时禁用入口。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenQrPairing))]
    private async Task OpenQrPairingAsync()
    {
        if (!CanOpenQrPairing())
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
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "DeviceManager", $"Open QR pairing failed: {ex.Message}");
            await _showDeviceError(UserError.Describe(ex));
        }
        finally
        {
            IsConnectionBusy = false;
        }
    }

    private bool CanOpenQrPairing() => IsRemoteAdb && !IsConnectionBusy;

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
        DetectedDevice? disconnect, DeviceTransportKind transport = DeviceTransportKind.Tcp,
        string? pairingCode = null)
    {
        var pairing = disconnect is null && protocol == DeviceProtocolKind.Adb &&
            !string.IsNullOrWhiteSpace(pairingCode);
        using var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;
        IsConnectionBusy = true;
        var operation = pairing ? "Pair" : disconnect is null ? "Connect" : "Disconnect";
        try
        {
            var connectedAddress = address;
            if (pairing)
            {
                connectedAddress = await _pairWithCode(address, pairingCode!, null, cancellation.Token)
                    .ConfigureAwait(true);
                RemoteAddress = connectedAddress;
                RemotePairingCode = string.Empty;
            }
            else
            {
                await _changeNetworkConnection(protocol, address, disconnect, transport, cancellation.Token);
            }
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
                    endpoint => endpoint.Protocol == protocol && endpoint.Serial == connectedAddress &&
                        endpoint.Transport == transport));
                if (connected is not null)
                {
                    connected.SelectedConnection = connected.Connections.First(
                        endpoint => endpoint.Protocol == protocol && endpoint.Serial == connectedAddress &&
                            endpoint.Transport == transport);
                    SelectedDevice = connected;
                }
            }
            _log.Write(LogLevel.Info, "DeviceManager", $"{operation} {protocol} {connectedAddress}: OK");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _log.Write(LogLevel.Info, "DeviceManager", $"{operation} {protocol} {address}: canceled");
        }
        catch (AdbQrPairingConnectPortException ex) when (pairing)
        {
            RemotePairingCode = string.Empty;
            if (ex.SuggestedPort != 0)
            {
                RemoteAddress = DebugBridgeNetworkAddress.FormatEndpoint(ex.Host, ex.SuggestedPort);
            }
            await _showDeviceError(string.Format(_localizer.GetString("Device_RemotePairingPortNeeded"), address));
            _log.Write(LogLevel.Warn, "DeviceManager",
                $"{operation} {protocol} {address}: paired, but no secure connect port was confirmed: {ex.Message}");
        }
        catch (Exception ex)
        {
            await _showDeviceError(pairing
                ? string.Format(_localizer.GetString("Device_RemotePairingFailed"), address, UserError.Describe(ex))
                : string.Format(_localizer.GetString("Device_RemoteFailed"), address) + "\n" + UserError.Describe(ex));
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

    private async Task ShowDeviceErrorAsync(string message)
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is not null)
        {
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_DeviceManager"), message,
                Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private static async Task<string> DefaultPairWithCodeAsync(string address, string pairingCode,
        IProgress<WirelessPairingProgress>? progress, CancellationToken cancellationToken)
    {
        var outcome = await App.DeviceMonitor.PairWithCodeAsync(address, pairingCode, progress, cancellationToken)
            .ConfigureAwait(true);
        return outcome.Address;
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
    /// Whether the selected device can reboot into the given quick-reboot mode. RKDL devices only
    /// support the plain DEVICE_RESET reboot to system; the other modes require ADB/Fastboot/HDC.
    /// <para>选中设备能否重启到指定快捷重启模式。RKDL 设备仅支持经 DEVICE_RESET 重启到系统；
    /// 其余模式需要 ADB/Fastboot/HDC。</para>
    /// </summary>
    /// <param name="mode">The quick-reboot mode (system/bootloader/fastbootd/recovery/edl).<para>快捷重启模式（system/bootloader/fastbootd/recovery/edl）。</para></param>
    public bool CanRebootTo(string? mode) => mode switch
    {
        "system" => SelectedDevice is not null,
        "bootloader" or "recovery" or "edl" => SelectedDevice?.Protocol
            is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot or DeviceProtocolKind.Hdc,
        _ => false,
    };

    public bool CanRebootToSystem => CanRebootTo("system");
    public bool CanRebootToBootloader => CanRebootTo("bootloader");
    public bool CanRebootToRecovery => CanRebootTo("recovery");
    public bool CanRebootToEdl => CanRebootTo("edl");

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
            if (!ok)
            {
                await _showDeviceError(StatusText);
            }
            _log.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode}: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Device_Reboot")} {device.Serial} -> {mode} {_localizer.GetString("UserError_Failed")}";
            await _showDeviceError(StatusText + "\n" + UserError.Describe(ex));
            _log.Write(LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode} failed: {ex.Message}");
        }
    }

}