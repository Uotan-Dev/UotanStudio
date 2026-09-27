using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
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
        DeviceProtocolKind.Rkdl => Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    private async Task EnrichEdlInfoAsync(DeviceInfo device)
    {
        var endpoint = device.Device;
        try
        {
            var state = await App.DeviceMonitor.ProbeEdlProtocolAsync(endpoint);
            if (ReferenceEquals(device.Device, endpoint))
            {
                device.EdlProtocol = state;
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
            // 属性拉取失败不影响主流程（设备可能未授权）。
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
            // 属性拉取失败不影响主流程。
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