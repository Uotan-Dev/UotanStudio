using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Devices;

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
    /// <para>获取当前激活的设备面板单例，供主窗口与工作区停靠面板共享同一实例
    /// （避免双实例导致重复订阅设备变更）。</para>
    /// Gets the active device panel singleton, shared by the main window and the
    /// workspace dock so no duplicate instance subscribes to device changes twice.
    /// </summary>
    public static DeviceViewModel? Instance { get; private set; }

    /// <summary>
    /// <para>确保设备面板单例已创建并返回它；首次调用时构造并在构造函数中注册到
    /// <see cref="Instance"/>，后续调用复用同一实例。</para>
    /// Ensures the device panel singleton exists and returns it; the first call constructs
    /// the instance (registered on <see cref="Instance"/> in the constructor) and later
    /// calls reuse the same instance.
    /// </summary>
    internal static DeviceViewModel EnsureInstance() => Instance ??= new();

    /// <summary>
    /// <para>获取或设置已检测到的设备列表。集合增删时同步通知
    /// <see cref="HasDevices"/> / <see cref="HasNoDevices"/>。</para>
    /// Gets or sets the list of detected devices. Collection changes also notify
    /// <see cref="HasDevices"/> / <see cref="HasNoDevices"/>.
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
            _ = EnrichDeviceInfoAsync(device);
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
        OpenAdbShellCommand.NotifyCanExecuteChanged();
        HdcOperationCommand.NotifyCanExecuteChanged();
        FastbootReadInfoCommand.NotifyCanExecuteChanged();
        QuickFlashCommand.NotifyCanExecuteChanged();
        BackupPartitionCommand.NotifyCanExecuteChanged();
        RestorePartitionCommand.NotifyCanExecuteChanged();
        ScreenMirrorCommand.NotifyCanExecuteChanged();
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
    /// <para>获取是否存在任何设备（用于顶栏选择器切换占位项与设备列表）。</para>
    /// Gets whether any device exists (the top-bar selector switches between the
    /// placeholder and the device list based on this).
    /// </summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>
    /// <para>获取是否已选中设备（用于顶栏选择器切换显示设备名或占位文本）。</para>
    /// Gets whether a device is selected (the top-bar selector switches between the
    /// device name and the placeholder text based on this).
    /// </summary>
    public bool HasSelectedDevice => SelectedDevice is not null;

    /// <summary>
    /// <para>获取是否显示「选择设备」占位文本（有设备但未选中时）。</para>
    /// Gets whether to show the "select device" placeholder (devices present but none selected).
    /// </summary>
    public bool ShowSelectPlaceholder => HasDevices && !HasSelectedDevice;

    /// <summary>
    /// <para>获取当前选中设备的显示名称（未选中时为空，供顶栏头部绑定避免空路径）。</para>
    /// Gets the display name of the selected device (empty when none; keeps the top-bar
    /// header binding free of null-path errors).
    /// </summary>
    public string SelectedDeviceName => SelectedDevice?.DisplayName ?? string.Empty;

    /// <summary>
    /// <para>选中指定设备（顶栏选择器点选入口，与设备面板共享同一选择状态）。</para>
    /// Selects the specified device (top-bar selector entry point, sharing the same
    /// selection state with the device panel).
    /// </summary>
    internal void SelectDevice(DeviceInfo? device) => SelectedDevice = device;

    /// <summary>
    /// <para>获取选中设备的详情文本。</para>
    /// Gets the detail text of the selected device.
    /// </summary>
    public string DeviceDetail => SelectedDevice is null
        ? string.Empty
        : $"Serial: {SelectedDevice.Serial}\nMode: {SelectedDevice.Mode}\nState: {SelectedDevice.State}\nTransport: {SelectedDevice.Transport}\nModel: {SelectedDevice.Model}\nAndroid: {SelectedDevice.AndroidVersion}";

    /// <summary>
    /// <para>各分区最近一次转发到统一日志的备份/还原进度百分比，用于去重避免刷屏。</para>
    /// Last backup/restore progress percent forwarded to the unified log per partition,
    /// used to deduplicate repeated values and avoid flooding the log.
    /// </summary>
    private readonly Dictionary<string, int> _backupProgressByPartition = [];

    /// <summary>
    /// <para>初始化设备视图模型：启动设备监视服务（含启动全量 USB 扫描与热插拔监听），
    /// 订阅设备变更事件并执行首次刷新。</para>
    /// Initializes the device view model: starts the device monitor service (which performs
    /// the startup full USB scan and begins hotplug monitoring), subscribes to device
    /// changes and performs the first refresh.
    /// </summary>
    public DeviceViewModel()
    {
        Instance = this;
        Devices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasNoDevices));
            OnPropertyChanged(nameof(ShowSelectPlaceholder));
        };
        App.DeviceMonitor.DevicesChanged += OnDevicesChanged;
        App.DeviceMonitor.Start();
        _ = RefreshDevicesAsync();
        WeakReferenceMessenger.Default.Register<BackupProgressMessage>(this, OnBackupProgress);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        // 热插拔/adb server 变更触发的增量刷新：保留当前选择。
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshDevicesAsync());

    /// <summary>
    /// <para>响应备份/还原进度消息，将进度转发到统一日志（按分区对百分比去重，避免刷屏）。</para>
    /// Responds to backup/restore progress messages, forwarding progress to the unified log
    /// (deduplicated by partition and percent to avoid flooding).
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
            var scanned = await Task.Run(() =>
                DeviceMonitorService.GroupPhysicalDevices(App.DeviceMonitor.Scan())).ConfigureAwait(true);

            // 差量更新：列表无变化直接跳过，有变化时按键保留/替换/插入/移除，
            // 避免 ObservableCollection 整表 Reset 造成选中丢失与界面闪烁。
            // Differential update: skip entirely when unchanged; otherwise keep/replace/insert/
            // remove by key instead of a full Clear+Add that resets the whole ItemsControl.
            var changed = Devices.Count != scanned.Count;
            if (!changed)
            {
                for (var i = 0; i < scanned.Count; i++)
                {
                    if (Devices[i].Key != (scanned[i][0].PhysicalId ?? scanned[i][0].Key) ||
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
                        Devices[i].Key == (scanned[i][0].PhysicalId ?? scanned[i][0].Key)
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

            // 选中不变式：只要列表中存在设备就保证选中项非空——原选中设备仍在则保留，
            // 已断开或从未选中时自动回退到当前列表第一项。
            // Selection invariant: whenever any device exists, keep a non-null selection —
            // retain the previous device if still present, otherwise fall back to the first
            // item (covers disconnects and the initial default selection).
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
                .Where(x => x.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot or DeviceProtocolKind.Hdc)
                .ToList();
            if (enrichable.Count > 0)
            {
                await Task.WhenAll(enrichable.Select(EnrichDeviceInfoAsync));
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
        await App.DeviceMonitor.ForceRefreshAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// <para>取消订阅设备变更事件。</para>
    /// Unsubscribes from device change events.
    /// </summary>
    public void Dispose()
    {
        App.DeviceMonitor.DevicesChanged -= OnDevicesChanged;
        WeakReferenceMessenger.Default.Unregister<BackupProgressMessage>(this);
        GC.SuppressFinalize(this);
    }
}