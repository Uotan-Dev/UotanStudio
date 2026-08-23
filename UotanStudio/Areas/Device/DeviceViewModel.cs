using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Device;

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

            var enrichable = Devices
                .Where(x => x.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Fastboot)
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
        DeviceProtocolKind.Fastboot => EnrichFastbootInfoAsync(device),
        // EDL/RKDL 处于 bootrom/loader 模式，无可拉取的系统属性。
        // EDL/RKDL stay in bootrom/loader mode with no system properties to retrieve.
        DeviceProtocolKind.Edl => Task.CompletedTask,
        DeviceProtocolKind.Rkdl => Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// <para>通过 UotanStudio.Comm 的 ADB 会话批量拉取设备属性（getprop / 系统级 / 电池 / 内存 / 存储），
    /// 用以丰富设备详细信息的展示。</para>
    /// Retrieves a broad set of device properties via a UotanStudio.Comm ADB session
    /// (getprop / system-level / battery / memory / storage) to enrich the detail view.
    /// </summary>
    private async Task EnrichAdbInfoAsync(DeviceInfo device)
    {
        Dictionary<string, string>? info = null;
        try
        {
            // 打开 USB 设备与执行 shell 均为同步阻塞调用（未授权设备可能挂到超时），
            // 整体放后台线程，避免扫描结束过渡到主窗时冻结 UI。
            // Opening the USB device and running shell commands are blocking calls
            // (unauthorized devices can stall until timeout); run on a background thread
            // so the splash-to-main transition never freezes the UI.
            info = await Task.Run(() =>
            {
                var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device);
                if (session is null)
                {
                    return null;
                }

                try
                {
                    session.Connect();
                    return session.Connection.IsConnected ? QueryAdbProperties(session) : null;
                }
                finally
                {
                    session.Dispose();
                }
            }).ConfigureAwait(true);
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
    private static Dictionary<string, string> QueryAdbProperties(AdbSession session)
    {
        string Get(string cmd)
        {
            return Clean(RunShell(session, cmd));
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
            ["resolution"] = ColonValue(RunShell(session, "wm size")),
            ["density"] = ColonValue(RunShell(session, "wm density")),
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

        var (bLevel, bText) = ParseBattery(RunShell(session, "dumpsys battery"));
        map["batteryLevel"] = bLevel.ToString(CultureInfo.InvariantCulture);
        map["batteryText"] = bText;

        var (mLevel, mText) = ParseMem(RunShell(session, "cat /proc/meminfo | grep -E 'MemTotal|MemAvailable'"));
        map["memLevel"] = mLevel.ToString(CultureInfo.InvariantCulture);
        map["memText"] = mText;

        var (dLevel, dText) = ParseDisk(RunShell(session, "echo; df /storage/emulated 2>/dev/null || df /data 2>/dev/null || df /sdcard 2>/dev/null"));
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
                DeviceProtocolKind.Adb => TryAdb(SelectedDevice),
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
        DeviceProtocolKind.Adb => "ADB",
        DeviceProtocolKind.Fastboot => "Fastboot",
        DeviceProtocolKind.Edl => "EDL",
        DeviceProtocolKind.Rkdl => "RKDL",
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
        _ => throw new ArgumentOutOfRangeException(nameof(Device.Transport), Device.Transport, "Unexpected transport kind."),
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
        _ => throw new ArgumentOutOfRangeException(nameof(Device.Transport), Device.Transport, "Unexpected transport kind."),
    };

    /// <summary>厂商/产品 USB ID（十六进制 VID:PID）。 Vendor/product USB ID.</summary>
    public string UsbIds => Device.VendorId == 0
        ? string.Empty
        : $"{Device.VendorId:X04}:{Device.ProductId:X04}";

    /// <summary>USB 厂商名（按 VendorId 查内置表，未知返回空）。 USB vendor name from the built-in VID table.</summary>
    public string VendorName => UsbVendorTable.TryGetVendor(Device.VendorId);

    /// <summary>是否为 ADB 设备。 Whether the device is in ADB mode.</summary>
    public bool IsAdb => Device.Protocol == DeviceProtocolKind.Adb;

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
    public bool HasResourceInfo => IsAdb && (HasBattery || HasMem || HasDisk);

    /// <summary>
    /// <para>磁盘占用文本（已用/总量）。</para>
    /// Disk usage text (used/total).
    /// </summary>
    public string DiskText { get; set => SetProperty(ref field, value); } = string.Empty;
}