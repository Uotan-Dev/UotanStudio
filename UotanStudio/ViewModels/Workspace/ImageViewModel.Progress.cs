using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private int _progressGeneration;

    /// <summary>操作完成报告的显示时长，到期后进度区自动隐藏。</summary>
    /// <summary>How long the completed-operation report stays visible before the progress area auto-hides.</summary>
    private static readonly TimeSpan ReportHideDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _hideReportCts;
    private FileSystemWorkStage? _fileSystemStage;

    /// <summary>Receives phase byte counts on the UI context and rejects callbacks from completed images.</summary>
    private IProgress<FileSystemByteProgress> CreateFileSystemProgress(string imageName)
    {
        var generation = _progressGeneration;
        _fileSystemStage = null;
        _meter.Reset();
        ProgressStats = string.Empty;
        return new Progress<FileSystemByteProgress>(sample =>
        {
            if (generation != _progressGeneration || !IsOperating) return;
            ReportFileSystemProgress(imageName, sample);
        });
    }

    internal void ReportFileSystemProgress(string imageName, FileSystemByteProgress sample)
    {
        if (_fileSystemStage != sample.Stage)
        {
            _meter.Reset();
            _fileSystemStage = sample.Stage;
        }
        var stage = LanguageService.Instance.GetString("Image_Fs" + sample.Stage);
        StatusText = string.Format(LanguageService.Instance.GetString("Image_FsStageFile"), stage, imageName);
        if (sample.Stage == FileSystemWorkStage.ReadingMetadata)
        {
            ProgressPercent = -1;
            ProgressStats = string.Empty;
            return;
        }
        ReportByteStatistics(sample.Processed, sample.Total);
    }

    /// <summary>Whether the current operation has no measurable percentage.</summary>
    public bool IsProgressIndeterminate => !double.IsFinite(ProgressPercent) || ProgressPercent < 0;

    /// <summary>Localized stage text with the measured percentage.</summary>
    public string ProgressStatus => string.IsNullOrEmpty(StatusText) || IsProgressIndeterminate
        ? StatusText
        : string.Format(LanguageService.Instance.GetString("Progress_StatusPercent"), StatusText,
            Math.Clamp(ProgressPercent, 0, 100));

    partial void OnProgressPercentChanged(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            _meter.Reset();
            ProgressStats = string.Empty;
        }
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressStatus));
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(ProgressStatus));

    partial void OnIsOperatingChanged(bool value)
    {
        _progressGeneration++;
        if (value)
        {
            // 新操作开始：取消尚未执行的报告自动隐藏。
            // A new operation starts: cancel any pending report auto-hide.
            _hideReportCts?.Cancel();
            _hideReportCts?.Dispose();
            _hideReportCts = null;
            _meter.Reset();
            ProgressStats = string.Empty;
            ProgressPercent = -1;
            return;
        }

        // 操作结束：到期后清空状态与统计文本，进度区随之自动隐藏。
        // The operation finished: clear status and stats after the delay so the progress area auto-hides.
        _hideReportCts?.Cancel();
        _hideReportCts?.Dispose();
        var cts = new CancellationTokenSource();
        _hideReportCts = cts;
        _ = HideReportAfterDelayAsync(cts.Token);
    }

    private async Task HideReportAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ReportHideDelay, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsOperating || token.IsCancellationRequested) return;
        StatusText = string.Empty;
        ProgressStats = string.Empty;
        ProgressPercent = 0;
    }

    /// <summary>Updates measured batch item counts without treating them as bytes.</summary>
    internal void ReportItemStatistics(int completed, int total)
    {
        _meter.ReportItems(completed, total);
        ProgressStats = _meter.FormatStats(LanguageService.Instance.GetString(
            _meter.EstimatedRemaining is null ? "Progress_ItemsStatsNoEta" : "Progress_ItemsStats"));
    }

    /// <summary>Updates cumulative copy bytes for the current operation.</summary>
    internal void ReportByteStatistics(long processed, long total)
    {
        _meter.Report(processed, total);
        ProgressPercent = total > 0 ? Math.Clamp(processed * 100.0 / total, 0, 100) : -1;
        ProgressStats = _meter.FormatStats(LanguageService.Instance.GetString(
            _meter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats"));
    }
}