using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.UiAbstractions;
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
    private readonly Func<DeviceProtocolKind, string, DetectedDevice?, DeviceTransportKind, CancellationToken, Task> _changeNetworkConnection;
    private readonly Func<string, string, IProgress<WirelessPairingProgress>?, CancellationToken, Task<string>> _pairWithCode;
    private readonly Func<string, Task> _showDeviceError;
    private Task? _refreshTask;

    /// <summary>Non-zero when a refresh request arrived during a running pass; written and read across the
    /// UI thread and the posted change handler, so it is touched only through <see cref="System.Threading.Volatile"/>.
    /// <para>遍历运行期间收到过刷新请求时为非零；它在 UI 线程与被投递的变更处理之间共享，因此只经
    /// <see cref="System.Threading.Volatile"/> 读写。</para></summary>
    private int _refreshQueued;
    private readonly Dictionary<string, (string Signature, long Ticks)> _enrichedDevices = new(StringComparer.Ordinal);
    private const long EnrichmentRefreshMilliseconds = 30_000;

    /// <summary>
    /// Window in milliseconds during which a selected endpoint a scan no longer reports keeps the selection instead of
    /// handing it to another device; it covers the gap a mode switch opens between the node that leaves and the node
    /// that replaces it.
    /// <para>扫描不再报告的选中端点在这段毫秒窗口内保持选中，而不是把它交给其他设备；该窗口覆盖模式切换在
    /// 离开的节点与替代节点之间留下的空档。</para>
    /// </summary>
    private const int SelectionHoldMilliseconds = 30_000;

    private readonly TimeProvider _clock;
    private DateTimeOffset _selectionHoldUntil;
    private HashSet<string>? _selectionHoldBaseline;

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
            var previous = field;
            if (SetProperty(ref field, value))
            {
                if (previous is not null)
                {
                    previous.PropertyChanged -= OnSelectedDevicePropertyChanged;
                }
                if (field is not null)
                {
                    field.PropertyChanged += OnSelectedDevicePropertyChanged;
                }
                OnPropertyChanged(nameof(HasSelectedDevice));
                NotifySelectedDeviceProperties();
                foreach (var device in Devices)
                {
                    device.NotifySelectionChanged();
                }

                WeakReferenceMessenger.Default.Send(new SelectedDeviceChangedMessage
                {
                    Protocol = value?.Protocol,
                    Serial = value?.Serial
                });
            }
        }
    }

    /// <summary>
    /// Forwards a change of the selected device entry to the computed name and detail text, so the top
    /// selector and the detail panel follow later enrichment such as the detected slot.
    /// <para>把选中设备条目的变更转发到计算出的名称与详情文本，使顶部选择器与详情面板跟随槽位等后续补充信息。</para>
    /// </summary>
    private void OnSelectedDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(DeviceDetail));
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
        OnPropertyChanged(nameof(CanRebootToSystem));
        OnPropertyChanged(nameof(CanRebootToBootloader));
        OnPropertyChanged(nameof(CanRebootToRecovery));
        OnPropertyChanged(nameof(CanRebootToEdl));
        OnPropertyChanged(nameof(CanQuickFlash));
        OnPropertyChanged(nameof(CanBackupRestore));
        OnPropertyChanged(nameof(CanScreenMirror));
        OnPropertyChanged(nameof(CanOpenDeviceFiles));
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
        OpenDeviceFilesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Gets or sets whether the device list is currently refreshing.
    /// <para>获取或设置是否正在刷新设备列表。</para>
    /// </summary>
    public bool IsRefreshing
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsDeviceOperationBusy));
            }
        }
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
    internal void SelectDevice(DeviceInfo? device)
    {
        ClearSelectionHold();
        SelectedDevice = device;
    }

    /// <summary>
    /// Gets the detail text of the selected device.
    /// <para>获取选中设备的详情文本。</para>
    /// </summary>
    public string DeviceDetail => SelectedDevice is null
        ? string.Empty
        : $"Serial: {SelectedDevice.SerialDisplay}\nMode: {SelectedDevice.Mode}\nState: {SelectedDevice.State}\nTransport: {SelectedDevice.Transport}\nModel: {SelectedDevice.Model}\nAndroid: {SelectedDevice.AndroidVersion}";

    /// <summary>
    /// Last backup/restore progress percent forwarded to the unified log per partition,
    /// used to deduplicate repeated values and avoid flooding the log.
    /// <para>各分区最近一次转发到统一日志的备份/还原进度百分比，用于去重避免刷屏。</para>
    /// </summary>
    private readonly Dictionary<string, int> _backupProgressByPartition = [];

    /// <summary>Localized string lookup supplied by the composition root. <para>由组合根提供的本地化字符串查询。</para></summary>
    private readonly ILocalizer _localizer;

    /// <summary>Unified log sink supplied by the composition root. <para>由组合根提供的统一日志接收端。</para></summary>
    private readonly IUnifiedLog _log;

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
        Func<DeviceProtocolKind, string, DetectedDevice?, DeviceTransportKind, CancellationToken, Task>? changeNetworkConnection = null,
        ILocalizer? localizer = null,
        IUnifiedLog? log = null,
        TimeProvider? clock = null,
        Func<string, string, IProgress<WirelessPairingProgress>?, CancellationToken, Task<string>>? pairWithCode = null,
        Func<string, Task>? showDeviceError = null)
    {
        _monitorDevices = monitorDevices;
        _changeNetworkConnection = changeNetworkConnection ?? ExecuteNetworkConnectionAsync;
        _pairWithCode = pairWithCode ?? DefaultPairWithCodeAsync;
        _showDeviceError = showDeviceError ?? ShowDeviceErrorAsync;
        _localizer = localizer ?? LanguageService.Instance;
        _log = log ?? LogService.Instance;
        _clock = clock ?? TimeProvider.System;
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
        _log.Write(LogLevel.Info, "BackupService",
            $"[{message.Percent}%] {message.Status}{(string.IsNullOrEmpty(partition) ? string.Empty : $" ({partition})")}");
    }

    /// <summary>
    /// Refreshes the device list: invokes the monitor service for a full scan, groups by
    /// protocol, then concurrently pulls ADB device properties to enrich details.
    /// <para>刷新设备列表：调用设备监视服务执行全量扫描并按协议分组；
    /// 随后并发拉取 ADB 设备属性丰富详情。</para>
    /// </summary>
    private Task RefreshDevicesAsync() => _refreshTask is { IsCompleted: false }
        ? QueueBehind(_refreshTask)
        : _refreshTask = RefreshUntilSettledAsync();

    /// <summary>
    /// Records a refresh request that arrived while a pass was already running. That pass sampled the device
    /// list before the request, so its result is already stale — and because a device query can hold the pass
    /// open for seconds, a device unplugged during it would otherwise keep its row until some unrelated event
    /// happened to start a new pass. The running pass therefore sweeps once more after finishing.
    /// <para>记录一次在已有遍历运行期间到达的刷新请求。该遍历在请求之前就已采样设备列表，结果对本请求而言是旧的——
    /// 而一次设备查询会让该遍历持续数秒，因此其间被拔掉的设备会一直保留条目，直到某个无关事件恰好启动新一轮。
    /// 所以运行中的遍历结束后必须再扫一次。</para>
    /// </summary>
    /// <param name="running">The in-flight refresh the caller awaits.
    /// <para>调用方等待的那次进行中的刷新。</para></param>
    /// <returns>The in-flight task, now guaranteed to be followed by another pass.
    /// <para>进行中的任务；它现在保证会被追加一轮。</para></returns>
    private Task QueueBehind(Task running)
    {
        Volatile.Write(ref _refreshQueued, 1);
        return running;
    }

    /// <summary>
    /// Runs refresh passes until no request arrived during the most recent one.
    /// <para>反复执行刷新遍历，直到最近一轮期间不再有新的请求到达。</para>
    /// </summary>
    private async Task RefreshUntilSettledAsync()
    {
        do
        {
            Volatile.Write(ref _refreshQueued, 0);
            await RefreshDevicesCoreAsync().ConfigureAwait(true);
        }
        while (Volatile.Read(ref _refreshQueued) != 0);
    }

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
        StatusText = _localizer.GetString("Device_Scanning");
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

            ApplySelectionAfterScan(selectedKey);

            StatusText = Devices.Count == 0
                ? _localizer.GetString("Device_NoDevices")
                : $"{Devices.Count}{_localizer.GetString("Device_DeviceCount")}";
            _log.Write(LogLevel.Info, "DeviceManager",
                $"Scan completed: {Devices.Count} device(s) found.");

            var enrichable = Devices
                .Where(x => x.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot
                        or DeviceProtocolKind.Hdc or DeviceProtocolKind.Edl or DeviceProtocolKind.Ios
                        or DeviceProtocolKind.Rkdl
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
            StatusText = _localizer.GetString("Device_NoDevices");
        }
        catch (Exception ex)
        {
            StatusText = _localizer.GetString("Device_RefreshFailed");
            await _showDeviceError(StatusText + "\n" + UserError.Describe(ex));
            _log.Write(LogLevel.Error, "DeviceManager", $"Device refresh failed: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(HasNoDevices));
        }
    }

    /// <summary>
    /// Arms the static wait a scan must respect while a device leaves the bus and comes back in another mode, so the
    /// selection neither jumps to an unrelated device during the gap nor has to be restored by hand afterwards.
    /// <para>开启扫描必须遵守的静态等待：设备离开总线并以另一种模式回来期间，选择既不会在空档跳到无关设备，
    /// 也不需要在之后手工恢复。</para>
    /// </summary>
    /// <remarks>
    /// Call this right after sending a stage-changing payload, such as a Rockchip maskrom loader: the maskrom node
    /// disappears before the usbplug node enumerates, and both carry a different key because the switch rewrites the
    /// serial and the OS container identity. The wait remembers which endpoints were known at that moment, so the one
    /// endpoint that appears afterwards is recognised as the replacement rather than as an unrelated device.
    /// <para>在发送会改变阶段的负载之后立刻调用（例如瑞芯微 maskrom 的 loader）：maskrom 节点先消失，之后
    /// usbplug 节点才枚举出来，而切换会改写序列号与操作系统容器身份，使两者的键不同。该等待记录当时已知的端点，
    /// 因此之后出现的那个端点会被认作替代节点，而不是无关设备。</para>
    /// </remarks>
    internal void BeginSelectionHold()
    {
        _selectionHoldBaseline = [.. Devices.Select(device => device.Key)];
        _selectionHoldUntil = _clock.GetUtcNow() + TimeSpan.FromMilliseconds(SelectionHoldMilliseconds);
    }

    /// <summary>
    /// Restores the selection after a scan: the entry with the captured key wins, an armed hold keeps the selection
    /// across a mode switch, and only a settled disappearance hands the selection to another device.
    /// <para>扫描后恢复选择：键匹配的条目优先；已开启的保持让选择跨越模式切换，只有确定消失后才把选择交给
    /// 其他设备。</para>
    /// </summary>
    /// <param name="selectedKey">The key captured before the scan, or <see langword="null"/> when nothing was selected.
    /// <para>扫描前捕获的键；没有选中项时为 <see langword="null"/>。</para></param>
    internal void ApplySelectionAfterScan(string? selectedKey)
    {
        if (selectedKey is not null && Devices.FirstOrDefault(device => device.Key == selectedKey) is { } match)
        {
            SelectedDevice = match;
            return;
        }

        if (selectedKey is not null && HoldSelectionAcrossModeSwitch())
        {
            return;
        }

        if (selectedKey is not null)
        {
            SelectedDevice = null;
        }

        if (SelectedDevice is null && Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
        }
    }

    /// <summary>
    /// Keeps the current selection while an armed hold is still waiting for the device to come back, and adopts the
    /// single endpoint that appeared since the hold was armed as its replacement.
    /// <para>在已开启的保持仍等待设备回来期间保留当前选择，并把保持开启后出现的唯一端点认作它的替代节点。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the selection was settled by the hold; otherwise <see langword="false"/>.
    /// <para>选择已由保持处理完毕时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    private bool HoldSelectionAcrossModeSwitch()
    {
        if (_selectionHoldBaseline is not { } baseline)
        {
            return false;
        }

        if (_clock.GetUtcNow() >= _selectionHoldUntil)
        {
            _selectionHoldBaseline = null;
            return false;
        }

        var appeared = Devices.Where(device => !baseline.Contains(device.Key)).Take(2).ToArray();
        if (appeared.Length == 1)
        {
            _selectionHoldBaseline = null;
            SelectedDevice = appeared[0];
            _log.Write(LogLevel.Info, "DeviceManager",
                $"Selected the device {appeared[0].Key} that came back after the mode switch.");
            return true;
        }

        _log.Write(LogLevel.Info, "DeviceManager",
            "Kept the selected device while it comes back in the switched mode.");
        return true;
    }

    /// <summary>
    /// Ends the selection hold; called whenever a selection is committed, because that commit already settled it.
    /// <para>结束选择保持；每次提交选择时调用，因为那次提交已经确定了选择。</para>
    /// </summary>
    private void ClearSelectionHold()
    {
        _selectionHoldUntil = default;
        _selectionHoldBaseline = null;
    }

    /// <summary>
    /// Manual force refresh: forces a rescan that re-reads the whole USB inventory, bypassing both the
    /// hotplug debounce and the incremental cache. Automatic passes stay incremental.
    /// <para>手动强制刷新：强制一次重新读取完整 USB 清单的扫描，同时绕过热插拔去抖与增量缓存。
    /// 自动触发的遍历仍保持增量。</para>
    /// </summary>
    [RelayCommand]
    private async Task ForceRefreshAsync()
    {
        await App.DeviceMonitor.ForceFullRefreshAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Unsubscribes from device change events.
    /// <para>取消订阅设备变更事件。</para>
    /// </summary>
    public void Dispose()
    {
        _connectionCancellation?.Cancel();
        if (SelectedDevice is { } selected)
        {
            selected.PropertyChanged -= OnSelectedDevicePropertyChanged;
        }
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