using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
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
}
