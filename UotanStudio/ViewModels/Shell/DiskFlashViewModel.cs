using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Disk-image flashing window view model: image probing, target drive selection, safety switches and
/// write/verify progress.
/// <para>磁盘镜像烧录窗口视图模型：镜像探测、目标驱动器选择、安全开关与写入/校验进度。</para>
/// </summary>
public sealed partial class DiskFlashViewModel : ObservableObject
{
    private readonly IDriveScanner _scanner;
    private readonly ImageFlasher _flasher;
    private CancellationTokenSource? _operationCts;
    private FlashImage? _image;
    private ProgressMeter? _meter;
    private FlashStage? _meterStage;

    private static LanguageService Language => LanguageService.Instance;

    /// <summary>
    /// Creates the view model with the platform scanner and flasher unless alternatives are supplied.
    /// <para>创建视图模型；未提供替代实现时使用平台扫描器与烧录器。</para>
    /// </summary>
    /// <param name="scanner">Drive scanner; <see langword="null"/> selects the platform scanner.
    /// <para>驱动器扫描器；<see langword="null"/> 时选择平台扫描器。</para></param>
    /// <param name="flasher">Image flasher; <see langword="null"/> creates the default flasher.
    /// <para>镜像烧录器；<see langword="null"/> 时创建默认烧录器。</para></param>
    public DiskFlashViewModel(IDriveScanner? scanner = null, ImageFlasher? flasher = null)
    {
        _scanner = scanner ?? DriveScanner.CreateDefault();
        _flasher = flasher ?? new ImageFlasher();
        ProgressStage = Language.GetString("DiskFlash_Ready");
    }

    /// <summary>
    /// Gets the selectable target drives.
    /// <para>获取可选的目标驱动器。</para>
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
    /// Gets or sets whether the user accepted that every selected drive will be overwritten.
    /// <para>获取或设置用户是否已接受所有选中驱动器都会被覆盖。</para>
    /// </summary>
    [ObservableProperty]
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
    private string _progressStage;

    /// <summary>
    /// Gets or sets the formatted byte, speed and remaining-time statistics.
    /// <para>获取或设置格式化后的字节、速度与剩余时间统计。</para>
    /// </summary>
    [ObservableProperty]
    private string _progressStats = string.Empty;

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
    /// Gets whether flashing can start.
    /// <para>获取是否可以开始烧录。</para>
    /// </summary>
    public bool CanFlash => !IsBusy && _image is not null
        && Drives.Any(item => item.IsSelected && !item.IsBlocked);

    /// <summary>
    /// Gets whether a running operation can be cancelled.
    /// <para>获取是否可以取消正在运行的操作。</para>
    /// </summary>
    public bool CanCancel => IsBusy;

    /// <summary>
    /// Gets whether an image has been probed successfully.
    /// <para>获取是否已成功探测镜像。</para>
    /// </summary>
    public bool HasImage => _image is not null;

    /// <summary>
    /// Gets whether any scanned drive is available.
    /// <para>获取是否已扫描到可用的驱动器。</para>
    /// </summary>
    public bool HasDrives => Drives.Count > 0;

    /// <summary>
    /// Gets the number of selected, unblocked targets.
    /// <para>获取已选中且未被阻止的目标数量。</para>
    /// </summary>
    public int SelectedCount => Drives.Count(item => item.IsSelected && !item.IsBlocked);

    /// <summary>
    /// Gets the localized selected-target count text.
    /// <para>获取本地化的已选目标数量文本。</para>
    /// </summary>
    public string SelectedCountText => string.Format(Language.GetString("DiskFlash_SelectedCount"), SelectedCount);

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

        if (string.IsNullOrWhiteSpace(path))
        {
            _image = null;
            ImagePath = string.Empty;
            ImageSummary = string.Empty;
            StatusMessage = string.Empty;
            StatusIsError = false;
            RefreshItems();
            NotifyDerived();
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
            _image = null;
            ImagePath = path;
            ImageSummary = string.Empty;
            StatusMessage = string.Format(Language.GetString("DiskFlash_ImageInvalid"), Path.GetFileName(path));
            StatusIsError = true;
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Probe failed: {ex.Message}");
        }
        finally
        {
            RefreshItems();
            NotifyDerived();
        }
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

        var previousSelection = Drives
            .Where(item => item.IsSelected)
            .Select(item => item.DevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            Drives.Clear();
            var drives = await _scanner.ScanAsync(cts.Token).ConfigureAwait(true);
            foreach (var drive in drives)
            {
                var item = new DiskFlashDriveItem(drive)
                {
                    IsSelected = previousSelection.Contains(drive.DevicePath),
                };
                item.PropertyChanged += OnDrivePropertyChanged;
                Drives.Add(item);
            }

            RefreshItems();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Language.GetString("DiskFlash_Cancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = Language.GetString("DiskFlash_ScanFailed");
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
    /// Writes the probed image to every selected target and optionally verifies the result.
    /// <para>把已探测的镜像写入全部选中目标，并可选地校验结果。</para>
    /// </summary>
    [RelayCommand]
    public async Task FlashAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_image is null)
        {
            StatusMessage = string.Format(Language.GetString("DiskFlash_ImageInvalid"), Path.GetFileName(ImagePath));
            StatusIsError = true;
            return;
        }

        var targets = Drives
            .Where(item => item.IsSelected && !item.IsBlocked)
            .Select(item => item.Drive)
            .ToArray();
        if (targets.Length == 0)
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
        var progress = new Progress<FlashProgress>(ApplyProgress);
        try
        {
            var result = await _flasher.FlashAsync(
                _image,
                targets,
                new FlashOptions
                {
                    ConfirmOverwrite = true,
                    VerifyAfterWrite = VerifyAfterWrite,
                    AllowLargeDrive = AllowLargeDrive,
                },
                progress,
                cts.Token).ConfigureAwait(true);

            ProgressStage = Language.GetString("DiskFlash_StageCompleted");
            ProgressPercent = 100;
            IsProgressIndeterminate = false;
            StatusMessage = string.Format(
                Language.GetString("DiskFlash_Success"),
                result.Targets.Count,
                ProgressMeter.FormatSize(result.BytesWritten),
                $"{result.Elapsed.TotalSeconds:0.0} s");
        }
        catch (FlashValidationException ex)
        {
            StatusMessage = string.Format(Language.GetString("DiskFlash_ValidationFailed"), FormatFailures(ex.Failures));
            StatusIsError = true;
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Validation failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Language.GetString("DiskFlash_Cancelled");
            ProgressStage = Language.GetString("DiskFlash_Ready");
        }
        catch (Exception ex)
        {
            StatusMessage = Language.GetString("DiskFlash_Failed");
            StatusIsError = true;
            ProgressStage = Language.GetString("DiskFlash_Ready");
            LogService.Instance.Write(LogLevel.Error, "DiskFlash", $"Flash failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
            }

            cts.Dispose();
            _meter = null;
            _meterStage = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
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

    private void ApplyProgress(FlashProgress value)
    {
        if (_meterStage != value.Stage)
        {
            _meterStage = value.Stage;
            _meter?.Reset();
        }

        ProgressStage = StageText(value.Stage);
        ProgressPercent = value.Percentage ?? 0;
        IsProgressIndeterminate = value.Percentage is null;
        if (_meter is null)
        {
            return;
        }

        _meter.Report(value.BytesProcessed, value.TotalBytes ?? 0);
        var template = value.TotalBytes is > 0
            ? _meter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats"
            : "Progress_ProcessedStats";
        ProgressStats = _meter.FormatStats(Language.GetString(template));
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
        if (e.PropertyName == nameof(DiskFlashDriveItem.IsSelected))
        {
            NotifyDerived();
        }
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(CanFlash));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasDrives));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
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

    private static string FormatFailures(IReadOnlyList<DriveValidationFailure> failures) =>
        string.Join("; ", failures.Select(failure =>
            $"{failure.Drive.DisplayName} ({failure.Drive.DevicePath}): "
            + string.Join(", ", failure.Issues.Select(issue => DiskFlashDriveItem.IssueLabel(issue.Issue)))));
}