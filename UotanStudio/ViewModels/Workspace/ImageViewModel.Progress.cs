using UotanStudio.Firmware.Core;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private int _progressGeneration;

    /// <summary>How long the completed-operation report stays visible before the progress area auto-hides.<para>操作完成报告在进度区自动隐藏前保持显示的时长。</para></summary>
    private static readonly TimeSpan ReportHideDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _hideReportCts;
    private FileSystemWorkStage? _fileSystemStage;

    /// <summary>Receives phase byte counts on the captured context and rejects callbacks from superseded operations.<para>在创建时捕获的上下文上接收阶段字节计数，并拒绝已被取代操作的回调。</para></summary>
    /// <param name="imageName">File name shown in stage status text.<para>在阶段状态文本中显示的文件名。</para></param>
    /// <returns>A progress sink for byte-progress samples.<para>用于字节进度样本的进度接收器。</para></returns>
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

    /// <summary>Whether the current operation has no measurable percentage.<para>当前操作是否没有可度量的百分比。</para></summary>
    /// <value>True when the percentage is absent or negative; otherwise false.<para>百分比缺失或为负时 true，否则 false。</para></value>
    public bool IsProgressIndeterminate => !double.IsFinite(ProgressPercent) || ProgressPercent < 0;

    /// <summary>Localized stage text with the measured percentage.<para>带已度量百分比的本地化阶段文本。</para></summary>
    /// <value>The status text, appended with the percentage when measurable.<para>状态文本；可度量时附加百分比。</para></value>
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

            _hideReportCts?.Cancel();
            _hideReportCts?.Dispose();
            _hideReportCts = null;
            _meter.Reset();
            ProgressStats = string.Empty;
            ProgressPercent = -1;
            return;
        }

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

    /// <summary>Updates measured batch item counts without treating them as bytes.<para>更新已度量的批量条目计数，不将其视为字节。</para></summary>
    /// <param name="completed">Items finished so far.<para>已完成的条目数。</para></param>
    /// <param name="total">Total items in the batch.<para>批次中的条目总数。</para></param>
    internal void ReportItemStatistics(int completed, int total)
    {
        _meter.ReportItems(completed, total);
        ProgressStats = _meter.FormatStats(LanguageService.Instance.GetString(
            _meter.EstimatedRemaining is null ? "Progress_ItemsStatsNoEta" : "Progress_ItemsStats"));
    }

    /// <summary>Updates cumulative copy bytes for the current operation.<para>更新当前操作的累计复制字节数。</para></summary>
    /// <param name="processed">Bytes processed so far.<para>已处理的字节数。</para></param>
    /// <param name="total">Total bytes for the operation.<para>本次操作的总字节数。</para></param>
    internal void ReportByteStatistics(long processed, long total)
    {
        _meter.Report(processed, total);
        ProgressPercent = total > 0 ? Math.Clamp(processed * 100.0 / total, 0, 100) : -1;
        ProgressStats = _meter.FormatStats(LanguageService.Instance.GetString(
            _meter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats"));
    }
}