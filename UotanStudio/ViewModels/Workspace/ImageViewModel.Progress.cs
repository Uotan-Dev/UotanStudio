using UotanStudio.Firmware.Core;
using UotanStudio.Services;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private int _progressGeneration;

    /// <summary>How long the completed-operation report stays before the progress area resets to zero.<para>操作完成后进度区归零前的保持时长。</para></summary>
    private static readonly TimeSpan ReportHideDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _hideReportCts;
    private FileSystemWorkStage? _fileSystemStage;

    /// <summary>Current batch item index shown as the "[x/x]" group; 0 hides the group.<para>当前批次条目序号，作为「[x/x]」分组显示；为 0 时不显示该分组。</para></summary>
    private int _busyIndex;

    /// <summary>Current batch item total shown as the "[x/x]" group; 0 hides the group.<para>当前批次条目总数，作为「[x/x]」分组显示；为 0 时不显示该分组。</para></summary>
    private int _busyTotal;

    /// <summary>
    /// Receives phase byte counts on the captured context and rejects callbacks from superseded operations.<para>在创建时捕获的上下文上接收阶段字节计数，并拒绝已被取代操作的回调。</para>
    /// </summary>
    /// <param name="imageName">File name shown in stage status text.<para>在阶段状态文本中显示的文件名。</para></param>
    /// <returns>A progress sink for byte-progress samples.<para>用于字节进度样本的进度接收器。</para></returns>
    private IProgress<FileSystemByteProgress> CreateFileSystemProgress(string imageName)
    {
        var generation = _progressGeneration;
        _fileSystemStage = null;
        _meter.Reset();
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
            return;
        }
        ReportByteStatistics(sample.Processed, sample.Total);
    }

    /// <summary>Whether the current operation has no measurable percentage.<para>当前操作是否没有可度量的百分比。</para></summary>
    /// <value>True when the percentage is absent or negative; otherwise false.<para>百分比缺失或为负时 true，否则 false。</para></value>
    public bool IsProgressIndeterminate => !double.IsFinite(ProgressPercent) || ProgressPercent < 0;

    /// <summary>
    /// Payload rendered under the busy spinner: batch position, stage text, measured speed and the
    /// estimated time left, each as its own unbreakable group.
    /// <para>忙碌转圈下方呈现的负载：批次序号、阶段文本、测得速度与预计剩余时间，各自作为独立的不可拆分分组。</para>
    /// </summary>
    /// <value>The current groups; updated in place whenever the busy state changes.
    /// <para>当前分组；忙碌状态变化时原地更新。</para></value>
    public BusyMessageModel Busy { get; } = new();

    /// <summary>Recomputes <see cref="Busy"/> from the current operation state.<para>按当前操作状态重新计算 <see cref="Busy"/>。</para></summary>
    private void RefreshBusyGroups()
    {
        if (!IsOperating)
        {
            Busy.Clear();
            return;
        }

        Busy.Update(StatusText, _meter, _busyIndex, _busyTotal,
            LanguageService.Instance.GetString("ImageView_Processing"));
    }

    /// <summary>Sets the "[x/x]" batch position shown in the busy line.<para>设置忙碌文本中显示的「[x/x]」批次位置。</para></summary>
    /// <param name="index">One-based index of the item being processed. <para>正在处理的条目序号（从 1 开始）。</para></param>
    /// <param name="total">Total items in the batch. <para>批次条目总数。</para></param>
    internal void ReportBusyPosition(int index, int total)
    {
        _busyIndex = Math.Max(0, index);
        _busyTotal = Math.Max(0, total);
        RefreshBusyGroups();
    }

    /// <summary>
    /// Splits a leading "[i/n]" batch marker off a flash status line, so the marker is rendered once
    /// in the busy line's own group instead of being repeated inside the status text.
    /// <para>把刷写状态行开头的「[i/n]」批次标记拆出来，让标记只在忙碌文本自己的分组里出现一次，
    /// 而不是在状态文本里重复。</para>
    /// </summary>
    /// <param name="status">The status text reported by the flash service. <para>刷写服务上报的状态文本。</para></param>
    /// <param name="index">Receives the parsed batch index; 0 when absent. <para>解析出的批次序号；缺失时为 0。</para></param>
    /// <param name="total">Receives the parsed batch total; 0 when absent. <para>解析出的批次总数；缺失时为 0。</para></param>
    /// <returns>The status text without the batch marker. <para>去掉批次标记后的状态文本。</para></returns>
    private static string SplitBatchPrefix(string status, out int index, out int total)
    {
        index = 0;
        total = 0;
        if (string.IsNullOrEmpty(status) || status[0] != '[') return status;

        var close = status.IndexOf(']');
        if (close < 0) return status;
        var marker = status.AsSpan(1, close - 1);
        var slash = marker.IndexOf('/');
        if (slash <= 0) return status;
        if (!int.TryParse(marker[..slash], out var parsedIndex)
            || !int.TryParse(marker[(slash + 1)..], out var parsedTotal)
            || parsedTotal <= 0)
            return status;

        index = parsedIndex;
        total = parsedTotal;
        return status[(close + 1)..].TrimStart();
    }

    /// <summary>Clears the "[x/x]" group once a batch finishes.<para>批次结束后清除「[x/x]」分组。</para></summary>
    internal void ClearBusyPosition()
    {
        if (_busyIndex == 0 && _busyTotal == 0) return;
        _busyIndex = 0;
        _busyTotal = 0;
        RefreshBusyGroups();
    }

    partial void OnProgressPercentChanged(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            _meter.Reset();
        }
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        RefreshBusyGroups();
    }

    partial void OnStatusTextChanged(string value) => RefreshBusyGroups();

    partial void OnIsOperatingChanged(bool value)
    {
        _progressGeneration++;
        if (value)
        {

            _hideReportCts?.Cancel();
            _hideReportCts?.Dispose();
            _hideReportCts = null;
            _meter.Reset();
            ProgressPercent = -1;
            RefreshBusyGroups();
            return;
        }

        var hadPosition = _busyIndex != 0 || _busyTotal != 0;
        ClearBusyPosition();
        if (!hadPosition) RefreshBusyGroups();
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
        ProgressPercent = 0;
    }

    /// <summary>Updates measured batch item counts without treating them as bytes.<para>更新已度量的批量条目计数，不将其视为字节。</para></summary>
    /// <param name="completed">Items finished so far.<para>已完成的条目数。</para></param>
    /// <param name="total">Total items in the batch.<para>批次中的条目总数。</para></param>
    internal void ReportItemStatistics(int completed, int total)
    {
        _meter.ReportItems(completed, total);
        RefreshBusyGroups();
    }

    /// <summary>Updates cumulative copy bytes for the current operation.<para>更新当前操作的累计复制字节数。</para></summary>
    /// <param name="processed">Bytes processed so far.<para>已处理的字节数。</para></param>
    /// <param name="total">Total bytes for the operation.<para>本次操作的总字节数。</para></param>
    internal void ReportByteStatistics(long processed, long total)
    {
        _meter.Report(processed, total);
        var next = total > 0 ? Math.Clamp(processed * 100.0 / total, 0, 100) : -1;
        var changed = !ProgressPercent.Equals(next);
        ProgressPercent = next;
        // OnProgressPercentChanged 仅在百分比变化时触发刷新，未变化时由这里补刷速度与 ETA。
        if (!changed) RefreshBusyGroups();
    }

    /// <summary>
    /// Shows a modal prompt dialog when an owner window is available; no-op in headless/test runs.
    /// <para>存在所有者窗口时弹出提示框；无窗口或测试环境下静默跳过。</para>
    /// </summary>
    private static Task ShowPromptAsync(string message,
        Ursa.Controls.MessageBoxIcon icon = Ursa.Controls.MessageBoxIcon.Information,
        string titleKey = "Dock_ImagesView")
        => DialogService.GetOwnerWindow() is { } owner
            ? DialogService.ShowAsync(owner, LanguageService.Instance.GetString(titleKey), message, icon)
            : Task.CompletedTask;
}