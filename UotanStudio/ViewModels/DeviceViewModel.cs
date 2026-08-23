using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>设备视图模型，管理 ADB/Fastboot/EDL/RKDL 设备列表与连接状态。
/// 枚举与协议交互完全基于 UotanStudio.Comm（DeviceMonitorService）。</para>
/// Device view model, managing ADB/Fastboot/EDL/RKDL device lists and connection state.
/// Enumeration and protocol interaction are fully based on UotanStudio.Comm
/// (DeviceMonitorService).
/// </summary>
public partial class DeviceViewModel : ViewModelBase, IDisposable
{

    /// <summary>
    /// <para>获取或设置已检测到的设备列表。</para>
    /// Gets or sets the list of detected devices.
    /// </summary>
    public ObservableCollection<DeviceInfo> Devices
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>
    /// <para>获取或设置当前选中的设备。</para>
    /// Gets or sets the currently selected device.
    /// </summary>
    public DeviceInfo? SelectedDevice
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DeviceDetail));
            }
        }
    }

    /// <summary>
    /// <para>获取或设置是否正在刷新设备列表。</para>
    /// Gets or sets whether the device list is currently refreshing.
    /// </summary>
    public bool IsRefreshing
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取或设置状态文本。</para>
    /// Gets or sets the status text.
    /// </summary>
    public string StatusText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// <para>获取是否没有任何设备（用于显示空状态提示）。</para>
    /// Gets whether there are no devices (used to show the empty-state hint).
    /// </summary>
    public bool HasNoDevices => !IsRefreshing && Devices.Count == 0;

    /// <summary>
    /// <para>获取选中设备的详情文本。</para>
    /// Gets the detail text of the selected device.
    /// </summary>
    public string DeviceDetail => SelectedDevice is null
        ? string.Empty
        : $"Serial: {SelectedDevice.Serial}\nMode: {SelectedDevice.Mode}\nState: {SelectedDevice.State}\nTransport: {SelectedDevice.Transport}\nModel: {SelectedDevice.Model}\nAndroid: {SelectedDevice.AndroidVersion}";

    /// <summary>
    /// <para>初始化设备视图模型：启动设备监视服务（含启动全量 USB 扫描与热插拔监听），
    /// 订阅设备变更事件并执行首次刷新。</para>
    /// Initializes the device view model: starts the device monitor service (which performs
    /// the startup full USB scan and begins hotplug monitoring), subscribes to device
    /// changes and performs the first refresh.
    /// </summary>
    public DeviceViewModel()
    {
        DeviceMonitorService.Instance.DevicesChanged += OnDevicesChanged;
        DeviceMonitorService.Instance.Start();
        _ = RefreshDevicesAsync();
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        // 热插拔/adb server 变更触发的增量刷新：保留当前选择。
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshDevicesAsync());

    /// <summary>
    /// <para>刷新设备列表：调用设备监视服务执行全量扫描并按协议分组；
    /// 随后并发拉取 ADB 设备属性丰富详情。</para>
    /// Refreshes the device list: invokes the monitor service for a full scan, groups by
    /// protocol, then concurrently pulls ADB device properties to enrich details.
    /// </summary>
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        StatusText = LanguageService.Instance.GetString("Device_Scanning");
        OnPropertyChanged(nameof(HasNoDevices));
        var selectedKey = SelectedDevice?.Key;
        try
        {
            var scanned = await Task.Run(() => DeviceMonitorService.Instance.Scan()).ConfigureAwait(true);

            Devices.Clear();
            foreach (var d in scanned)
            {
                Devices.Add(new DeviceInfo(d));
            }

            if (selectedKey is not null)
            {
                SelectedDevice = Devices.FirstOrDefault(x => x.Key == selectedKey);
            }

            StatusText = Devices.Count == 0
                ? LanguageService.Instance.GetString("Device_NoDevices")
                : $"{Devices.Count}{LanguageService.Instance.GetString("Device_DeviceCount")}";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager",
                $"Scan completed: {Devices.Count} device(s) found.");

            var adbDevices = Devices.Where(x => x.Protocol == DeviceProtocolKind.Adb && x.Transport == "USB").ToList();
            if (adbDevices.Count > 0)
            {
                await Task.WhenAll(adbDevices.Select(EnrichDeviceInfoAsync));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = LanguageService.Instance.GetString("Device_NoDevices");
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(HasNoDevices));
        }
    }

    /// <summary>
    /// <para>手动强制刷新：强制重新扫描（绕过热插拔去抖）。</para>
    /// Manual force refresh: forces a rescan (bypassing the hotplug debounce).
    /// </summary>
    [RelayCommand]
    private async Task ForceRefreshAsync()
    {
        await DeviceMonitorService.Instance.ForceRefreshAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// <para>通过 UotanStudio.Comm 的 ADB 会话拉取设备 getprop 属性填充 Model/AndroidVersion 字段。</para>
    /// Retrieves device getprop properties via a UotanStudio.Comm ADB session to populate
    /// Model/AndroidVersion fields.
    /// </summary>
    private async Task EnrichDeviceInfoAsync(DeviceInfo device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
        {
            return;
        }

        try
        {
            // 打开 USB 设备与 getprop 均为同步阻塞调用（未授权设备可能挂到超时），
            // 整体放后台线程，避免扫描结束过渡到主窗时冻结 UI。
            // Opening the USB device and getprop are blocking calls (unauthorized devices
            // can stall until timeout); run them on a background thread so the transition
            // from splash to main window never freezes the UI.
            var (model, version) = await Task.Run(() =>
            {
                var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device);
                if (session is null)
                {
                    return (string.Empty, string.Empty);
                }

                try
                {
                    session.Connect();
                    var result = session.ExecuteCommand("getprop ro.product.model; getprop ro.build.version.release");
                    var lines = System.Text.Encoding.UTF8.GetString(result.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    return (lines.Length > 0 ? lines[0].Trim() : string.Empty,
                            lines.Length > 1 ? lines[1].Trim() : string.Empty);
                }
                finally
                {
                    session.Dispose();
                }
            }).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(model))
            {
                device.Model = model;
            }

            if (!string.IsNullOrEmpty(version))
            {
                device.AndroidVersion = version;
            }

            if (SelectedDevice == device)
            {
                OnPropertyChanged(nameof(DeviceDetail));
            }
        }
        catch
        {
            // 属性拉取失败不影响主流程（设备可能未授权）。
        }
    }
    /// <summary>
    /// <para>连接到选中的设备（验证协议可达性）。</para>
    /// Connects to the selected device (verifies protocol reachability).
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedDevice == null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Connecting")} {SelectedDevice.Serial}";
        try
        {
            var ok = await Task.Run(() => SelectedDevice.Protocol switch
            {
                DeviceProtocolKind.Fastboot => TryFastboot(SelectedDevice),
                DeviceProtocolKind.Edl => true, // EDL 握手需要 programmer，此处仅确认设备在位
                DeviceProtocolKind.Rkdl => TryRkdl(SelectedDevice),
                _ => TryAdb(SelectedDevice),
            }).ConfigureAwait(true);

            SelectedDevice.State = ok ? SelectedDevice.State : "offline";
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} OK"
                : $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Connect {SelectedDevice.Serial} ({SelectedDevice.Mode}): {(ok ? "OK" : "FAIL")}");
            OnPropertyChanged(nameof(DeviceDetail));
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Connect {SelectedDevice.Serial} failed: {ex.Message}");
        }
    }

    private static bool TryAdb(DeviceInfo device)
    {
        var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device);
        if (session is null)
        {
            return false;
        }

        try
        {
            session.Connect();
            return session.Connection.IsConnected;
        }
        catch { return false; }
        finally { session.Dispose(); }
    }

    private static bool TryFastboot(DeviceInfo device)
    {
        var session = DeviceMonitorService.Instance.OpenFastbootSession(device.Device);
        if (session is null)
        {
            return false;
        }

        try
        {
            var product = session.GetVariable("product", useCache: false);
            if (!string.IsNullOrEmpty(product))
            {
                device.Model = product;
            }

            return true;
        }
        catch { return false; }
        finally { session.Dispose(); }
    }

    private static bool TryRkdl(DeviceInfo device)
    {
        var session = DeviceMonitorService.Instance.OpenRkdlSession(device.Device);
        if (session is null)
        {
            return false;
        }

        try
        {
            return session.IsRockchipDevice() && session.TestDeviceReady();
        }
        catch { return false; }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// <para>断开选中的设备（USB 直连无需显式断开；网络 ADB 设备断开 adb server 连接）。</para>
    /// Disconnects the selected device (direct USB sessions need no explicit disconnect;
    /// network ADB devices disconnect from the adb server).
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (SelectedDevice == null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial}...";
        try
        {
            if (SelectedDevice.Transport == "Network")
            {
                var client = DeviceMonitorService.Instance.AdbServerClient;
                if (client is not null)
                {
                    await client.DisconnectAsync(SelectedDevice.Serial).ConfigureAwait(true);
                }
            }
            SelectedDevice.State = "offline";
            StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial} OK";
            OnPropertyChanged(nameof(DeviceDetail));
        }
        catch
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial} FAIL";
        }
    }

    /// <summary>
    /// <para>异步重启选中设备到指定模式（system/bootloader/recovery/edl）。</para>
    /// Asynchronously reboots the selected device to the specified mode
    /// (system/bootloader/recovery/edl).
    /// </summary>
    /// <param name="mode">重启模式（system/bootloader/recovery/edl）。</param>
    [RelayCommand]
    private async Task RebootAsync(string? mode)
    {
        if (SelectedDevice == null || string.IsNullOrEmpty(mode))
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode}...";
        try
        {
            var ok = await DeviceMonitorService.Instance.RebootAsync(SelectedDevice.Device, mode).ConfigureAwait(true);
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode} OK"
                : $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode} FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Reboot {SelectedDevice.Serial} -> {mode}: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Reboot {SelectedDevice.Serial} -> {mode} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>取消订阅设备变更事件。</para>
    /// Unsubscribes from device change events.
    /// </summary>
    public void Dispose()
    {
        DeviceMonitorService.Instance.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// <para>设备信息模型（包装 DeviceMonitorService 枚举出的 DetectedDevice）。</para>
/// Device info model (wrapping a DetectedDevice enumerated by DeviceMonitorService).
/// </summary>
/// <remarks>
/// <para>初始化设备信息。</para>
/// Initializes the device info.
/// </remarks>
public class DeviceInfo(DetectedDevice device) : ViewModelBase
{

    /// <summary>底层枚举设备。 The underlying detected device.</summary>
    public DetectedDevice Device { get; } = device;

    /// <summary>稳定键。 Stable key.</summary>
    public string Key => Device.Key;

    /// <summary>设备序列号。 Device serial.</summary>
    public string Serial => Device.Serial;

    /// <summary>设备状态（device/offline/fastboot/edl/maskrom/loader 等）。 Device state.</summary>
    public string State
    {
        get => Device.State;
        set
        {
            if (Device.State != value)
            {
                Device.State = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>连接模式（ADB/Fastboot/EDL/RKDL）。 Connection mode.</summary>
    public string Mode => Device.Protocol switch
    {
        DeviceProtocolKind.Fastboot => "Fastboot",
        DeviceProtocolKind.Edl => "EDL",
        DeviceProtocolKind.Rkdl => "RKDL",
        _ => "ADB",
    };

    /// <summary>协议类型枚举值。 Protocol kind value.</summary>
    public DeviceProtocolKind Protocol => Device.Protocol;

    /// <summary>传输类型（USB/Network/Serial）。 Transport kind (USB/Network/Serial).</summary>
    public string Transport => Device.Transport switch
    {
        DeviceTransportKind.Tcp => "Network",
        DeviceTransportKind.Serial => "Serial",
        _ => "USB",
    };

    /// <summary>设备型号（ro.product.model 或 fastboot product）。 Device model.</summary>
    public string Model
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>Android 版本（ro.build.version.release）。 Android version.</summary>
    public string AndroidVersion
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>显示名称。 Display name.</summary>
    public string DisplayName => string.IsNullOrEmpty(Model)
        ? $"{Serial} ({State})"
        : $"{Serial} - {Model} ({State})";
}