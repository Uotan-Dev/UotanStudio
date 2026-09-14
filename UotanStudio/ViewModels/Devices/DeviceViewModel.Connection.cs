using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
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
}