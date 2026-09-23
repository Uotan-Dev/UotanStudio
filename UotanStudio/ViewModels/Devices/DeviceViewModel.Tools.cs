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
            DeviceMonitorService.Instance.OpenInteractiveShell(device.Device);
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
                DeviceMonitorService.Instance.RunFastboot(device.Device, fb =>
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
