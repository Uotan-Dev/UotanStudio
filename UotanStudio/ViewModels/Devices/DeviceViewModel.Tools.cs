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
    /// Whether an ADB shell can be opened for the selected device (ADB-mode devices only).
    /// <para>是否可打开选中设备的 ADB Shell（仅 ADB 模式设备）。</para>
    /// </summary>
    public bool CanOpenAdbShell => SelectedDevice?.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc;

    /// <summary>
    /// Whether the selected Fastboot device's partition/getvar info can be read.
    /// <para>是否可读取选中设备的 Fastboot 分区/getvar 信息（仅 Fastboot 模式设备）。</para>
    /// </summary>
    public bool CanFastbootReadInfo => SelectedDevice?.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>
    /// Whether quick flash is available for the selected device (ADB/Fastboot/EDL/RKDL).
    /// <para>是否可对选中设备执行快速刷写（ADB/Fastboot/EDL/RKDL 均支持）。</para>
    /// </summary>
    public bool CanQuickFlash => SelectedDevice?.Protocol is DeviceProtocolKind.Adb
        or DeviceProtocolKind.Fastboot
        or DeviceProtocolKind.Edl
        or DeviceProtocolKind.Rkdl;

    /// <summary>
    /// Whether partition backup/restore is available for the selected device
    /// (Fastboot / EDL mode devices).
    /// <para>是否可对选中设备执行分区备份/还原（Fastboot / EDL 模式设备）。</para>
    /// </summary>
    public bool CanBackupRestore => SelectedDevice?.Protocol is DeviceProtocolKind.Fastboot or DeviceProtocolKind.Edl;

    /// <summary>
    /// Whether scrcpy mirroring can start for the selected device (requires the ADB protocol).
    /// <para>是否可对选中设备开启 scrcpy 投屏（需要 ADB 协议设备）。</para>
    /// </summary>
    public bool CanScreenMirror => SelectedDevice?.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc;

    /// <summary>
    /// Whether the iOS tools (SSH channel, crash-log export) apply to the selected device.
    /// <para>选中设备是否适用 iOS 工具（SSH 通道、崩溃日志导出）。</para>
    /// </summary>
    public bool CanIosTools => SelectedDevice?.Protocol == DeviceProtocolKind.Ios;

    /// <summary>
    /// Opens an SSH channel to the selected jailbroken iOS device and reports the local port.
    /// <para>打开到选中越狱 iOS 设备的 SSH 通道并报告本地端口。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanIosTools))]
    private void OpenIosSshTunnel()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            var port = App.DeviceMonitor.StartSshTunnel(device.Device);
            StatusText = $"{LanguageService.Instance.GetString("Device_IosSshTunnelOpen")} 127.0.0.1:{port}";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager",
                $"SSH channel for {device.Serial} forwarding 127.0.0.1:{port} to device port 22.");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_IosSshTunnelOpen")} {device.Serial} " +
                LanguageService.Instance.GetString("UserError_Failed");
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"SSH channel for {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes the SSH channel of the selected iOS device.
    /// <para>关闭选中 iOS 设备的 SSH 通道。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanIosTools))]
    private void CloseIosSshTunnel()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        App.DeviceMonitor.StopSshTunnel(device.Device);
        StatusText = LanguageService.Instance.GetString("Device_IosSshTunnelClose");
        LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"SSH channel for {device.Serial} closed.");
    }

    /// <summary>
    /// Starts the AirPlay receiving endpoint so the selected iOS device can mirror to this machine; the
    /// endpoint is read-only, so no control messages follow.
    /// <para>启动 AirPlay 接收端点，使选中的 iOS 设备可以投屏到本机；该端点只读，之后不发控制消息。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanIosTools))]
    private void StartIosAirPlayReceiver()
    {
        try
        {
            var session = App.DeviceMonitor.StartAirPlayReceiver(text =>
                LogService.Instance.Write(LogLevel.Info, "DeviceManager", text));
            StatusText = $"{LanguageService.Instance.GetString("Device_IosAirPlayReceive")} 127.0.0.1:{session.RtspPort}";
        }
        catch (Exception ex)
        {
            StatusText = LanguageService.Instance.GetString("Device_IosAirPlayReceive") + " " +
                LanguageService.Instance.GetString("UserError_Failed");
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"AirPlay receiver failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Exports the selected device's crash reports into the temp materialization directory.
    /// <para>把选中设备的崩溃报告导出到临时物化目录。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanIosTools))]
    private void ExportIosCrashLogs()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        StatusText = $"{LanguageService.Instance.GetString("Device_IosExportCrashLogs")} {device.Serial}...";
        try
        {
            var directory = App.DeviceMonitor.ExportCrashLogs(device.Device);
            StatusText = $"{LanguageService.Instance.GetString("Device_IosExportCrashLogs")} {directory}";
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_IosExportCrashLogs")} {device.Serial} " +
                LanguageService.Instance.GetString("UserError_Failed");
            LogService.Instance.Write(LogLevel.Error, "DeviceManager",
                $"Crash export for {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts mirroring for the selected device: scrcpy for ADB, ohscrcpy for HDC. Opens the
    /// workspace mirror document and sends the request message.
    /// <para>对选中的设备开启投屏：ADB 经 scrcpy，HDC 经 ohscrcpy。打开工作区投屏文档并发送请求消息。</para>
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
    /// Opens the selected device's interactive shell in the managed terminal dock.
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
            App.DeviceMonitor.OpenInteractiveShell(device.Device);
            StatusText = $"{LanguageService.Instance.GetString("Device_OpenAdbShell")} {device.Serial} OK";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"Open {device.Mode} shell for {device.Serial}.");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_OpenAdbShell")} {device.Serial} {LanguageService.Instance.GetString("UserError_Failed")}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Open {device.Mode} shell for {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads partition/getvar variables from the selected Fastboot device (product, secure, slot,
    /// boot partition size, etc.) into the status bar and the unified log.
    /// <para>读取选中 Fastboot 设备的分区/getvar 变量（product、secure、slot、boot 分区大小等），
    /// 结果写入状态栏与统一日志。</para>
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
                App.DeviceMonitor.RunFastboot(device.Device, fb =>
                {
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
                });
            }).ConfigureAwait(true);

            StatusText = $"{LanguageService.Instance.GetString("Device_FastbootReadInfo")} {device.Serial} OK";
            LogService.Instance.Write(LogLevel.Info, "DeviceManager", $"Fastboot getvar {device.Serial}:\n{sb.ToString().TrimEnd()}");
        }
        catch (Exception ex)
        {
            StatusText = $"{LanguageService.Instance.GetString("Device_FastbootReadInfo")} {device.Serial} {LanguageService.Instance.GetString("UserError_Failed")}";
            LogService.Instance.Write(LogLevel.Error, "DeviceManager", $"Fastboot read info {device.Serial} failed: {ex.Message}");
        }
    }
}