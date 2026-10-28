using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Devices;

public partial class DeviceViewModel
{
    /// <summary>
    /// Quick flash entry: picks an image and flashes it to the given partition (default "boot") on
    /// the selected device via <see cref="FlashService.FlashImageAsync"/>, reporting progress and
    /// results to the status bar and unified log. User confirmation is requested through
    /// DialogService before flashing.
    /// <para>快速刷写入口：为选中设备选择镜像并刷写到指定分区（默认 boot），经
    /// <see cref="FlashService.FlashImageAsync"/> 执行，进度与结果回报到状态栏与统一日志。
    /// 刷写前会经 DialogService 请求用户确认。</para>
    /// </summary>
    /// <param name="partition">The target partition; "boot" when null. <para>目标分区名；null 时使用 "boot"。</para></param>
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
            Title = _localizer.GetString("Device_QuickFlash"),
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
            _localizer.GetString("Device_FlashConfirmTitle"),
            _localizer.GetString("Device_FlashConfirmMessage") + $"\n{device.Serial}\n{partitionName}\n{imagePath}"))
        {
            return;
        }

        StatusText = $"{_localizer.GetString("Flash_Flashing")} {partitionName}...";
        try
        {
            var progress = new Progress<FlashProgress>(fp =>
            {
                var percent = Math.Clamp(fp.Percent, 0, 100);
                StatusText = $"[{percent}%] {fp.Status}";
            });
            var result = await FlashService.Default.FlashImageAsync(device, partitionName, imagePath, progress, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{_localizer.GetString("Flash_Done")} {partitionName}"
                : $"{_localizer.GetString("Flash_Failed")} {partitionName}: {result.Summary}";
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Quick flash {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Flash_Failed")} {partitionName}";
            _log.Write(LogLevel.Error, "DeviceManager", $"Quick flash {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Backup entry: picks an output path for the selected Fastboot device via a save-file dialog,
    /// opens a fastboot session and backs up the given partition through
    /// <see cref="BackupService.BackupFastbootPartition"/>, reporting progress to the status bar
    /// and writing the result back to it.
    /// <para>备份分区入口：为选中 Fastboot 设备经 <c>SaveFileDialog</c> 选择输出路径，
    /// 打开 fastboot 会话后调用 <see cref="BackupService.BackupFastbootPartition"/> 备份指定分区，
    /// 进度回报状态栏与统一日志，结果写状态栏。</para>
    /// </summary>
    /// <param name="partition">The target partition; "boot" when null. <para>目标分区名；null 时使用 "boot"。</para></param>
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
            Title = _localizer.GetString("Backup_SaveTitle"),
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
        StatusText = $"{_localizer.GetString("Backup_BackingUp")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await BackupService.BackupPartitionAsync(
                device, partitionName, outPath, progress, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{_localizer.GetString("Backup_Done")} {partitionName}"
                : $"{_localizer.GetString("Backup_Failed")} {partitionName}: {result.Summary}";
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Backup {partitionName} from {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Backup_Failed")} {partitionName}";
            _log.Write(LogLevel.Error, "DeviceManager", $"Backup {partitionName} from {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore entry: picks an image for the selected Fastboot device via an open-file dialog,
    /// opens a fastboot session and restores the given partition through
    /// <see cref="BackupService.RestoreFastbootPartition"/>, reporting progress to the status bar
    /// and writing the result back to it.
    /// <para>还原分区入口：为选中 Fastboot 设备经 <c>OpenFileDialog</c> 选择镜像文件，
    /// 打开 fastboot 会话后调用 <see cref="BackupService.RestoreFastbootPartition"/> 刷回指定分区，
    /// 进度回报状态栏与统一日志，结果写状态栏。</para>
    /// </summary>
    /// <param name="partition">The target partition; "boot" when null. <para>目标分区名；null 时使用 "boot"。</para></param>
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
            Title = _localizer.GetString("Backup_OpenTitle"),
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
        StatusText = $"{_localizer.GetString("Backup_Restoring")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await BackupService.RestorePartitionAsync(
                device, partitionName, imagePath, progress, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{_localizer.GetString("Backup_RestoreDone")} {partitionName}"
                : $"{_localizer.GetString("Backup_RestoreFailed")} {partitionName}: {result.Summary}";
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"Restore {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Backup_RestoreFailed")} {partitionName}";
            _log.Write(LogLevel.Error, "DeviceManager", $"Restore {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// EDL backup core flow: picks a rawprogram XML for partition geometry plus an output path,
    /// opens an EDL session and streams the partition backup through
    /// <see cref="BackupService.ResolveEdlPartitionGeometry"/> and
    /// <see cref="BackupService.BackupEdlPartitionAsync"/>, reporting progress to the status bar
    /// and the unified log.
    /// <para>EDL 备份核心流程：选择 rawprogram XML 解析分区几何与输出路径，打开 EDL 会话后经
    /// <see cref="BackupService.ResolveEdlPartitionGeometry"/> 与 <see cref="BackupService.BackupEdlPartitionAsync"/>
    /// 流式备份指定分区，进度回报状态栏与统一日志。</para>
    /// </summary>
    /// <param name="device">The selected EDL device. <para>选中的 EDL 设备。</para></param>
    /// <param name="partitionName">The target partition name. <para>目标分区名。</para></param>
    private async Task BackupEdlPartitionCoreAsync(DeviceInfo device, string partitionName)
    {
        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var rawProgramFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Backup_SelectRawProgram"),
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
            Title = _localizer.GetString("Backup_SaveTitle"),
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
        StatusText = $"{_localizer.GetString("Backup_BackingUp")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var result = await BackupService.BackupEdlPartitionAsync(
                device, partitionName, outPath, rawProgramPath, progress, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{_localizer.GetString("Backup_Done")} {partitionName}"
                : $"{_localizer.GetString("Backup_Failed")} {partitionName}: {result.Summary}";
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"EDL backup {partitionName} from {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Backup_Failed")} {partitionName}";
            _log.Write(LogLevel.Error, "DeviceManager", $"EDL backup {partitionName} from {device.Serial} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// EDL restore core flow: picks a rawprogram XML, resolves the target partition entry,
    /// opens an EDL session and restores the partition through
    /// <see cref="BackupService.RestoreEdlPartitionAsync"/> (the image is located via the
    /// rawprogram entry), reporting progress to the status bar and the unified log.
    /// <para>EDL 还原核心流程：选择 rawprogram XML，解析出目标分区条目后打开 EDL 会话，经
    /// <see cref="BackupService.RestoreEdlPartitionAsync"/> 刷回该分区（镜像文件由 rawprogram
    /// 条目引用定位），进度回报状态栏与统一日志。</para>
    /// </summary>
    /// <param name="device">The selected EDL device. <para>选中的 EDL 设备。</para></param>
    /// <param name="partitionName">The target partition name. <para>目标分区名。</para></param>
    private async Task RestoreEdlPartitionCoreAsync(DeviceInfo device, string partitionName)
    {
        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var rawProgramFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Backup_SelectRawProgram"),
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
        StatusText = $"{_localizer.GetString("Backup_Restoring")} {partitionName}...";
        try
        {
            var progress = new Progress<BackupProgress>(bp =>
            {
                var percent = Math.Clamp(bp.Percent, 0, 100);
                StatusText = $"[{percent}%] {bp.Status}";
            });
            var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
            var entry = FlashService.FindEdlTarget(entries, partitionName, imagePath: null)
                ?? throw new InvalidOperationException(
                    $"Partition '{partitionName}' not found in rawprogram: {rawProgramPath}.");
            var result = await App.DeviceMonitor.RunEdlAsync(device.Device,
                session => BackupService.RestoreEdlPartitionAsync(
                    session, entry, imageDir, progress, CancellationToken.None)).ConfigureAwait(true);
            StatusText = result.Success
                ? $"{_localizer.GetString("Backup_RestoreDone")} {partitionName}"
                : $"{_localizer.GetString("Backup_RestoreFailed")} {partitionName}: {result.Summary}";
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceManager",
                $"EDL restore {partitionName} to {device.Serial}: {(result.Success ? "OK" : result.Summary)}");
        }
        catch (Exception ex)
        {
            StatusText = $"{_localizer.GetString("Backup_RestoreFailed")} {partitionName}";
            _log.Write(LogLevel.Error, "DeviceManager", $"EDL restore {partitionName} to {device.Serial} failed: {ex.Message}");
        }
    }
}