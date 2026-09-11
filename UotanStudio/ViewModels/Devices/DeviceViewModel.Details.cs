using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

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
            .FirstOrDefault(d => string.Equals(d.Serial, device.Serial, StringComparison.Ordinal));
        return target is null
            ? null
            : DevicePropertyReader.ReadAdbProperties(cmd =>
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
        }, LanguageService.Instance.GetString);
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
                ? DevicePropertyReader.ReadAdbProperties(cmd => RunShell(session, cmd), LanguageService.Instance.GetString)
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
