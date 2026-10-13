using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary>
    /// Removes the selected image from the list and moves its project file to
    /// <c>.deleted/</c>, recording reversible history (undo moves it back).
    /// <para>将选中镜像从列表移除并把项目内文件移动到 <c>.deleted/</c>，登记可撤销历史（撤销移回）。</para>
    /// </summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() => RunExclusiveAsync(DeleteSelectedCoreAsync);

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        if (IsOperating) return;
        IsOperating = true;
        try { await operation(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Images", ex.Message); }
        finally { IsOperating = false; }
    }

    private async Task DeleteSelectedCoreAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Delete aborted: no project is open  删除中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        ReportItemStatistics(0, selectedImages.Count);
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Deleting {image.DisplayName}  正在删除 {image.DisplayName}";
            await DeleteImageAsync(image);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
            ReportItemStatistics(i + 1, selectedImages.Count);
        }

        if (SelectedImage is not null && !Images.Contains(SelectedImage))
        {
            SelectedImage = null;
        }
    }

    internal async Task DeleteImageAsync(ImageEntryViewModel image)
    {
        if (image.IsDirectory)
        {
            await DeleteUnpackedDirectoryAsync(image);
            return;
        }

        var source = GetSelectedSourcePath(image, "Delete");
        if (source is null)
        {
            return;
        }

        try
        {
            var subProjectRoot = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", "Delete aborted: no active sub-project  删除中止：无活动子项目");
                return;
            }

            var deletedDir = Path.Combine(subProjectRoot, ".deleted");
            Directory.CreateDirectory(deletedDir);
            var destination = Path.Combine(deletedDir, GetUniqueFileName(deletedDir, Path.GetFileName(source)));
            await ProjectFiles.MoveAsync(source, destination);
            Images.Remove(image);
            if (ReferenceEquals(SelectedImage, image)) SelectedImage = null;
            LogService.Instance.Write(LogLevel.Info, "Images", $"Deleted {image.DisplayName} -> .deleted  已删除 {image.DisplayName}");
            var relativeDestination = ProjectContextService.Instance.RelativePath(destination);
            if (relativeDestination is null) throw new IOException("The deleted image is outside the project.");
            OperationHistoryService.Instance.Record(
                $"Delete {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.DeleteImage,
                    Description = $"Delete {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    RelativePath = image.RelativePath,
                    DestinationPath = relativeDestination
                },
                () =>
                {
                    File.Move(destination, source);
                    Images.Add(image);
                },
                () =>
                {
                    File.Move(source, destination);
                    Images.Remove(image);
                });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Delete failed: {ex.Message}  删除失败：{ex.Message}");
        }
    }

    internal async Task DeleteUnpackedDirectoryAsync(ImageEntryViewModel image)
    {
        var owner = TopLevelExtensions.GetTopLevel() as Avalonia.Controls.Window;
        var context = ProjectContextService.Instance;
        var root = context.ActiveSubProject?.RootPath;
        if (owner is null || root is null)
        {
            return;
        }

        var language = LanguageService.Instance;
        try
        {
            var source = context.ResolvePath(image.RelativePath) ?? throw new IOException("Invalid project path.");
            var directory = new ImageOperationPaths(source, image.IsDirectory, root, root).UnpackedDirectory;

            if (!Directory.Exists(directory))
            {
                await DialogService.ShowAsync(owner, language.GetString("Image_UnpackedDirectoryMissingTitle"),
                    string.Format(language.GetString("Image_UnpackedDirectoryMissingMessage"), directory), Ursa.Controls.MessageBoxIcon.Information);
                return;
            }

            if (!await DialogService.ConfirmAsync(owner, language.GetString("Dialog_ConfirmDelete"),
                    string.Format(language.GetString("Dialog_ConfirmDeleteMessage"), directory),
                    language.GetString("Menu_Delete")))
            {
                return;
            }

            await Task.Run(() => Directory.Delete(directory, recursive: true));
            image.IsUnpacked = false;
            if (image.IsDirectory)
            {
                Images.Remove(image);
                if (ReferenceEquals(SelectedImage, image)) SelectedImage = null;
            }
            StatusText = language.GetString("ImageView_OperationDone");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Deleted unpacked directory: {directory}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Delete unpacked directory failed: {ex.Message}");
            await DialogService.ShowAsync(owner, language.GetString("Dialog_DeleteFailed"), global::UotanStudio.Services.UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Builds a super image from the project's raw/sparse images using the LP metadata builder and the
    /// sparse writer, outputting to <c>output/</c> and recording history.
    /// <para>使用 LP 元数据构建器与 sparse 写入器将项目内 raw/sparse 镜像合成为 super 镜像，输出到
    /// <c>output/</c> 并登记历史。</para>
    /// </summary>
    [RelayCommand]
    private Task SynthesizeSuperAsync() => RunExclusiveAsync(SynthesizeSuperCoreAsync);

    private async Task SynthesizeSuperCoreAsync()
    {
        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize super aborted: no project is open  合成 super 中止：尚未打开工程");
            return;
        }

        var settings = ProjectSettingViewModel.EnsureInstance();
        List<SuperPartitionInput> partitions = [];
        foreach (var image in Images.Where(image => image.IsSelected))
        {
            if (image.Format is not ("raw" or "sparse"))
            {
                continue;
            }

            var path = ProjectContextService.Instance.ResolvePath(image.RelativePath);
            if (path is not null && File.Exists(path))
            {
                partitions.Add(new SuperPartitionInput(Path.GetFileNameWithoutExtension(image.RelativePath), path));
            }
        }

        if (partitions.Count == 0)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize super aborted: no raw/sparse images in project  合成 super 中止：项目中无 raw/sparse 镜像");
            return;
        }

        StatusText = $"Synthesizing super image ({partitions.Count} partitions)  正在合成 super 镜像（{partitions.Count} 个分区）";
        ProgressPercent = -1;
        try
        {
            var outputDir = ProjectContextService.Instance.EnsureOutputDirectory();
            var outputPath = Path.Combine(outputDir, "super.img");
            var hadExistingOutput = File.Exists(outputPath);
            if (!await ConfirmPackOverwriteAsync(hadExistingOutput ? [outputPath] : [])) return;
            var deviceSize = ulong.TryParse(settings.SUPERSize, out var parsed) && parsed > 0 ? parsed : 9_126_805_504UL;
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing super image with {partitions.Count} partition(s)  正在合成 super 镜像（{partitions.Count} 个分区）");
            var result = await _firmware.SynthesizeSuperAsync(partitions, outputPath, deviceSize, settings.SUPERName);
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                StatusText = global::UotanStudio.Services.LanguageService.Instance.GetString("UserError_SuperFailed");
                ProgressPercent = 0;
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            StatusText = $"Synthesized super image  已合成 super 镜像";
            ProgressPercent = 100;
            if (!hadExistingOutput && File.Exists(outputPath)
                && ImageViewModel.CreateOutputOperation(HistoryOperationKind.SynthesizeSuper,
                    "Synthesize super image", [outputPath]) is { } operation)
            {
                OperationHistoryService.Instance.Record(
                    operation.Description, operation,
                    () => ImageViewModel.UndoOutput(operation),
                    () => ImageViewModel.RedoOutput(operation));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Synthesize super failed: {ex.Message}  合成 super 失败：{ex.Message}");
            StatusText = global::UotanStudio.Services.UserError.Describe(ex, "UserError_SuperFailed");
            ProgressPercent = 0;
        }
    }

    /// <summary>
    /// Flashes the clicked or checked flashable images (boot/raw/sparse/ext4/erofs/f2fs/super) to the currently selected device
    /// in order: the partition name is the image file name without its extension, and compound names such as
    /// <c>system_ext_a</c> are preserved verbatim (this layer never splits or slot-suffixes them); slot
    /// resolution is delegated to <c>FastbootSession.ResolvePartitionWithSlot</c> which flashes an
    /// explicit slot suffix unchanged. Each partition is flashed sequentially via
    /// <see cref="BatchFlashService.FlashSelectedAsync"/> which reuses <see cref="FlashService.FlashImageAsync"/>,
    /// a single-partition failure does not stop the rest, and progress/results are reported to the status text.
    /// <para>把右键条目或用户勾选的可刷写镜像（boot/raw/sparse/ext4/erofs/f2fs/super）刷入当前选中设备：分区名取镜像文件名去扩展名，
    /// 多段分区名（如 <c>system_ext_a</c>）原样保留，不由本层拆分或追加槽位后缀；槽位解析交给
    /// <c>FastbootSession.ResolvePartitionWithSlot</c> 处理（已带显式槽位后缀的名按原样刷写）。
    /// 逐分区复用 <see cref="FlashService.FlashImageAsync"/> 执行，单分区失败会继续，进度与结果回报到状态文本。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFlashSelected))]
    private Task FlashSelectedAsync(ImageEntryViewModel? image = null) => RunExclusiveAsync(() => FlashSelectedCoreAsync(image));

    private async Task FlashSelectedCoreAsync(ImageEntryViewModel? clicked)
    {
        ProgressPercent = 0;

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no project is open  选中文件刷入中止：尚未打开工程");
            return;
        }

        var device = DeviceViewModel.Instance?.SelectedDevice;
        if (device is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no device selected  选中文件刷入中止：未选中设备");
            return;
        }

        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: selected device is not fastboot  选中文件刷入中止：当前选中设备非 fastboot");
            return;
        }

        List<(string Partition, string ImagePath)> entries = [];
        var selected = clicked is null ? GetSelectedImages() : [clicked];
        foreach (var image in selected)
        {

            if (image.IsDirectory
                || image.Format is not ("raw" or "sparse" or "ext4" or "erofs" or "f2fs" or "super")
                && !ImageInputService.IsBootFamilyFormat(image.Format))
            {
                continue;
            }

            var path = ProjectContextService.Instance.ResolvePath(image.RelativePath);
            if (path is null || !File.Exists(path))
            {
                LogService.Instance.Write(LogLevel.Warn, "Images", $"Flash selected skipped missing file: {image.RelativePath}  选中文件刷入跳过缺失文件：{image.RelativePath}");
                continue;
            }

            entries.Add((GetEntryBaseName(image), path));
        }

        if (entries.Count == 0)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no flashable images selected  选中文件刷入中止：未勾选可刷写镜像");
            return;
        }

        StatusText = $"Flashing {entries.Count} image(s)  正在刷入 {entries.Count} 个镜像";
        try
        {
            var progress = new Progress<FlashProgress>(fp =>
            {
                var percent = Math.Clamp(fp.Percent, 0, 100);
                ProgressPercent = percent;
                StatusText = $"[{percent}%] {fp.Status}";
            });
            var result = await BatchFlashService.FlashSelectedAsync(device, entries, progress, CancellationToken.None).ConfigureAwait(true);
            if (result.Success)
            {
                ProgressPercent = 100;
            }
            StatusText = result.Summary;
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Warn, "Images", result.Summary);
        }
        catch (Exception ex)
        {
            ProgressPercent = 0;
            StatusText = global::UotanStudio.Services.UserError.Describe(ex, "UserError_FlashFailed");
            LogService.Instance.Write(LogLevel.Error, "Images", $"Flash selected failed: {ex.Message}  选中文件刷入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Whether the "flash selected" command is enabled: it can only run when the currently
    /// selected device uses the Fastboot protocol, which drives the button's enabled state.
    /// <para>「刷入选中文件」命令是否可用：仅当当前选中设备为 Fastboot 协议时才可执行，
    /// 以此作为按钮禁用的判定条件。</para>
    /// </summary>
    private bool CanFlashSelected(ImageEntryViewModel? image)
        => DeviceViewModel.Instance?.SelectedDevice is { } d
           && d.Protocol == DeviceProtocolKind.Fastboot;

    /// <summary>
    /// Builds DAT output for the clicked or checked entries.
    /// <para>为右键或勾选条目构建 DAT。</para>
    /// </summary>
    [RelayCommand]
    private Task SynthesizeDatAsync(ImageEntryViewModel? image = null) => RunImagesAsync("dat", image);
}