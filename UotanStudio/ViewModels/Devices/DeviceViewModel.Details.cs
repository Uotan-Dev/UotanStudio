using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
    /// <summary>
    /// <para>MTK 只读身份查询的协作式时限；到点即放弃，不让一次扫描被设备拖住。</para>
    /// Cooperative bound for the MTK read-only identity query, so one scan is never held up by a device.
    /// </summary>
    private const int MtkIdentityTimeoutMs = 5000;

    /// <summary>
    /// <para>按设备协议分发到对应的信息丰富方法。</para>
    /// Dispatches to the mode-specific enrichment method based on the device protocol.
    /// </summary>
    private Task EnrichDeviceInfoAsync(DeviceInfo device) => device.Protocol switch
    {
        DeviceProtocolKind.Adb => EnrichAdbInfoAsync(device),
        DeviceProtocolKind.Hdc => EnrichHdcInfoAsync(device),
        DeviceProtocolKind.Fastboot => EnrichFastbootInfoAsync(device),
        DeviceProtocolKind.Edl => EnrichEdlInfoAsync(device),
        DeviceProtocolKind.Mtk => EnrichMtkInfoAsync(device),
        DeviceProtocolKind.Rkdl => Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// <para>仅对 Boot ROM/Preloader 描述符状态的 MTK 目标做一次只读握手，取得硬件代码与安全位。
    /// 握手阶段不喂狗、不写内存、不绕过认证、不复位；已查询过或状态不属于可安全握手的取值时直接跳过。</para>
    /// Performs one read-only handshake against MTK targets whose descriptors report Boot ROM or
    /// preloader state, recording the hardware code and security bits. The handshake feeds no
    /// watchdog, writes no memory, bypasses no authentication and resets nothing; targets already
    /// queried or in any other state are skipped.
    /// </summary>
    /// <param name="device">The device entry to enrich. <para>待丰富的设备条目。</param>
    /// <returns>A task completing after the query or its bounded failure. <para>查询或其受限失败后完成的任务。</returns>
    private async Task EnrichMtkInfoAsync(DeviceInfo device)
    {
        if (!ShouldQueryMtkIdentity(device.Device.State, device.HasMtkInfo))
        {
            return;
        }

        var endpoint = device.Device;
        try
        {
            using var timeout = new CancellationTokenSource(MtkIdentityTimeoutMs);
            var info = await App.DeviceMonitor.RunMtkAsync(endpoint,
                session => session.InitializeAsync(timeout.Token), timeout.Token).ConfigureAwait(true);
            if (ReferenceEquals(device.Device, endpoint))
            {
                device.SetMtkInfo(info);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
                $"MTK identity query for {endpoint.Key} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>决定是否对一个 MTK 端点发起只读身份握手：仅限描述符状态为 Boot ROM 或 Preloader、
    /// 且尚未查询过的目标；DA 运行态、日志态与 adb 态一律不发协议字节。</para>
    /// Decides whether to run the read-only identity handshake for an MTK endpoint: only descriptor
    /// states Boot ROM or preloader, and only before a successful query; DA-runtime, logging and adb
    /// states never receive protocol bytes.
    /// </summary>
    /// <param name="state">The descriptor-derived state reported by discovery. <para>发现层给出的描述符状态。</param>
    /// <param name="hasMtkInfo">Whether identity info was already recorded. <para>身份信息是否已记录。</param>
    /// <returns><see langword="true"/> when the handshake may run. <para>可以握手时为 true。</returns>
    internal static bool ShouldQueryMtkIdentity(string state, bool hasMtkInfo)
        => !hasMtkInfo && state is "brom" or "preloader";

    private async Task EnrichEdlInfoAsync(DeviceInfo device)
    {
        var endpoint = device.Device;
        try
        {
            var state = await App.DeviceMonitor.ProbeEdlProtocolAsync(endpoint);
            if (ReferenceEquals(device.Device, endpoint))
            {
                device.EdlProtocol = state;
                device.SetEdlDetails(App.DeviceMonitor.GetEdlDetails(endpoint));
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(device.Device, endpoint))
            {
                device.EdlProtocol = UotanStudio.Comm.Protocols.Edl.EdlProtocolState.Unknown;
            }
            LogService.Instance.Write(LogLevel.Debug, "DeviceManager",
                $"EDL protocol detection for {endpoint.Key} failed: {ex.Message}");
        }
    }
    /// <summary>
    /// <para>拉取 ADB 设备扩展信息（getprop / 系统级 / 电池 / 内存 / 存储）。官方 adb server 会枚举
    /// USB 与网络设备，因此无论传输类型都优先经 Comm 的 adb server 服务执行 shell；
    /// 仅在 server 连接被拒绝时使用统一会话入口解析 USB 直连。</para>
    /// Retrieves extended ADB device info (getprop / system-level / battery / memory / storage).
    /// The official adb server enumerates both USB and network devices, so regardless of transport
    /// the shell runs via Comm's adb server service first; only when the server
    /// connection is refused does the shared session route resolve direct USB.
    /// </summary>
    private async Task EnrichAdbInfoAsync(DeviceInfo device)
    {
        var endpoint = device.Device;
        Dictionary<string, string>? info = null;
        try
        {
            info = await Task.Run(() =>
                QueryViaAdbServer(device)
                ?? QueryViaAdbSession(device)
            ).ConfigureAwait(true);
        }
        catch
        {
        }

        if (info is null || !ReferenceEquals(device.Device, endpoint))
        {
            return;
        }

        ApplyAdbProperties(device, info);
        device.HasSu = await FlashSuService.HasSuAsync(device.Device, CancellationToken.None);
        if (SelectedDevice == device)
        {
            OnPropertyChanged(nameof(DeviceDetail));
        }
        if (SelectedDevice == device)
        {
            OnPropertyChanged(nameof(DeviceDetail));
        }
    }

    /// <summary>
    /// <para>经 Comm 的 adb server 服务通过官方 adb server（127.0.0.1:5037）按序列号匹配设备并拉取属性；
    /// 仅 server 连接被拒绝时返回 null；缺失目标必须报错。</para>
    /// Queries device properties via Comm's adb server service against the official adb server
    /// (127.0.0.1:5037), matching the device by serial; returns null only when the server connection is refused.
    /// </summary>
    private static Dictionary<string, string>? QueryViaAdbServer(DeviceInfo device)
    {
        var monitor = App.DeviceMonitor;
        if (!monitor.HasAdbServer ||
            !device.Device.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            !UotanStudio.Comm.Devices.Services.DebugBridgeSessionOpener.AdbServerOwns(device.Device.Serial))
        {
            return null;
        }

        monitor.RequireAdbServerDevice(device.Device);
        return DevicePropertyReader.ReadAdbProperties(cmd =>
        {
            try
            {
                return monitor.ExecuteAdbServerShell(device.Device, cmd);
            }
            catch
            {
                return string.Empty;
            }
        }, LanguageService.Instance.GetString);
    }

    /// <summary>
    /// <para>通过 UotanStudio.Comm 原生 USB ADB 会话拉取设备属性；这是尽力而为的查询：
    /// 端点被 logcat 捕获租用（already leased）、设备正处于重枚举窗口（端点暂不可用）或
    /// 会话超时都按"暂无信息"处理并返回 null，不向调用方抛错。</para>
    /// Queries device properties via a UotanStudio.Comm native USB ADB session; this is a
    /// best-effort query: the endpoint being leased by a logcat capture (already leased), a
    /// re-enumeration window (endpoint temporarily unavailable) or a session timeout all mean
    /// "no info right now" and return null instead of throwing to the caller.
    /// </summary>
    private static Dictionary<string, string>? QueryViaAdbSession(DeviceInfo device)
    {
        try
        {
            return App.DeviceMonitor.RunAdb(device.Device,
                session => DevicePropertyReader.ReadAdbProperties(
                    cmd => RunShell(session, cmd), LanguageService.Instance.GetString));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// <para>通过 UotanStudio.Comm 的 Fastboot 会话查询 getvar 变量丰富设备信息。</para>
    /// Queries fastboot getvar variables via a UotanStudio.Comm session to enrich device info.
    /// </summary>
    private async Task EnrichFastbootInfoAsync(DeviceInfo device)
    {
        var endpoint = device.Device;
        Dictionary<string, string>? info = null;
        try
        {
            info = await Task.Run(() =>
                App.DeviceMonitor.RunFastboot(endpoint, session =>
                {
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
                }, CancellationToken.None)).ConfigureAwait(true);
        }
        catch
        {
        }

        if (info is null || !ReferenceEquals(device.Device, endpoint))
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
    private static string RunShell(UotanStudio.Comm.Contracts.IAdbDeviceSession session, string command)
        => System.Text.Encoding.UTF8.GetString(session.ExecuteCommand(command).Stdout);

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

        device.Slot = DevicePropertyReader.ParseSlot(info["slot"]);
    }

}