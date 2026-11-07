using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.UiAbstractions;
using DiskFlashProgress = UotanStudio.DD.FlashProgress;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Disk-image flashing window view model: image probing, the single target drive, safety switches, the
/// privilege gate and write/verify progress.
/// <para>磁盘镜像烧录窗口视图模型：镜像探测、唯一目标驱动器、安全开关、权限门控与写入/校验进度。</para>
/// </summary>
/// <remarks>
/// The host process never needs an administrator token: when this process may not write the selected drive,
/// <see cref="IDiskFlashService"/> asks the user and then runs the write in a process started for that purpose.
/// <para>
/// 宿主进程本身不需要管理员令牌：本进程无权写入选中驱动器时，<see cref="IDiskFlashService"/> 询问用户后再由
/// 为此启动的进程执行写入。
/// </para>
/// </remarks>
public sealed partial class DiskFlashViewModel : ObservableObject
{
    private readonly IDiskFlashService _flashService;
    private readonly IDiskFlashDialogs _dialogs;
    private CancellationTokenSource? _operationCts;
    private FlashImage? _image;
    private SpacemiTCardPackage? _cardPackage;
    private volatile bool _synthesizing;
    private ProgressMeter? _meter;
    private FlashStage? _meterStage;
    private bool _isElevated;

    /// <summary>
    /// Identifies the current flash run so callbacks posted by <see cref="Progress{T}"/> after the run ended are
    /// ignored instead of touching state that has already been cleared.
    /// <para>标识当前烧录轮次，使 <see cref="Progress{T}"/> 在轮次结束后才投递的回调被忽略，而不是访问已清理的状态。</para>
    /// </summary>
    private int _progressGeneration;

    private static LanguageService Language => LanguageService.Instance;

    /// <summary>
    /// Creates the view model with the platform flash service unless alternatives are supplied.
    /// <para>创建视图模型；未提供替代实现时使用平台烧录服务。</para>
    /// </summary>
    /// <param name="flashService">Flash service; <see langword="null"/> uses <see cref="DiskFlashService.Default"/>.
    /// <para>烧录服务；<see langword="null"/> 时使用 <see cref="DiskFlashService.Default"/>。</para></param>
    /// <param name="dialogs">Message windows; <see langword="null"/> uses the detached fallback.
    /// <para>消息窗口；<see langword="null"/> 时使用脱离界面的回退实现。</para></param>
    public DiskFlashViewModel(IDiskFlashService? flashService = null, IDiskFlashDialogs? dialogs = null)
    {
        _flashService = flashService ?? DiskFlashService.Default;
        _dialogs = dialogs ?? NullDiskFlashDialogs.Instance;
        _isElevated = _flashService.HasWriteAccess;
        ProgressStage = Language.GetString("DiskFlash_Ready");
    }

    /// <summary>
    /// Gets the scanned drives; at most one of them is selected.
    /// <para>获取已扫描的驱动器；其中至多选中一个。</para>
    /// </summary>
    public ObservableCollection<DiskFlashDriveItem> Drives { get; } = new();

    /// <summary>
    /// Gets or sets the selected image path.
    /// <para>获取或设置已选择的镜像路径。</para>
    /// </summary>
    [ObservableProperty]
    private string _imagePath = string.Empty;

    /// <summary>
    /// Gets or sets the probed image format and size summary.
    /// <para>获取或设置已探测的镜像格式与大小摘要。</para>
    /// </summary>
    [ObservableProperty]
    private string _imageSummary = string.Empty;

    /// <summary>
    /// Gets or sets the status or feedback message.
    /// <para>获取或设置状态或反馈消息。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Gets or sets whether <see cref="StatusMessage"/> describes a failure.
    /// <para>获取或设置 <see cref="StatusMessage"/> 是否描述失败。</para>
    /// </summary>
    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>
    /// Gets or sets whether a scan or flash operation is running.
    /// <para>获取或设置是否正在执行扫描或烧录。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(CanFlash))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    /// <summary>
    /// Gets or sets whether the written bytes are read back and compared with the source.
    /// <para>获取或设置写入字节是否回读并与源镜像比较。</para>
    /// </summary>
    [ObservableProperty]
    private bool _verifyAfterWrite = true;

    /// <summary>
    /// Gets or sets whether targets far larger than the image may be written.
    /// <para>获取或设置是否允许写入远大于镜像的目标。</para>
    /// </summary>
    [ObservableProperty]
    private bool _allowLargeDrive;

    /// <summary>
    /// Gets or sets whether the user accepted that the selected drive will be overwritten.
    /// <para>获取或设置用户是否已接受选中驱动器将被覆盖。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFlash))]
    private bool _confirmOverwrite;

    /// <summary>
    /// Gets or sets the progress percentage from 0 to 100.
    /// <para>获取或设置 0 到 100 的进度百分比。</para>
    /// </summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>
    /// Gets or sets whether the progress bar runs without a known percentage.
    /// <para>获取或设置进度条是否在未知百分比下运行。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>
    /// Gets or sets the localized stage text.
    /// <para>获取或设置本地化阶段文本。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _progressStage;

    /// <summary>
    /// Gets or sets the formatted byte, speed and remaining-time statistics.
    /// <para>获取或设置格式化后的字节、速度与剩余时间统计。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _progressStats = string.Empty;

    /// <summary>
    /// Gets the combined progress or status description for the bottom bar.
    /// <para>获取底栏的组合进度或状态描述。</para>
    /// </summary>
    public string ProgressText
    {
        get
        {
            if (!string.IsNullOrEmpty(StatusMessage))
            {
                return StatusMessage;
            }

            if (string.IsNullOrEmpty(ProgressStats))
            {
                return ProgressStage;
            }

            return string.IsNullOrEmpty(ProgressStage)
                ? ProgressStats
                : $"{ProgressStage} · {ProgressStats}";
        }
    }

    /// <summary>
    /// Gets whether no operation is running.
    /// <para>获取当前没有操作在运行。</para>
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// Gets whether a drive scan can start.
    /// <para>获取是否可以开始扫描驱动器。</para>
    /// </summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>
    /// Gets whether flashing can start: an image is probed or a card package is recognized, one writable drive
    /// is selected and the overwrite confirmation is ticked.
    /// <para>获取是否可以开始烧录：镜像已探测或卡包已识别、已选中一个可写驱动器且覆盖确认已勾选。</para>
    /// </summary>
    public bool CanFlash => !IsBusy
        && HasImage
        && ConfirmOverwrite
        && SelectedItem is { IsBlocked: false };

    /// <summary>
    /// Gets whether a running operation can be cancelled.
    /// <para>获取是否可以取消正在运行的操作。</para>
    /// </summary>
    public bool CanCancel => IsBusy;

    /// <summary>
    /// Gets whether an image has been probed or a card package has been recognized.
    /// <para>获取是否已成功探测镜像或已识别卡包。</para>
    /// </summary>
    public bool HasImage => _image is not null || _cardPackage is not null;

    /// <summary>
    /// Gets whether any scanned drive is available.
    /// <para>获取是否已扫描到可用的驱动器。</para>
    /// </summary>
    public bool HasDrives => Drives.Count > 0;

    /// <summary>
    /// Gets the selected drive row, or <see langword="null"/> when nothing is selected.
    /// <para>获取选中的驱动器行；未选中时为 <see langword="null"/>。</para>
    /// </summary>
    public DiskFlashDriveItem? SelectedItem => Drives.FirstOrDefault(item => item.IsSelected && !item.IsBlocked);

    /// <summary>
    /// Gets the number of selected drives; the window allows at most one.
    /// <para>获取选中的驱动器数量；窗口最多允许一个。</para>
    /// </summary>
    public int SelectedCount => SelectedItem is null ? 0 : 1;

    /// <summary>
    /// Gets the localized selected-target count text.
    /// <para>获取本地化的已选目标数量文本。</para>
    /// </summary>
    public string SelectedCountText => string.Format(Language.GetString("DiskFlash_SelectedCount"), SelectedCount);

    /// <summary>
    /// Gets whether this process already holds the raw-disk write privilege.
    /// <para>获取本进程是否已持有裸磁盘写权限。</para>
    /// </summary>
    public bool IsElevated
    {
        get => _isElevated;
        private set => SetProperty(ref _isElevated, value);
    }

    /// <summary>
    /// Gets or sets the localized explanation of what the last scan could not read, such as a drive whose
    /// capacity needs administrator rights.
    /// <para>获取或设置上一次扫描未能读取的内容的本地化说明，例如需要管理员权限才能读取的驱动器容量。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanDetail))]
    private string _scanDetail = string.Empty;

    /// <summary>
    /// Gets whether <see cref="ScanDetail"/> has something to show.
    /// <para>获取 <see cref="ScanDetail"/> 是否有内容可显示。</para>
    /// </summary>
    public bool HasScanDetail => !string.IsNullOrEmpty(ScanDetail);

    /// <summary>
    /// Probes the given image path and refreshes drive compatibility.
    /// <para>探测给定镜像路径并刷新驱动器兼容性。</para>
    /// </summary>
    /// <param name="path">The image path, or <see langword="null"/> to clear the selection.
    /// <para>镜像路径；<see langword="null"/> 时清空选择。</para></param>
    public void SetImage(string? path)
    {
        if (IsBusy)
        {
            return;
        }

        _cardPackage = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            _image = null;
            ImagePath = string.Empty;
            ImageSummary = string.Empty;
            StatusMessage = string.Empty;
            StatusIsError = false;
            RefreshItems();
            return;
        }

        try
        {
            var image = ImageProbe.Probe(path);
            _image = image;
            ImagePath = image.Path;
            ImageSummary = DescribeImage(image);
            StatusMessage = string.Empty;
            StatusIsError = false;
        }
        catch (Exception ex) when (ex is FlashException or IOException or UnauthorizedAccessException)
        {
            if (!TryAdoptCardPackage(path))
            {
                _image = null;
                ImagePath = path;
                ImageSummary = string.Empty;
                StatusMessage = string.Format(Language.GetString("DiskFlash_ImageInvalid"), Path.GetFileName(path));
                StatusIsError = true;
                LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Probe failed: {ex.Message}");
            }
        }
        finally
        {
            RefreshItems();
        }
    }

    private bool TryAdoptCardPackage(string path)
    {
        try
        {
            var card = SpacemiTCardPackage.TryOpen(path);
            if (card is null)
            {
                return false;
            }

            _image = null;
            _cardPackage = card;
            ImagePath = card.PackagePath;
            ImageSummary = string.Format(
                Language.GetString("DiskFlash_ImageCard"),
                card.PartitionCount,
                ProgressMeter.FormatSize(card.EstimatedImageSizeBytes));
            StatusMessage = string.Empty;
            StatusIsError = false;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or ArgumentException or FormatException)
        {
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Card package recognition failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Selects one drive row and clears every other row, keeping the selection exclusive.
    /// <para>选中一个驱动器行并清除其它行，保持选择互斥。</para>
    /// </summary>
    /// <param name="item">The row to select; <see langword="null"/> or a blocked row clears the selection.
    /// <para>要选中的行；<see langword="null"/> 或受限行会清空选择。</para></param>
    public void SelectDrive(DiskFlashDriveItem? item)
    {
        if (IsBusy)
        {
            return;
        }

        var target = item is { IsBlocked: false } ? item : null;
        foreach (var row in Drives)
        {
            row.IsSelected = ReferenceEquals(row, target);
        }

        NotifyDerived();
    }

    /// <summary>
    /// Scans the machine and repopulates the drive list, preserving the previous selection.
    /// <para>扫描本机并重新填充驱动器列表，保留此前的选择。</para>
    /// </summary>
    [RelayCommand]
    public async Task RefreshDrivesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var previousSelection = SelectedItem?.DevicePath;

        var cts = new CancellationTokenSource();
        _operationCts = cts;
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = string.Empty;
        ProgressStage = Language.GetString("DiskFlash_StageScanning");
        IsProgressIndeterminate = true;
        ProgressStats = string.Empty;
        try
        {
            foreach (var item in Drives)
            {
                item.PropertyChanged -= OnDrivePropertyChanged;
            }

            var drives = await _flashService.ScanDrivesAsync(cts.Token).ConfigureAwait(true);
            ReplaceDrives(drives, previousSelection);
            RefreshItems();
            ScanDetail = DescribeOrdinaryScan();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Language.GetString("DiskFlash_Cancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = Language.GetString("DiskFlash_ScanFailed");
            ScanDetail = string.Format(Language.GetString("DiskFlash_ScanFailedDetail"), ex.Message);
            StatusIsError = true;
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
            }

            cts.Dispose();
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressStage = Language.GetString("DiskFlash_Ready");
            NotifyDerived();
        }
    }

    /// <summary>
    /// Writes the probed image to the selected drive, verifying the result when requested.
    /// <para>把已探测的镜像写入选中驱动器，并按需校验结果。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFlash))]
    public async Task FlashAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_image is null && _cardPackage is null)
        {
            StatusMessage = string.Format(Language.GetString("DiskFlash_ImageInvalid"), Path.GetFileName(ImagePath));
            StatusIsError = true;
            return;
        }

        if (SelectedItem is not { } target)
        {
            StatusMessage = Language.GetString("DiskFlash_NoTarget");
            StatusIsError = true;
            return;
        }

        if (!ConfirmOverwrite)
        {
            StatusMessage = Language.GetString("DiskFlash_ConfirmRequired");
            StatusIsError = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _operationCts = cts;
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = string.Empty;
        _meter = new ProgressMeter();
        _meterStage = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        ProgressStats = string.Empty;
        ProgressStage = Language.GetString("DiskFlash_StagePreparing");
        var generation = Interlocked.Increment(ref _progressGeneration);
        var progress = new Progress<DiskFlashProgress>(value => ApplyProgress(generation, value));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? temporaryImage = null;
        var imageSize = _image?.Size ?? _image?.CompressedSize ?? 0L;
        try
        {
            string flashPath;
            if (_cardPackage is { } card)
            {
                temporaryImage = CreateTemporaryImagePath();
                ProgressStage = Language.GetString("DiskFlash_StageSynthesizing");
                _synthesizing = true;
                var synthesisProgress = new Progress<SpacemiTCardImageProgress>(
                    value => ApplySynthesisProgress(generation, value));
                SpacemiTCardImageResult synthesis;
                try
                {
                    synthesis = await Task.Run(
                        () => card.Synthesize(temporaryImage, synthesisProgress, cts.Token),
                        cts.Token).ConfigureAwait(true);
                }
                finally
                {
                    _synthesizing = false;
                }

                imageSize = synthesis.ImageSizeBytes;
                flashPath = temporaryImage;
                ProgressStage = Language.GetString("DiskFlash_StagePreparing");
                ProgressPercent = 0;
                IsProgressIndeterminate = true;
                ProgressStats = string.Empty;
            }
            else
            {
                flashPath = _image!.Path;
            }

            var failure = await _flashService.FlashAsync(
                flashPath,
                [target.Drive],
                new FlashJobOptions
                {
                    VerifyAfterWrite = VerifyAfterWrite,
                    AllowLargeDrive = AllowLargeDrive,
                },
                progress,
                cts.Token).ConfigureAwait(true);

            IsElevated = _flashService.HasWriteAccess;
            if (failure is not null)
            {
                await ReportFailureAsync(failure).ConfigureAwait(true);
                return;
            }

            watch.Stop();
            ProgressStage = Language.GetString("DiskFlash_StageCompleted");
            ProgressPercent = 100;
            IsProgressIndeterminate = false;
            StatusMessage = string.Format(
                Language.GetString("DiskFlash_Success"),
                1,
                ProgressMeter.FormatSize(imageSize),
                $"{watch.Elapsed.TotalSeconds:0.0} s");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Language.GetString("DiskFlash_Cancelled");
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(Language.GetString("DiskFlash_Failed")).ConfigureAwait(true);
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Flash failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
            }

            cts.Dispose();
            Interlocked.Increment(ref _progressGeneration);
            _meter = null;
            _meterStage = null;
            _synthesizing = false;
            IsBusy = false;
            IsProgressIndeterminate = false;
            DeleteTemporaryImage(temporaryImage);
            NotifyDerived();
        }
    }

    /// <summary>
    /// Cancels the running scan or flash operation.
    /// <para>取消正在运行的扫描或烧录操作。</para>
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        _operationCts?.Cancel();
    }

    partial void OnAllowLargeDriveChanged(bool value) => RefreshItems();

    partial void OnConfirmOverwriteChanged(bool value) => FlashCommand.NotifyCanExecuteChanged();

    private async Task ReportFailureAsync(string message)
    {
        StatusMessage = message;
        StatusIsError = true;
        ProgressStage = Language.GetString("DiskFlash_Ready");
        await _dialogs.ShowErrorAsync(Language.GetString("DiskFlash_Title"), message).ConfigureAwait(true);
    }

    private void ApplySynthesisProgress(int generation, SpacemiTCardImageProgress value)
    {
        if (Volatile.Read(ref _progressGeneration) != generation || !_synthesizing)
        {
            return;
        }

        ProgressStage = Language.GetString("DiskFlash_StageSynthesizing");
        var meter = _meter;
        if (meter is null)
        {
            return;
        }

        var total = value.BytesTotal;
        var completed = value.BytesCompleted;
        if (total.HasValue && total.Value > 0 && completed.HasValue)
        {
            var totalBytes = total.Value;
            var done = completed.Value;
            ProgressPercent = Math.Clamp(done * 100.0 / totalBytes, 0, 100);
            IsProgressIndeterminate = false;
            meter.Report(done, totalBytes);
            var template = meter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats";
            ProgressStats = meter.FormatStats(Language.GetString(template));
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private static string CreateTemporaryImagePath() =>
        Path.Combine(Path.GetTempPath(), $"uotan-spacemit-card-{Guid.NewGuid():N}.img");

    private static void DeleteTemporaryImage(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ApplyProgress(int generation, DiskFlashProgress value)
    {
        if (Volatile.Read(ref _progressGeneration) != generation)
        {
            return;
        }

        var meter = _meter;
        if (meter is null)
        {
            return;
        }

        if (_meterStage != value.Stage)
        {
            _meterStage = value.Stage;
            meter.Reset();
        }

        ProgressStage = StageText(value.Stage);
        ProgressPercent = value.Percentage ?? 0;
        IsProgressIndeterminate = value.Percentage is null;
        meter.Report(value.BytesProcessed, value.TotalBytes ?? 0);
        var template = value.TotalBytes is > 0
            ? meter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats"
            : "Progress_ProcessedStats";
        ProgressStats = meter.FormatStats(Language.GetString(template));
    }

    private int ReplaceDrives(IReadOnlyList<FlashDrive> drives, string? previousSelection = null)
    {
        foreach (var item in Drives)
        {
            item.PropertyChanged -= OnDrivePropertyChanged;
        }

        Drives.Clear();
        var reselect = true;
        foreach (var drive in drives)
        {
            var item = new DiskFlashDriveItem(drive)
            {
                IsSelected = reselect && string.Equals(drive.DevicePath, previousSelection, StringComparison.OrdinalIgnoreCase),
            };
            reselect &= !item.IsSelected;
            item.PropertyChanged += OnDrivePropertyChanged;
            Drives.Add(item);
        }

        return Drives.Count;
    }

    private string DescribeOrdinaryScan()
    {
        var unknown = Drives.Count(item => item.Drive.Size <= 0);
        if (unknown == 0)
        {
            return string.Empty;
        }

        return string.Format(
            Language.GetString(IsElevated
                ? "DiskFlash_ScanSizeUnknownElevated"
                : "DiskFlash_ScanSizeUnknown"),
            unknown);
    }


    private void RefreshItems()
    {
        foreach (var item in Drives)
        {
            item.Refresh(_image, AllowLargeDrive);
        }

        NotifyDerived();
    }

    private void OnDrivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskFlashDriveItem.IsSelected) &&
            sender is DiskFlashDriveItem { IsSelected: true } selected)
        {
            foreach (var row in Drives)
            {
                if (!ReferenceEquals(row, selected))
                {
                    row.IsSelected = false;
                }
            }
        }

        if (e.PropertyName is nameof(DiskFlashDriveItem.IsSelected) or nameof(DiskFlashDriveItem.IsBlocked))
        {
            NotifyDerived();
        }
    }

    private void NotifyDerived()
    {
        FlashCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanFlash));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasDrives));
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(ProgressText));
    }

    private static string DescribeImage(FlashImage image)
    {
        var format = image.Format.Container switch
        {
            ImageContainerKind.Raw => Language.GetString("DiskFlash_ImageRaw"),
            ImageContainerKind.Zip => Language.GetString("DiskFlash_ImageZip"),
            _ => string.Format(Language.GetString("DiskFlash_ImageCompressed"), image.Format.Compression),
        };
        var size = image.Size is { } value
            ? ProgressMeter.FormatSize(value)
            : ProgressMeter.FormatSize(image.CompressedSize);
        return string.Format(Language.GetString("DiskFlash_ImageSummary"), format, size);
    }

    private static string StageText(FlashStage stage) => stage switch
    {
        FlashStage.Preparing => Language.GetString("DiskFlash_StagePreparing"),
        FlashStage.Writing => Language.GetString("DiskFlash_StageWriting"),
        FlashStage.Verifying => Language.GetString("DiskFlash_StageVerifying"),
        FlashStage.Completed => Language.GetString("DiskFlash_StageCompleted"),
        _ => Language.GetString("DiskFlash_Ready"),
    };
}