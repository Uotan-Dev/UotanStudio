using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

/// <summary>
/// Device view model, managing ADB/Fastboot/EDL/RKDL device lists and connection state.
/// Enumeration and protocol interaction are fully based on UotanStudio.Comm
/// (DeviceMonitorService).
/// <para>设备视图模型，管理 ADB/Fastboot/EDL/RKDL 设备列表与连接状态。
/// 枚举与协议交互完全基于 UotanStudio.Comm（DeviceMonitorService）。</para>
/// </summary>
public partial class DeviceViewModel : ViewModelBase, IDisposable
{
    private readonly bool _monitorDevices;
    private readonly Func<DeviceProtocolKind, string, DetectedDevice?, CancellationToken, Task> _changeNetworkConnection;
    private Task? _refreshTask;
    private readonly Dictionary<string, (string Signature, long Ticks)> _enrichedDevices = new(StringComparer.Ordinal);
    private const long EnrichmentRefreshMilliseconds = 30_000;

    /// <summary>
    /// Gets the active device panel singleton, shared by the main window and the
    /// workspace dock so no duplicate instance subscribes to device changes twice.
    /// <para>获取当前激活的设备面板单例，供主窗口与工作区停靠面板共享同一实例
    /// （避免双实例导致重复订阅设备变更）。</para>
    /// </summary>
    public static DeviceViewModel? Instance { get; private set; }

    /// <summary>
    /// Ensures the device panel singleton exists and returns it; the first call constructs
    /// the instance (registered on <see cref="Instance"/> in the constructor) and later
    /// calls reuse the same instance.
    /// <para>确保设备面板单例已创建并返回它；首次调用时构造并在构造函数中注册到
    /// <see cref="Instance"/>，后续调用复用同一实例。</para>
    /// </summary>
    internal static DeviceViewModel EnsureInstance() => Instance ??= new();

    /// <summary>
    /// Gets or sets the list of detected devices. Collection changes also notify
    /// <see cref="HasDevices"/> / <see cref="HasNoDevices"/>.
    /// <para>获取或设置已检测到的设备列表。集合增删时同步通知
    /// <see cref="HasDevices"/> / <see cref="HasNoDevices"/>。</para>
    /// </summary>
    public ObservableCollection<DeviceInfo> Devices
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    /// <summary>
    /// Gets or sets the currently selected device.
    /// <para>获取或设置当前选中的设备。</para>
    /// </summary>
    public DeviceInfo? SelectedDevice
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                NotifySelectedDeviceProperties();
                foreach (var device in Devices)
                {
                    device.NotifySelectionChanged();
                }
            }
        }
    }

    internal void NotifyConnectionChanged(DeviceInfo device)
    {
        if (ReferenceEquals(SelectedDevice, device))
        {
            NotifySelectedDeviceProperties();
            if (_monitorDevices)
            {
                _ = EnrichDeviceInfoAsync(device);
            }
        }
    }

    private void NotifySelectedDeviceProperties()
    {
        OnPropertyChanged(nameof(ShowSelectPlaceholder));
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(DeviceDetail));
        OnPropertyChanged(nameof(CanOpenAdbShell));
        OnPropertyChanged(nameof(CanFastbootReadInfo));
        OnPropertyChanged(nameof(CanRebootToFastbootd));
        OnPropertyChanged(nameof(CanQuickFlash));
        OnPropertyChanged(nameof(CanBackupRestore));
        OnPropertyChanged(nameof(CanScreenMirror));
        OnPropertyChanged(nameof(CanManageSelectedConnection));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        OpenAdbShellCommand.NotifyCanExecuteChanged();
        HdcOperationCommand.NotifyCanExecuteChanged();
        FastbootReadInfoCommand.NotifyCanExecuteChanged();
        QuickFlashCommand.NotifyCanExecuteChanged();
        BackupPartitionCommand.NotifyCanExecuteChanged();
        RestorePartitionCommand.NotifyCanExecuteChanged();
        ScreenMirrorCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Gets or sets whether the device list is currently refreshing.
    /// <para>获取或设置是否正在刷新设备列表。</para>
    /// </summary>
    public bool IsRefreshing
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets or sets the status text.
    /// <para>获取或设置状态文本。</para>
    /// </summary>
    public string StatusText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Gets whether there are no devices (used to show the empty-state hint).
    /// <para>获取是否没有任何设备（用于显示空状态提示）。</para>
    /// </summary>
    public bool HasNoDevices => !IsRefreshing && Devices.Count == 0;

    /// <summary>
    /// Gets whether any device exists (the top-bar selector switches between the
    /// placeholder and the device list based on this).
    /// <para>获取是否存在任何设备（用于顶栏选择器切换占位项与设备列表）。</para>
    /// </summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>
    /// Gets whether a device is selected (the top-bar selector switches between the
    /// device name and the placeholder text based on this).
    /// <para>获取是否已选中设备（用于顶栏选择器切换显示设备名或占位文本）。</para>
    /// </summary>
    public bool HasSelectedDevice => SelectedDevice is not null;

    /// <summary>
    /// Gets whether to show the "select device" placeholder (devices present but none selected).
    /// <para>获取是否显示「选择设备」占位文本（有设备但未选中时）。</para>
    /// </summary>
    public bool ShowSelectPlaceholder => HasDevices && !HasSelectedDevice;

    /// <summary>
    /// Gets the display name of the selected device (empty when none; keeps the top-bar
    /// header binding free of null-path errors).
    /// <para>获取当前选中设备的显示名称（未选中时为空，供顶栏头部绑定避免空路径）。</para>
    /// </summary>
    public string SelectedDeviceName => SelectedDevice?.DisplayName ?? string.Empty;

    /// <summary>
    /// Selects the specified device (top-bar selector entry point, sharing the same
    /// selection state with the device panel).
    /// <para>选中指定设备（顶栏选择器点选入口，与设备面板共享同一选择状态）。</para>
    /// </summary>
    internal void SelectDevice(DeviceInfo? device) => SelectedDevice = device;

    /// <summary>
    /// Gets the detail text of the selected device.
    /// <para>获取选中设备的详情文本。</para>
    /// </summary>
    public string DeviceDetail => SelectedDevice is null
        ? string.Empty
        : $"Serial: {SelectedDevice.Serial}\nMode: {SelectedDevice.Mode}\nState: {SelectedDevice.State}\nTransport: {SelectedDevice.Transport}\nModel: {SelectedDevice.Model}\nAndroid: {SelectedDevice.AndroidVersion}";

    /// <summary>
    /// Last backup/restore progress percent forwarded to the unified log per partition,
    /// used to deduplicate repeated values and avoid flooding the log.
    /// <para>各分区最近一次转发到统一日志的备份/还原进度百分比，用于去重避免刷屏。</para>
    /// </summary>
    private readonly Dictionary<string, int> _backupProgressByPartition = [];

    /// <summary>
    /// Initializes the device view model: starts the device monitor service (which performs
    /// the startup full USB scan and begins hotplug monitoring), subscribes to device
    /// changes and performs the first refresh.
    /// <para>初始化设备视图模型：启动设备监视服务（含启动全量 USB 扫描与热插拔监听），
    /// 订阅设备变更事件并执行首次刷新。</para>
    /// </summary>
    public DeviceViewModel() : this(monitorDevices: true)
    {
    }

    internal DeviceViewModel(bool monitorDevices,
        Func<DeviceProtocolKind, string, DetectedDevice?, CancellationToken, Task>? changeNetworkConnection = null)
    {
        _monitorDevices = monitorDevices;
        _changeNetworkConnection = changeNetworkConnection ?? ExecuteNetworkConnectionAsync;
        Devices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasNoDevices));
            OnPropertyChanged(nameof(ShowSelectPlaceholder));
        };
        if (monitorDevices)
        {
            Instance = this;
            App.DeviceMonitor.DevicesChanged += OnDevicesChanged;
            App.DeviceMonitor.Start();
            _ = RefreshDevicesAsync();
        }
        WeakReferenceMessenger.Default.Register<BackupProgressMessage>(this, OnBackupProgress);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshDevicesAsync());

    /// <summary>
    /// Responds to backup/restore progress messages, forwarding progress to the unified log
    /// (deduplicated by partition and percent to avoid flooding).
    /// <para>响应备份/还原进度消息，将进度转发到统一日志（按分区对百分比去重，避免刷屏）。</para>
    /// </summary>
    private void OnBackupProgress(object recipient, BackupProgressMessage message)
    {
        var partition = message.Partition ?? string.Empty;
        if (message.Percent < 100
            && _backupProgressByPartition.TryGetValue(partition, out var last)
            && last == message.Percent)
        {
            return;
        }

        _backupProgressByPartition[partition] = message.Percent;
        LogService.Instance.Write(LogLevel.Info, "BackupService",
            $"[{message.Percent}%] {message.Status}{(string.IsNullOrEmpty(partition) ? string.Empty : $" ({partition})")}");
    }

    /// <summary>
    /// Refreshes the device list: invokes the monitor service for a full scan, groups by
    /// protocol, then concurrently pulls ADB device properties to enrich details.
    /// <para>刷新设备列表：调用设备监视服务执行全量扫描并按协议分组；
    /// 随后并发拉取 ADB 设备属性丰富详情。</para>
    /// </summary>
    private Task RefreshDevicesAsync() => _refreshTask is { IsCompleted: false }
        ? _refreshTask : _refreshTask = RefreshDevicesCoreAsync();

    private bool ShouldEnrich(DeviceInfo device)
    {
        var signature = $"{device.Protocol}:{device.State}";
        var now = Environment.TickCount64;
        if (_enrichedDevices.TryGetValue(device.Key, out var previous)
            && previous.Signature == signature
            && now - previous.Ticks < EnrichmentRefreshMilliseconds)
        {
            return false;
        }

        _enrichedDevices[device.Key] = (signature, now);
        return true;
    }

    private void PruneEnrichmentCache()
    {
        if (_enrichedDevices.Count == 0)
        {
            return;
        }

        HashSet<string>? live = null;
        List<string>? stale = null;
        foreach (var key in _enrichedDevices.Keys)
        {
            live ??= new HashSet<string>(Devices.Select(device => device.Key), StringComparer.Ordinal);
            if (!live.Contains(key))
            {
                (stale ??= []).Add(key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var key in stale)
        {
            _enrichedDevices.Remove(key);
        }
    }

    private async Task RefreshDevicesCoreAsync()
    {
        if (!_monitorDevices)
        {
            return;
        }

        IsRefreshing = true;
        StatusText = LanguageService.Instance.GetString("Device_Scanning");
        OnPropertyChanged(nameof(HasNoDevices));
        var selectedKey = SelectedDevice?.Key;
        try
        {
            var scanned = await Task.Run(() =>
                DeviceMonitorService.GroupPhysicalDevices(App.DeviceMonitor.Scan())).ConfigureAwait(true);

            var changed = Devices.Count != scanned.Count;
            if (!changed)
            {
                for (var i = 0; i < scanned.Count; i++)
                {
                    if (Devices[i].Key != scanned[i][0].GroupId ||
                        !Devices[i].MatchesConnections(scanned[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
            {
                for (var i = 0; i < scanned.Count; i++)
                {
                    var match = i < Devices.Count &&
                        Devices[i].Key == scanned[i][0].GroupId
                        ? Devices[i] : null;
                    if (match is not null)
                    {
                        if (!match.MatchesConnections(scanned[i]))
                        {
                            var replacement = new DeviceInfo(scanned[i], this);
                            var selectedEndpoint = scanned[i].FirstOrDefault(endpoint => endpoint.Key == match.Device.Key);
                            if (selectedEndpoint is not null)
                                replacement.SelectedConnection = selectedEndpoint;
                            Devices[i] = replacement;
                        }
                        continue;
                    }
                    Devices.Insert(i, new DeviceInfo(scanned[i], this));
                }
                for (var i = Devices.Count - 1; i >= scanned.Count; i--)
                {
                    Devices.RemoveAt(i);
                }
            }

            if (selectedKey is not null)
            {
                SelectedDevice = Devices.FirstOrDefault(x => x.Key == selectedKey);
            }

            if (SelectedDevice is null && Devices.Count > 0)
            {
                SelectedDevice = Devices[0];
            }

            StatusText = Devices.Count == 0
                ? LanguageService.Instance.GetString("Device_NoDevices")
                : $"{Devices.Count}{LanguageService.Instance.GetString("Device_DeviceCount")}";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager",
                $"Scan completed: {Devices.Count} device(s) found.");

            var enrichable = Devices
                .Where(x => x.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot
                        or DeviceProtocolKind.Hdc or DeviceProtocolKind.Edl
                    || (x.Protocol is DeviceProtocolKind.Mtk && x.State is "brom" or "preloader"))
                .Where(ShouldEnrich)
                .ToList();
            PruneEnrichmentCache();
            if (enrichable.Count > 0)
            {
                await Task.WhenAll(enrichable.Select(EnrichDeviceInfoAsync));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = LanguageService.Instance.GetString("Device_NoDevices");
        }
        catch (Exception ex)
        {
            StatusText = LanguageService.Instance.GetString("Device_RefreshFailed");
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Device refresh failed: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(HasNoDevices));
        }
    }

    /// <summary>
    /// Manual force refresh: forces a rescan (bypassing the hotplug debounce).
    /// <para>手动强制刷新：强制重新扫描（绕过热插拔去抖）。</para>
    /// </summary>
    [RelayCommand]
    private async Task ForceRefreshAsync()
    {
        await App.DeviceMonitor.ForceRefreshAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Unsubscribes from device change events.
    /// <para>取消订阅设备变更事件。</para>
    /// </summary>
    public void Dispose()
    {
        _connectionCancellation?.Cancel();
        if (_monitorDevices)
        {
            App.DeviceMonitor.DevicesChanged -= OnDevicesChanged;
        }
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
        WeakReferenceMessenger.Default.Unregister<BackupProgressMessage>(this);
        GC.SuppressFinalize(this);
    }
}