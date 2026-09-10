using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
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
}
