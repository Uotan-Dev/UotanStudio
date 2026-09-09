using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Messages;
using UotanStudio.Models;
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
                OnPropertyChanged(nameof(ShowSelectPlaceholder));
                OnPropertyChanged(nameof(SelectedDeviceName));
                OnPropertyChanged(nameof(DeviceDetail));
                OnPropertyChanged(nameof(CanOpenAdbShell));
                OnPropertyChanged(nameof(CanFastbootReadInfo));
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
                foreach (var device in Devices)
                {
                    device.NotifySelectionChanged();
                }
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
        DeviceMonitorService.Instance.DevicesChanged += OnDevicesChanged;
        DeviceMonitorService.Instance.Start();
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
            var scanned = await Task.Run(() => DeviceMonitorService.Instance.Scan()).ConfigureAwait(true);

            // 差量更新：列表无变化直接跳过，有变化时按键保留/替换/插入/移除，
            // 避免 ObservableCollection 整表 Reset 造成选中丢失与界面闪烁。
            // Differential update: skip entirely when unchanged; otherwise keep/replace/insert/
            // remove by key instead of a full Clear+Add that resets the whole ItemsControl.
            var changed = Devices.Count != scanned.Count;
            if (!changed)
            {
                for (var i = 0; i < scanned.Count; i++)
                {
                    if (Devices[i].Key != scanned[i].Key || Devices[i].State != scanned[i].State)
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
                    var match = i < Devices.Count && Devices[i].Key == scanned[i].Key ? Devices[i] : null;
                    if (match is not null)
                    {
                        if (match.State != scanned[i].State)
                        {
                            Devices[i] = new DeviceInfo(scanned[i], this);
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
        await DeviceMonitorService.Instance.ForceRefreshAsync().ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// <para>按设备协议分发到对应的信息丰富方法。</para>
    /// Dispatches to the mode-specific enrichment method based on the device protocol.
    /// </summary>
    private Task EnrichDeviceInfoAsync(DeviceInfo device) => device.Protocol switch
    {
        DeviceProtocolKind.Adb => EnrichAdbInfoAsync(device),
        DeviceProtocolKind.Hdc => EnrichHdcInfoAsync(device),
        DeviceProtocolKind.Fastboot => EnrichFastbootInfoAsync(device),
        // EDL/RKDL 处于 bootrom/loader 模式，无可拉取的系统属性。
        // EDL/RKDL stay in bootrom/loader mode with no system properties to retrieve.
        DeviceProtocolKind.Edl => Task.CompletedTask,
        DeviceProtocolKind.Rkdl => Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// <para>拉取 ADB 设备扩展信息（getprop / 系统级 / 电池 / 内存 / 存储）。官方 adb server 会枚举
    /// USB 与网络设备，因此无论传输类型都优先经 AdvancedSharpAdbClient 通过 adb server 执行 shell；
    /// adb server 不可用或设备不在其列表中时，仅对 USB 设备回退到 UotanStudio.Comm 原生 USB 会话。</para>
    /// Retrieves extended ADB device info (getprop / system-level / battery / memory / storage).
    /// The official adb server enumerates both USB and network devices, so regardless of transport
    /// the shell runs via AdvancedSharpAdbClient against the adb server first; only when the server
    /// is unavailable or the device is absent from its list does a USB device fall back to the
    /// UotanStudio.Comm native USB session.
    /// </summary>
    private async Task EnrichAdbInfoAsync(DeviceInfo device)
    {
        Dictionary<string, string>? info = null;
        try
        {
            info = await Task.Run(() =>
                QueryViaAdbServer(device)
                ?? (device.IsUsb ? QueryViaNativeUsb(device) : null)
            ).ConfigureAwait(true);
        }
        catch
        {
            // 属性拉取失败不影响主流程（设备可能未授权）。
        }

        if (info is null)
        {
            return;
        }

        ApplyAdbProperties(device, info);
        if (SelectedDevice == device)
        {
            OnPropertyChanged(nameof(DeviceDetail));
        }
    }

    /// <summary>
    /// <para>经 AdvancedSharpAdbClient 通过官方 adb server（127.0.0.1:5037）按序列号匹配设备并拉取属性；
    /// 设备不在列表中或 server 未运行时返回 null。</para>
    /// Queries device properties via AdvancedSharpAdbClient against the official adb server
    /// (127.0.0.1:5037), matching the device by serial; returns null when the device is absent
    /// from the list or the server is not running.
    /// </summary>
    private static Dictionary<string, string>? QueryViaAdbServer(DeviceInfo device)
    {
        var client = DeviceMonitorService.Instance.AdbServerClient;
        if (client is null)
        {
            return null;
        }

        var target = client.GetDevices()
            .FirstOrDefault(d => string.Equals(d.Serial, device.Serial, StringComparison.OrdinalIgnoreCase));
        return target is null
            ? null
            : QueryAdbProperties(cmd =>
        {
            try
            {
                var receiver = new ConsoleOutputReceiver();
                client.ExecuteRemoteCommand(cmd, target, receiver);
                return receiver.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// <para>通过 UotanStudio.Comm 原生 USB ADB 会话拉取设备属性；会话不可用或未连接时返回 null。</para>
    /// Queries device properties via a UotanStudio.Comm native USB ADB session; returns null when
    /// the session is unavailable or not connected.
    /// </summary>
    private static Dictionary<string, string>? QueryViaNativeUsb(DeviceInfo device)
    {
        var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device);
        if (session is null)
        {
            return null;
        }

        try
        {
            session.Connect();
            return session.Connection.IsConnected
                ? QueryAdbProperties(cmd => RunShell(session, cmd))
                : null;
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// <para>通过 UotanStudio.Comm 的 Fastboot 会话查询 getvar 变量丰富设备信息。</para>
    /// Queries fastboot getvar variables via a UotanStudio.Comm session to enrich device info.
    /// </summary>
    private async Task EnrichFastbootInfoAsync(DeviceInfo device)
    {
        Dictionary<string, string>? info = null;
        try
        {
            info = await Task.Run(() =>
            {
                var session = DeviceMonitorService.Instance.OpenFastbootSession(device.Device);
                if (session is null)
                {
                    return null;
                }

                try
                {
                    return !session.IsConnected
                        ? null
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["product"] = session.GetVariable("product", useCache: false),
                            ["bl"] = session.GetVariable("unlocked", useCache: false),
                            ["slot"] = session.GetVariable("current-slot", useCache: false),
                            ["userspace"] = session.GetVariable("is-userspace", useCache: false),
                            ["variant"] = session.GetVariable("variant", useCache: false),
                        };
                }
                catch
                {
                    return null;
                }
                finally
                {
                    session.Dispose();
                }
            }).ConfigureAwait(true);
        }
        catch
        {
            // 属性拉取失败不影响主流程。
        }

        if (info is null)
        {
            return;
        }

        ApplyFastbootProperties(device, info);
        if (SelectedDevice == device)
        {
            OnPropertyChanged(nameof(DeviceDetail));
        }
    }

    /// <summary>
    /// <para>执行一条 ADB shell 命令并返回去除空白行与首尾裁剪后的文本。</para>
    /// Runs an ADB shell command and returns the whitespace-collapsed trimmed output.
    /// </summary>
    private static string RunShell(AdbSession session, string command)
        => System.Text.Encoding.UTF8.GetString(session.ExecuteCommand(command).Stdout);

    /// <summary>
    /// <para>清理 shell 输出：去除空行并合并空白，错误标记归一化为 "--"。</para>
    /// Cleans shell output: strips blank lines, collapses whitespace, normalizes error
    /// markers to "--".
    /// </summary>
    private static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--";
        }

        var trimmed = string.Join(" ", raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return trimmed.Length == 0
            || trimmed.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("device offline", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("fail", StringComparison.OrdinalIgnoreCase)
                ? "--"
                : trimmed;
    }

    /// <summary>
    /// <para>从冒号分隔的输出中取冒号后的值（如 "Physical size: 1080x2340" -> "1080x2340"）。</para>
    /// Extracts the last colon-delimited segment (e.g. "Physical size: 1080x2340" -> "1080x2340").
    /// </summary>
    private static string ColonValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--";
        }

        var parts = raw.Split(['\r', '\n', ':'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1].Trim() : "--";
    }

    /// <summary>
    /// <para>批量查询 ADB 设备属性并解析电池、内存、磁盘信息。</para>
    /// Queries ADB device properties and parses battery, memory and disk details.
    /// </summary>
    private static Dictionary<string, string> QueryAdbProperties(Func<string, string> run)
    {
        string Get(string cmd)
        {
            return Clean(run(cmd));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brand"] = Get("getprop ro.product.brand"),
            ["model"] = Get("getprop ro.product.model"),
            ["android"] = Get("getprop ro.build.version.release"),
            ["sdk"] = Get("getprop ro.build.version.sdk"),
            ["codename"] = Get("getprop ro.product.device"),
            ["cpuabi"] = Get("getprop ro.product.cpu.abi"),
            ["cpucode"] = Get("getprop ro.board.platform"),
            ["kernel"] = Get("uname -r"),
            ["resolution"] = ColonValue(run("wm size")),
            ["density"] = ColonValue(run("wm density")),
            ["boardid"] = Get("cat /sys/devices/soc0/serial_number"),
            ["slot"] = Get("getprop ro.boot.slot_suffix"),
            ["compile"] = Get("getprop ro.system.build.version.incremental"),
            ["vndk"] = Get("getprop ro.vndk.version"),
        };

        var bl = Get("getprop ro.secureboot.lockstate");
        if (bl == "--")
        {
            bl = Get("getprop ro.boot.vbmeta.device_state");
        }

        map["bl"] = bl;
        map["uptime"] = FormatUptime(Get("cat /proc/uptime"));

        var (bLevel, bText) = ParseBattery(run("dumpsys battery"));
        map["batteryLevel"] = bLevel.ToString(CultureInfo.InvariantCulture);
        map["batteryText"] = bText;

        var (mLevel, mText) = ParseMem(run("cat /proc/meminfo | grep -E 'MemTotal|MemAvailable'"));
        map["memLevel"] = mLevel.ToString(CultureInfo.InvariantCulture);
        map["memText"] = mText;

        var (dLevel, dText) = ParseDisk(run("echo; df /storage/emulated 2>/dev/null || df /data 2>/dev/null || df /sdcard 2>/dev/null"));
        map["diskLevel"] = dLevel.ToString(CultureInfo.InvariantCulture);
        map["diskText"] = dText;

        return map;
    }

    /// <summary>
    /// <para>将查询到的 ADB 属性应用到设备信息模型。</para>
    /// Applies the queried ADB properties onto the device info model.
    /// </summary>
    private static void ApplyAdbProperties(DeviceInfo device, Dictionary<string, string> info)
    {
        device.Brand = info["brand"];
        device.SdkVersion = info["sdk"];
        device.CodeName = info["codename"];
        device.CpuAbi = info["cpuabi"];
        device.CpuCode = info["cpucode"];
        device.Kernel = info["kernel"];
        device.Resolution = info["resolution"];
        device.Density = info["density"];
        device.BoardId = info["boardid"];
        device.BlStatus = info["bl"];
        device.Slot = info["slot"];
        device.CompileVersion = info["compile"];
        device.VndkVersion = info["vndk"];
        device.PowerOnTime = info["uptime"];
        device.BatteryText = info["batteryText"];
        device.MemText = info["memText"];
        device.DiskText = info["diskText"];

        if (int.TryParse(info["batteryLevel"], out var bLevel))
        {
            device.BatteryLevel = bLevel;
        }

        if (int.TryParse(info["memLevel"], out var mLevel))
        {
            device.MemLevel = mLevel;
        }

        if (int.TryParse(info["diskLevel"], out var dLevel))
        {
            device.DiskLevel = dLevel;
        }

        if (info["model"] != "--")
        {
            device.Model = info["model"];
        }

        if (info["android"] != "--")
        {
            device.AndroidVersion = info["android"];
        }
    }

    /// <summary>
    /// <para>将查询到的 Fastboot 属性应用到设备信息模型。</para>
    /// Applies the queried fastboot properties onto the device info model.
    /// </summary>
    private static void ApplyFastbootProperties(DeviceInfo device, Dictionary<string, string> info)
    {
        var lang = LanguageService.Instance;
        if (info["product"] != "--")
        {
            device.Model = info["product"];
            device.CodeName = info["product"];
        }

        device.IsUserspace = info["userspace"].ToLowerInvariant().Contains("yes") ? "fastbootd" : "fastboot";
        device.DiskType = info["variant"].ToUpperInvariant() switch
        {
            string v when v.Contains("UFS") => "UFS",
            string v when v.Contains("EMMC") => "EMMC",
            _ => "--",
        };

        device.BlStatus = info["bl"].ToLowerInvariant() switch
        {
            "yes" => lang.GetString("Device_Unlocked"),
            "no" => lang.GetString("Device_Locked"),
            _ => "--",
        };

        device.Slot = ParseSlot(info["slot"]);
    }

    /// <summary>
    /// <para>解析 A/B 槽位号为可读字符串。</para>
    /// Parses an A/B slot id into a human-readable value.
    /// </summary>
    private static string ParseSlot(string slot)
    {
        if (string.IsNullOrEmpty(slot))
        {
            return "--";
        }

        var normalized = slot.Trim().ToLowerInvariant();
        return normalized.EndsWith("_a") || normalized.Equals("a") || normalized.Contains("current-slot: a")
            ? "Slot A"
            : normalized.EndsWith("_b") || normalized.Equals("b") || normalized.Contains("current-slot: b") ? "Slot B" : "--";
    }

    /// <summary>
    /// <para>将 /proc/uptime 的秒数格式化为可读的开机时长。</para>
    /// Formats the /proc/uptime seconds into a human-readable power-on duration.
    /// </summary>
    private static string FormatUptime(string raw)
    {
        if (raw is null or "--")
        {
            return "--";
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return "--";
        }

        var lang = LanguageService.Instance;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Days}{lang.GetString("Device_UptimeDay")}"
             + $"{ts.Hours}{lang.GetString("Device_UptimeHour")}"
             + $"{ts.Minutes}{lang.GetString("Device_UptimeMinute")}"
             + $"{ts.Seconds}{lang.GetString("Device_UptimeSecond")}";
    }

    /// <summary>
    /// <para>解析 dumpsys battery 输出，返回（电量百分比, 电压/温度文本）。</para>
    /// Parses dumpsys battery output; returns (level percent, voltage/temperature text).
    /// </summary>
    private static (int Level, string Text) ParseBattery(string info)
    {
        var values = new List<string>();
        var lines = info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("Max charging voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Charger voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("the voltage was", StringComparison.OrdinalIgnoreCase)
                || line.Contains("last voltage value", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("level", StringComparison.OrdinalIgnoreCase)
                || line.Contains("voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0)
                {
                    values.Add(tokens[^1]);
                }
            }
        }

        if (values.Count < 3)
        {
            return (-1, "--");
        }

        int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var millivolts);
        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tenthsCelsius);
        return (Math.Clamp(level, 0, 100), $"{millivolts / 1000.0:0.00}V {tenthsCelsius / 10.0:0.0}℃");
    }

    /// <summary>
    /// <para>解析 /proc/meminfo 中 MemTotal/MemAvailable，返回（占用百分比, 已用/总量文本）。</para>
    /// Parses MemTotal/MemAvailable from /proc/meminfo; returns (usage percent, used/total text).
    /// </summary>
    private static (int Level, string Text) ParseMem(string info)
    {
        long total = 0, available = 0;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out available);
            }
        }

        if (total <= 0)
        {
            return (-1, "--");
        }

        if (available <= 0)
        {
            available = total;
        }

        double usedKb = total - available;
        var totalGb = Math.Round(total * 1.024 / 1_000_000, 1);
        var usedGb = Math.Round(usedKb * 1.024 / 1_000_000, 1);
        var percent = (int)Math.Round(usedGb / (totalGb <= 0 ? 1 : totalGb) * 100);
        return (Math.Clamp(percent, 0, 100), $"{usedGb:0.0}GB/{totalGb:0.0}GB");
    }

    /// <summary>
    /// <para>解析 df 输出中目标挂载点所在行，返回（占用百分比, 已用/总量文本）。</para>
    /// Parses the target mount line from df output; returns (usage percent, used/total text).
    /// </summary>
    private static (int Level, string Text) ParseDisk(string info)
    {
        string? target = null;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("/storage/emulated")
                || line.StartsWith("/data")
                || line.StartsWith("/sdcard"))
            {
                target = line;
                break;
            }
        }

        if (target is null)
        {
            return (-1, "--");
        }

        // 列顺序：Filesystem Size Used Avail Use% Mounted
        var columns = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length < 5
            || !double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var blocks)
            || !double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedBlocks))
        {
            return (-1, "--");
        }

        var sizeGb = Math.Round(blocks / 1024.0 / 1024.0, 2);
        var usedGb = Math.Round(usedBlocks / 1024.0 / 1024.0, 2);
        var percent = (int)Math.Round(usedBlocks / (blocks <= 0 ? 1 : blocks) * 100);
        return (Math.Clamp(percent, 0, 100), $"{usedGb:0.00}GB/{sizeGb:0.00}GB");
    }
    /// <summary>
    /// <para>连接到选中的设备（验证协议可达性）。</para>
    /// Connects to the selected device (verifies protocol reachability).
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        var device = SelectedDevice;
        if (device == null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Connecting")} {device.Serial}";
        try
        {
            var ok = await Task.Run(() => device.Protocol switch
            {
                DeviceProtocolKind.Fastboot => TryFastboot(device),
                DeviceProtocolKind.Edl => true, // EDL 握手需要 programmer，此处仅确认设备在位
                DeviceProtocolKind.Rkdl => TryRkdl(device),
                DeviceProtocolKind.Adb => TryAdb(device),
                DeviceProtocolKind.Hdc => TryHdc(device),
                _ => false,
            }).ConfigureAwait(true);

            device.State = ok ? device.State : "offline";
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Connect")} {device.Serial} OK"
                : $"{LanguageService.Instance.GetString("Device_Connect")} {device.Serial} FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Connect {device.Serial} ({device.Mode}): {(ok ? "OK" : "FAIL")}");
            OnPropertyChanged(nameof(DeviceDetail));
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Connect")} {device.Serial} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Connect {device.Serial} failed: {ex.Message}");
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
        var device = SelectedDevice;
        if (device == null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {device.Serial}...";
        try
        {
            if (device.Protocol == DeviceProtocolKind.Hdc)
            {
                await HdcDeviceService.FromSettings().DisconnectAsync(device.Device).ConfigureAwait(true);
                await DeviceMonitorService.Instance.ForceRefreshAsync().ConfigureAwait(true);
            }
            else if (device.Transport == "Network")
            {
                var client = DeviceMonitorService.Instance.AdbServerClient;
                if (client is not null)
                {
                    await client.DisconnectAsync(device.Serial).ConfigureAwait(true);
                }
            }
            device.State = "offline";
            StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {device.Serial} OK";
            OnPropertyChanged(nameof(DeviceDetail));
        }
        catch
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {device.Serial} FAIL";
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
        var device = SelectedDevice;
        if (device == null || string.IsNullOrEmpty(mode))
        {
            return;
        }

        // 弹出确认对话框
        var modeDisplayName = mode switch
        {
            "system" => LanguageService.Instance.GetString("Device_ModeSystem"),
            "bootloader" => LanguageService.Instance.GetString("Device_ModeBootloader"),
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
            var ok = await DeviceMonitorService.Instance.RebootAsync(device.Device, mode).ConfigureAwait(true);
            StatusText = ok
                ? $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} OK"
                : $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} FAIL";
            LogService.Instance.Write(ok ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode}: {(ok ? "OK" : "FAIL")}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {device.Serial} -> {mode} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Reboot {device.Serial} -> {mode} failed: {ex.Message}");
        }
    }


    // ===== 快速固件操作 / Quick firmware operations =====

    /// <summary>
    /// <para>是否可打开选中设备的 ADB Shell（仅 ADB 模式设备）。</para>
    /// Whether an ADB shell can be opened for the selected device (ADB-mode devices only).
    /// </summary>
    public bool CanOpenAdbShell => SelectedDevice?.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc;

    /// <summary>
    /// <para>是否可读取选中设备的 Fastboot 分区/getvar 信息（仅 Fastboot 模式设备）。</para>
    /// Whether the selected Fastboot device's partition/getvar info can be read.
    /// </summary>
    public bool CanFastbootReadInfo => SelectedDevice?.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>
    /// <para>是否可对选中设备执行快速刷写（ADB/Fastboot/EDL/RKDL 均支持）。</para>
    /// Whether quick flash is available for the selected device (ADB/Fastboot/EDL/RKDL).
    /// </summary>
    public bool CanQuickFlash => SelectedDevice?.Protocol is DeviceProtocolKind.Adb
        or DeviceProtocolKind.Fastboot
        or DeviceProtocolKind.Edl
        or DeviceProtocolKind.Rkdl;

    /// <summary>
    /// <para>是否可对选中设备执行分区备份/还原（Fastboot / EDL 模式设备）。</para>
    /// Whether partition backup/restore is available for the selected device
    /// (Fastboot / EDL mode devices).
    /// </summary>
    public bool CanBackupRestore => SelectedDevice?.Protocol is DeviceProtocolKind.Fastboot or DeviceProtocolKind.Edl;

    /// <summary>
    /// <para>是否可对选中设备开启 scrcpy 投屏（需要 ADB 协议设备）。</para>
    /// Whether scrcpy mirroring can start for the selected device (requires the ADB protocol).
    /// </summary>
    public bool CanScreenMirror => SelectedDevice?.Protocol is DeviceProtocolKind.Adb;

    /// <summary>
    /// <para>对选中的 ADB 设备开启 scrcpy 投屏：打开工作区投屏文档并发送请求消息。</para>
    /// Starts scrcpy mirroring for the selected ADB device: opens the workspace mirror document
    /// and sends the request message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScreenMirror))]
    private void ScreenMirror()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new OpenScreenMirrorRequestMessage { Serial = device.Serial });
    }

    /// <summary>
    /// <para>打开选中设备的 ADB Shell 终端：使用设置中配置的 ADB 路径（未配置则用 PATH 中的 adb），
    /// 在新控制台窗口执行 <c>adb -s &lt;serial&gt; shell</c>。</para>
    /// Opens an interactive ADB shell for the selected device in a new console window, running
    /// <c>adb -s &lt;serial&gt; shell</c> with the adb binary from settings (or PATH when unset).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenAdbShell))]
    private void OpenAdbShell()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            var adbPath = device.Protocol == DeviceProtocolKind.Hdc ? HdcDeviceService.ResolveExecutable() : SettingsPersistenceService.Instance.LoadSettings().AdbPath;
            if (string.IsNullOrWhiteSpace(adbPath))
            {
                adbPath = "adb";
            }

            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                UseShellExecute = true
            };
            psi.ArgumentList.Add(device.Protocol == DeviceProtocolKind.Hdc ? "-t" : "-s");
            psi.ArgumentList.Add(device.Serial);
            psi.ArgumentList.Add("shell");
            Process.Start(psi);
            StatusText = $"{LanguageService.Instance.GetString("Device_OpenAdbShell")} {device.Serial} OK";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"Open {device.Mode} shell for {device.Serial}.");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_OpenAdbShell")} {device.Serial} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Open {device.Mode} shell for {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>读取选中 Fastboot 设备的分区/getvar 变量（product、secure、slot、boot 分区大小等），
    /// 结果写入状态栏与统一日志。</para>
    /// Reads partition/getvar variables from the selected Fastboot device (product, secure, slot,
    /// boot partition size, etc.) into the status bar and the unified log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFastbootReadInfo))]
    private async Task FastbootReadInfoAsync()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_FastbootReadInfo")} {device.Serial}...";
        try
        {
            var sb = new StringBuilder();
            await Task.Run(() =>
            {
                var session = DeviceMonitorService.Instance.OpenFastbootSession(device.Device);
                if (session is null)
                {
                    throw new InvalidOperationException("No fastboot session available. 无法建立 fastboot 会话。");
                }

                using var fb = session;
                if (!fb.IsConnected)
                {
                    throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                }

                foreach (var name in new[] { "product", "version-bootloader", "variant", "secure", "unlocked", "current-slot", "is-userspace", "partition-type:boot", "partition-size:boot" })
                {
                    try
                    {
                        sb.AppendLine($"{name}={fb.GetVariable(name, useCache: false)}");
                    }
                    catch
                    {
                        sb.AppendLine($"{name}=--");
                    }
                }
            }).ConfigureAwait(true);

            StatusText = $"{LanguageService.Instance.GetString("Device_FastbootReadInfo")} {device.Serial} OK";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"Fastboot getvar {device.Serial}:\n{sb.ToString().TrimEnd()}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_FastbootReadInfo")} {device.Serial} FAIL";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Fastboot read info {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>快速刷写入口：为选中设备选择镜像并刷写到指定分区（默认 boot），经
    /// <see cref="FlashService.FlashImageAsync"/> 执行，进度与结果回报到状态栏与统一日志。
    /// 刷写前会经 DialogService 请求用户确认。</para>
    /// Quick flash entry: picks an image and flashes it to the given partition (default "boot") on
    /// the selected device via <see cref="FlashService.FlashImageAsync"/>, reporting progress and
    /// results to the status bar and unified log. User confirmation is requested through
    /// DialogService before flashing.
    /// </summary>
    /// <param name="partition"><para>目标分区名；null 时使用 "boot"。</para>The target partition; "boot" when null.</param>
    [RelayCommand(CanExecute = nameof(CanQuickFlash))]
    private async Task QuickFlashAsync(object? partition)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        var partitionName = partition as string;
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            partitionName = "boot";
        }

        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Device_QuickFlash"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img", "*.bin"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        var imagePath = files[0].Path.LocalPath;
        if (topLevel is not Avalonia.Controls.Window owner) return;
        if (!await DialogService.ConfirmAsync(owner,
            LanguageService.Instance.GetString("Device_FlashConfirmTitle"),
            LanguageService.Instance.GetString("Device_FlashConfirmMessage") + $"\n{device.Serial}\n{partitionName}\n{imagePath}"))
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Flash_Flashing")} {partitionName}...";
        try
        {
            var progress = new Progress<FlashProgress>(fp =>
            {
                var percent = Math.Clamp(fp.Percent, 0, 100);
                StatusText = $"[{percent}%] {fp.Status}";
            });
            var result = await FlashService.Default.FlashImageAsync(device, partitionName, imagePath, progress, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Flash_Done")} {partitionName}"
                : $"{LanguageService.Instance.GetString("Flash_Failed")} {partitionName}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Quick flash {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Flash_Failed")} {partitionName}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Quick flash {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>备份分区入口：为选中 Fastboot 设备经 <c>SaveFileDialog</c> 选择输出路径，
    /// 打开 fastboot 会话后调用 <see cref="BackupService.BackupFastbootPartition"/> 备份指定分区，
    /// 进度回报状态栏与统一日志，结果写状态栏。</para>
    /// Backup entry: picks an output path for the selected Fastboot device via a save-file dialog,
    /// opens a fastboot session and backs up the given partition through
    /// <see cref="BackupService.BackupFastbootPartition"/>, reporting progress to the status bar
    /// and writing the result back to it.
    /// </summary>
    /// <param name="partition"><para>目标分区名；null 时使用 "boot"。</para>The target partition; "boot" when null.</param>
    [RelayCommand(CanExecute = nameof(CanBackupRestore))]
    private async Task BackupPartitionAsync(object? partition)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        var partitionName = partition as string;
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            partitionName = "boot";
        }

        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        if (device.Protocol == DeviceProtocolKind.Edl)
        {
            await BackupEdlPartitionCoreAsync(device, partitionName).ConfigureAwait(true);
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Backup_SaveTitle"),
            SuggestedFileName = $"{partitionName}.img",
            DefaultExtension = "img",
            FileTypeChoices =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        var outPath = file.Path.LocalPath;
        StatusText = $"{LanguageService.Instance.GetString("Backup_BackingUp")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await Task.Run(() =>
            {
                using var session = DeviceMonitorService.Instance.OpenFastbootSession(device.Device)
                    ?? throw new InvalidOperationException("No fastboot session available. 无法建立 fastboot 会话。");
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                }

                return BackupService.BackupFastbootPartition(session, partitionName, outPath, progress, CancellationToken.None);
            }).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Backup_Done")} {partitionName}"
                : $"{LanguageService.Instance.GetString("Backup_Failed")} {partitionName}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Backup {partitionName} from {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Backup_Failed")} {partitionName}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Backup {partitionName} from {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>还原分区入口：为选中 Fastboot 设备经 <c>OpenFileDialog</c> 选择镜像文件，
    /// 打开 fastboot 会话后调用 <see cref="BackupService.RestoreFastbootPartition"/> 刷回指定分区，
    /// 进度回报状态栏与统一日志，结果写状态栏。</para>
    /// Restore entry: picks an image for the selected Fastboot device via an open-file dialog,
    /// opens a fastboot session and restores the given partition through
    /// <see cref="BackupService.RestoreFastbootPartition"/>, reporting progress to the status bar
    /// and writing the result back to it.
    /// </summary>
    /// <param name="partition"><para>目标分区名；null 时使用 "boot"。</para>The target partition; "boot" when null.</param>
    [RelayCommand(CanExecute = nameof(CanBackupRestore))]
    private async Task RestorePartitionAsync(object? partition)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        var partitionName = partition as string;
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            partitionName = "boot";
        }

        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        if (device.Protocol == DeviceProtocolKind.Edl)
        {
            await RestoreEdlPartitionCoreAsync(device, partitionName).ConfigureAwait(true);
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Backup_OpenTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img", "*.bin"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        var imagePath = files[0].Path.LocalPath;
        StatusText = $"{LanguageService.Instance.GetString("Backup_Restoring")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await Task.Run(() =>
            {
                using var session = DeviceMonitorService.Instance.OpenFastbootSession(device.Device)
                    ?? throw new InvalidOperationException("No fastboot session available. 无法建立 fastboot 会话。");
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                }

                return BackupService.RestoreFastbootPartition(session, partitionName, imagePath, progress, CancellationToken.None);
            }).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Backup_RestoreDone")} {partitionName}"
                : $"{LanguageService.Instance.GetString("Backup_RestoreFailed")} {partitionName}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Restore {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Backup_RestoreFailed")} {partitionName}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Restore {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>EDL 备份核心流程：选择 rawprogram XML 解析分区几何与输出路径，打开 EDL 会话后经
    /// <see cref="BackupService.ResolveEdlPartitionGeometry"/> 与 <see cref="BackupService.BackupEdlPartitionAsync"/>
    /// 流式备份指定分区，进度回报状态栏与统一日志。</para>
    /// EDL backup core flow: picks a rawprogram XML for partition geometry plus an output path,
    /// opens an EDL session and streams the partition backup through
    /// <see cref="BackupService.ResolveEdlPartitionGeometry"/> and
    /// <see cref="BackupService.BackupEdlPartitionAsync"/>, reporting progress to the status bar
    /// and the unified log.
    /// </summary>
    /// <param name="device"><para>选中的 EDL 设备。</para>The selected EDL device.</param>
    /// <param name="partitionName"><para>目标分区名。</para>The target partition name.</param>
    private async Task BackupEdlPartitionCoreAsync(DeviceInfo device, string partitionName)
    {
        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var rawProgramFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Backup_SelectRawProgram"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("rawprogram") { Patterns = ["*.xml"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (rawProgramFiles.Count == 0)
        {
            return;
        }

        var rawProgramPath = rawProgramFiles[0].Path.LocalPath;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Backup_SaveTitle"),
            SuggestedFileName = $"{partitionName}.img",
            DefaultExtension = "img",
            FileTypeChoices =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        var outPath = file.Path.LocalPath;
        StatusText = $"{LanguageService.Instance.GetString("Backup_BackingUp")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await Task.Run(() =>
            {
                using var session = DeviceMonitorService.Instance.OpenEdlSession(device.Device)
                    ?? throw new InvalidOperationException("No EDL session available. 无法建立 EDL 会话。");
                var (startSector, sectorCount) = BackupService.ResolveEdlPartitionGeometry(rawProgramPath, partitionName);
                return BackupService.BackupEdlPartitionAsync(session, partitionName, startSector, sectorCount, outPath, progress, CancellationToken.None);
            }).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Backup_Done")} {partitionName}"
                : $"{LanguageService.Instance.GetString("Backup_Failed")} {partitionName}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"EDL backup {partitionName} from {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Backup_Failed")} {partitionName}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"EDL backup {partitionName} from {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>EDL 还原核心流程：选择 rawprogram XML，解析出目标分区条目后打开 EDL 会话，经
    /// <see cref="BackupService.RestoreEdlPartitionAsync"/> 刷回该分区（镜像文件由 rawprogram
    /// 条目引用定位），进度回报状态栏与统一日志。</para>
    /// EDL restore core flow: picks a rawprogram XML, resolves the target partition entry,
    /// opens an EDL session and restores the partition through
    /// <see cref="BackupService.RestoreEdlPartitionAsync"/> (the image is located via the
    /// rawprogram entry), reporting progress to the status bar and the unified log.
    /// </summary>
    /// <param name="device"><para>选中的 EDL 设备。</para>The selected EDL device.</param>
    /// <param name="partitionName"><para>目标分区名。</para>The target partition name.</param>
    private async Task RestoreEdlPartitionCoreAsync(DeviceInfo device, string partitionName)
    {
        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var rawProgramFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Backup_SelectRawProgram"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("rawprogram") { Patterns = ["*.xml"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (rawProgramFiles.Count == 0)
        {
            return;
        }

        var rawProgramPath = rawProgramFiles[0].Path.LocalPath;
        var imageDir = Path.GetDirectoryName(Path.GetFullPath(rawProgramPath)) ?? string.Empty;
        StatusText = $"{LanguageService.Instance.GetString("Backup_Restoring")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await Task.Run(() =>
            {
                using var session = DeviceMonitorService.Instance.OpenEdlSession(device.Device)
                    ?? throw new InvalidOperationException("No EDL session available. 无法建立 EDL 会话。");
                var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
                var entry = FlashService.FindEdlTarget(entries, partitionName, imagePath: null)
                    ?? throw new InvalidOperationException(
                        $"Partition '{partitionName}' not found in rawprogram: {rawProgramPath}. 分区 '{partitionName}' 未在 rawprogram 中匹配到。");
                return BackupService.RestoreEdlPartitionAsync(session, entry, imageDir, progress, CancellationToken.None);
            }).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{LanguageService.Instance.GetString("Backup_RestoreDone")} {partitionName}"
                : $"{LanguageService.Instance.GetString("Backup_RestoreFailed")} {partitionName}: {result.Summary}";
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"EDL restore {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Backup_RestoreFailed")} {partitionName}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"EDL restore {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }


    /// <summary>
    /// <para>取消订阅设备变更事件。</para>
    /// Unsubscribes from device change events.
    /// </summary>
    public void Dispose()
    {
        DeviceMonitorService.Instance.DevicesChanged -= OnDevicesChanged;
        WeakReferenceMessenger.Default.Unregister<BackupProgressMessage>(this);
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
public partial class DeviceInfo(DetectedDevice device, DeviceViewModel? owner = null) : ViewModelBase
{

    /// <summary>底层枚举设备。 The underlying detected device.</summary>
    public DetectedDevice Device { get; } = device;

    /// <summary>
    /// <para>获取或设置当前条目是否为全局选中设备（顶栏选择器打勾标记；
    /// 选中状态变化由 <see cref="DeviceViewModel"/> 统一通知）。</para>
    /// Gets or sets whether this entry is the globally selected device (top-bar selector
    /// check mark; selection changes are notified by <see cref="DeviceViewModel"/>).
    /// </summary>
    public bool IsSelected
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>由 <see cref="DeviceViewModel"/> 在选中设备变化时调用，刷新打勾绑定。</para>
    /// Called by <see cref="DeviceViewModel"/> when the selected device changes, refreshing
    /// the check-mark binding.
    /// </summary>
    internal void NotifySelectionChanged() => IsSelected = owner is not null && ReferenceEquals(owner.SelectedDevice, this);

    /// <summary>
    /// <para>选中本设备（顶栏选择器点选入口；无宿主 <see cref="DeviceViewModel"/> 的临时实例不可执行）。</para>
    /// Selects this device (top-bar selector entry point; not executable for temporary
    /// instances without an owning <see cref="DeviceViewModel"/>).
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        if (owner is not null)
        {
            owner.SelectDevice(this);
        }
    }

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
        DeviceProtocolKind.Adb => "ADB",
        DeviceProtocolKind.Fastboot => "Fastboot",
        DeviceProtocolKind.Edl => "EDL",
        DeviceProtocolKind.Rkdl => "RKDL",
        DeviceProtocolKind.Hdc => "HDC",
        _ => throw new ArgumentOutOfRangeException(nameof(Device.Protocol), Device.Protocol, "Unexpected protocol kind."),
    };

    /// <summary>协议类型枚举值。 Protocol kind value.</summary>
    public DeviceProtocolKind Protocol => Device.Protocol;

    /// <summary>传输类型（USB/Network/Serial）。 Transport kind (USB/Network/Serial).</summary>
    public string Transport => Device.Transport switch
    {
        DeviceTransportKind.Tcp => "Network",
        DeviceTransportKind.Serial => "Serial",
        DeviceTransportKind.Usb => "USB",
        _ => "Unknown",
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

    /// <summary>链接端口/地址信息（USB 标识、TCP host:port、串口路径）。 Link/port info.</summary>
    public string LinkPort => Device.Transport switch
    {
        DeviceTransportKind.Tcp =>
            Device.Key.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? Device.Key[4..] : Device.Key,
        DeviceTransportKind.Serial => Device.Key,
        DeviceTransportKind.Usb => "USB",
        _ => "Unknown",
    };

    /// <summary>厂商/产品 USB ID（十六进制 VID:PID）。 Vendor/product USB ID.</summary>
    public string UsbIds => Device.VendorId == 0
        ? string.Empty
        : $"{Device.VendorId:X04}:{Device.ProductId:X04}";

    /// <summary>USB 厂商名（按 VendorId 查内置表，未知返回空）。 USB vendor name from the built-in VID table.</summary>
    public string VendorName => UsbVendorTable.TryGetVendor(Device.VendorId);

    /// <summary>是否为 ADB 设备。 Whether the device is in ADB mode.</summary>
    public bool IsAdb => Device.Protocol == DeviceProtocolKind.Adb;

    /// <summary>Whether this is an HDC device.</summary>
    public bool IsHdc => Device.Protocol == DeviceProtocolKind.Hdc;

    /// <summary>Whether system details are available.</summary>
    public bool HasSystemInfo => IsAdb || IsHdc;

    /// <summary>是否为 Fastboot 设备。 Whether the device is in Fastboot mode.</summary>
    public bool IsFastboot => Device.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>是否为 EDL 设备。 Whether the device is in EDL mode.</summary>
    public bool IsEdl => Device.Protocol == DeviceProtocolKind.Edl;

    /// <summary>是否为 RKDL 设备。 Whether the device is in RKDL mode.</summary>
    public bool IsRkdl => Device.Protocol == DeviceProtocolKind.Rkdl;

    /// <summary>是否为 USB 直连设备。 Whether the device is connected over USB.</summary>
    public bool IsUsb => Device.Transport == DeviceTransportKind.Usb;

    /// <summary>是否为网络（TCP/adb server）设备。 Whether the device is connected over network.</summary>
    public bool IsNetwork => Device.Transport == DeviceTransportKind.Tcp;

    /// <summary>是否为串口设备。 Whether the device is connected over a serial port.</summary>
    public bool IsSerial => Device.Transport == DeviceTransportKind.Serial;

    /// <summary>
    /// <para>设备品牌（ro.product.brand）。</para>
    /// Device brand (ro.product.brand).
    /// </summary>
    public string Brand { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>系统 SDK 版本（ro.build.version.sdk）。</para>
    /// System SDK version (ro.build.version.sdk).
    /// </summary>
    public string SdkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>设备代号（ro.product.device / fastboot product）。</para>
    /// Device codename (ro.product.device / fastboot product).
    /// </summary>
    public string CodeName { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>CPU ABI（ro.product.cpu.abi）。</para>
    /// CPU ABI (ro.product.cpu.abi).
    /// </summary>
    public string CpuAbi { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>SoC/主板平台代号（ro.board.platform）。</para>
    /// SoC/board platform code (ro.board.platform).
    /// </summary>
    public string CpuCode { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>内核版本（uname -r）。</para>
    /// Kernel version (uname -r).
    /// </summary>
    public string Kernel { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>屏幕分辨率（wm size）。</para>
    /// Screen resolution (wm size).
    /// </summary>
    public string Resolution { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>屏幕像素密度（wm density）。</para>
    /// Screen pixel density (wm density).
    /// </summary>
    public string Density { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>主板/SoC 序列号。</para>
    /// Board/SoC serial number.
    /// </summary>
    public string BoardId { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>Bootloader 锁定状态。</para>
    /// Bootloader lock state.
    /// </summary>
    public string BlStatus { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>当前 A/B 槽位（如 _a/_b）。</para>
    /// Current A/B slot (e.g. _a/_b).
    /// </summary>
    public string Slot { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>开机时长（uptime）。</para>
    /// Power-on time (uptime).
    /// </summary>
    public string PowerOnTime { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>编译/构建增量版本号。</para>
    /// Compile/build incremental version.
    /// </summary>
    public string CompileVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>VNDK 版本。</para>
    /// VNDK version.
    /// </summary>
    public string VndkVersion { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>存储类型（UFS/EMMC，来自 fastboot variant）。</para>
    /// Storage type (UFS/EMMC, from fastboot variant).
    /// </summary>
    public string DiskType { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>fastboot 是否为 user-space（fastbootd）。</para>
    /// Whether fastboot is user-space (fastbootd).
    /// </summary>
    public string IsUserspace { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>电池电量百分比，-1 表示不可用。</para>
    /// Battery level percent (-1 = unavailable).
    /// </summary>
    public int BatteryLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasBattery));
            }
        }
    } = -1;

    /// <summary>电池信息是否可用。 Whether battery info is available.</summary>
    public bool HasBattery => BatteryLevel >= 0;

    /// <summary>
    /// <para>电池状态文本（电压/温度）。</para>
    /// Battery status text (voltage/temperature).
    /// </summary>
    public string BatteryText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>内存占用百分比，-1 表示不可用。</para>
    /// Memory usage percent (-1 = unavailable).
    /// </summary>
    public int MemLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasMem));
            }
        }
    } = -1;

    /// <summary>内存信息是否可用。 Whether memory info is available.</summary>
    public bool HasMem => MemLevel >= 0;

    /// <summary>
    /// <para>内存使用文本（已用/总量）。</para>
    /// Memory usage text (used/total).
    /// </summary>
    public string MemText { get; set => SetProperty(ref field, value); } = string.Empty;

    /// <summary>
    /// <para>磁盘占用百分比，-1 表示不可用。</para>
    /// Disk usage percent (-1 = unavailable).
    /// </summary>
    public int DiskLevel
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasDisk));
            }
        }
    } = -1;

    /// <summary>磁盘信息是否可用。 Whether disk info is available.</summary>
    public bool HasDisk => DiskLevel >= 0;

    /// <summary>
    /// <para>资源使用信息（电池/内存/存储）是否至少一项可用。</para>
    /// Whether at least one of battery/memory/storage resource metrics is available.
    /// </summary>
    public bool HasResourceInfo => HasSystemInfo && (HasBattery || HasMem || HasDisk);

    /// <summary>
    /// <para>磁盘占用文本（已用/总量）。</para>
    /// Disk usage text (used/total).
    /// </summary>
    public string DiskText { get; set => SetProperty(ref field, value); } = string.Empty;
}