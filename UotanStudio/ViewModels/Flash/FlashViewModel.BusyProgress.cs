using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The busy overlay's transfer statistics: a sliding-window speed and remaining time derived from the bytes a run
/// reports, or from the overall fraction when no transfer length is known. The overlay falls back to its plain busy
/// caption whenever neither source yields a determinate fraction.
/// <para>忙碌遮罩的传输统计：按运行上报的字节，或在不掌握传输长度时按总体比例，计算滑动窗口速度与剩余时间；
/// 两种来源都没有确定比例时，遮罩退回纯忙碌文案。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    private readonly ProgressMeter _busyByteMeter = new();
    private readonly ProgressMeter _busyPercentMeter = new();

    /// <summary>Byte weight per row for the current busy scope, resolved once because file probes are expensive.
    /// <para>当前忙碌范围内每行的字节权重，只解析一次以避免反复探测文件。</para></summary>
    private readonly Dictionary<FlashPartitionEntry, long> _busyRowWeights = [];

    /// <summary>Whether a source outside the row grid feeds the byte meter, so row-derived samples must not override it.
    /// <para>行列表之外的来源是否正在向字节计量器供数；此时不得用行推导样本覆盖它。</para></summary>
    private bool _busyManualTransfer;

    /// <summary>The localized speed and remaining-time line shown under the busy caption; empty when not measurable.
    /// <para>忙碌文案下方显示的本地化速度与剩余时间行；不可度量时为空。</para></summary>
    [ObservableProperty]
    private string _busyStatsText = string.Empty;

    /// <summary>Whether <see cref="BusyStatsText"/> carries a determinate transfer statistic.
    /// <para><see cref="BusyStatsText"/> 是否带有确定的传输统计。</para></summary>
    [ObservableProperty]
    private bool _hasBusyStats;

    /// <summary>The localized line shown while a cancelled transfer is being taken back from the target, naming how
    /// much of the declared payload has been reclaimed; empty outside that window.
    /// <para>被取消的传输正在从目标收回时显示的本地化文案，标出已回收多少声明负载；不在该窗口时为空。</para></summary>
    [ObservableProperty]
    private string _busyStopText = string.Empty;

    /// <summary>Whether <see cref="BusyStopText"/> is showing a stopping transfer.
    /// <para><see cref="BusyStopText"/> 是否正在显示停止中的传输。</para></summary>
    [ObservableProperty]
    private bool _hasBusyStop;

    /// <summary>
    /// Clears every sample and weight so a new busy scope starts from zero; called when the page enters or leaves a
    /// busy operation.
    /// <para>清空全部样本与权重，使新的忙碌范围从零开始；页面进入或离开忙碌操作时调用。</para>
    /// </summary>
    internal void ResetBusyProgress()
    {
        _busyByteMeter.Reset();
        _busyPercentMeter.Reset();
        _busyRowWeights.Clear();
        _busyManualTransfer = false;
        ClearBusyStats();
        ClearSafeStopping();
    }

    /// <summary>
    /// Shows how much of an interrupted transfer has been taken back from the target while the protocol layer returns
    /// it to the command boundary. Cancelling an EDL raw read cannot stop it at an arbitrary byte, so this is the only
    /// signal that the interrupt is progressing rather than hung.
    /// <para>显示被中断的传输已被从目标收回多少，此时协议层正在把它送回命令边界。取消 EDL 原始读取无法停在任意字节，
    /// 因此这是“中断正在推进而不是卡死”的唯一信号。</para>
    /// </summary>
    /// <param name="drainedBytes">Bytes already reclaimed from the target. <para>已从目标回收的字节数。</para></param>
    /// <param name="totalBytes">Whole payload the target declared. <para>目标声明的完整负载。</para></param>
    internal void ReportSafeStopping(long drainedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            ClearSafeStopping();
            return;
        }

        BusyStopText = string.Format(CultureInfo.CurrentCulture, _localizer.GetString("Flash_BusyStopping"),
            ProgressMeter.FormatSize(Math.Clamp(drainedBytes, 0, totalBytes)), ProgressMeter.FormatSize(totalBytes));
        HasBusyStop = true;
    }

    /// <summary>
    /// Removes the stopping line once the interrupt finished, either because the target returned to command mode or
    /// because the link was reset and the session has to be reconnected.
    /// <para>中断结束后移除停止文案，无论是目标已回到命令模式，还是链路被复位而会话需要重新连接。</para>
    /// </summary>
    internal void ClearSafeStopping()
    {
        BusyStopText = string.Empty;
        HasBusyStop = false;
    }

    /// <summary>
    /// Reports one byte sample from a source without partition rows, such as the Sunxi flasher; the byte meter then
    /// owns the overlay statistics for the rest of the busy scope.
    /// <para>上报一条来自无分区行来源的字节样本（例如全志烧录器）；此后本次忙碌范围的遮罩统计由字节计量器负责。</para>
    /// </summary>
    /// <param name="processed">Bytes completed so far. <para>已完成字节数。</para></param>
    /// <param name="total">Bytes the transfer covers. <para>本次传输覆盖的总字节数。</para></param>
    internal void ReportBusyTransfer(long processed, long total)
    {
        if (!IsBusy || total <= 0)
        {
            return;
        }

        _busyManualTransfer = true;
        _busyByteMeter.Report(Math.Clamp(processed, 0L, total), total);
        PublishByteStats();
    }

    /// <summary>
    /// Recomputes the overlay statistics from the target rows and the overall fraction; called after every row progress
    /// update so the caption follows the same samples the progress bars show.
    /// <para>按目标行与总体比例重算遮罩统计；每次行进度更新后调用，使文案跟随进度条所用的同一批样本。</para>
    /// </summary>
    internal void UpdateBusyProgress()
    {
        if (!IsBusy)
        {
            ClearBusyStats();
            return;
        }

        if (_busyManualTransfer)
        {
            PublishByteStats();
            return;
        }

        if (TryComputeRowTransfer(out var done, out var total))
        {
            _busyByteMeter.Report(done, total);
            PublishByteStats();
            return;
        }

        PublishPercentStats();
    }

    private bool TryComputeRowTransfer(out long done, out long total)
    {
        done = 0;
        total = 0;
        var running = false;
        foreach (var row in _overallRows)
        {
            var weight = ResolveTransferWeight(row);
            if (weight <= 0)
            {
                continue;
            }

            total += weight;
            switch (row.State)
            {
                case FlashRowState.Succeeded:
                    done += weight;
                    break;
                case FlashRowState.Failed:
                case FlashRowState.Skipped:
                    done += (long)(weight * (Math.Clamp(row.Percent, 0, 100) / 100d));
                    break;
                case FlashRowState.Running:
                    if (row.IsIndeterminate || row.Percent < 0)
                    {
                        return false;
                    }

                    running = true;
                    done += (long)(weight * (Math.Clamp(row.Percent, 0, 100) / 100d));
                    break;
            }
        }

        done = Math.Clamp(done, 0L, total);
        return running && total > 0;
    }

    private long ResolveTransferWeight(FlashPartitionEntry row)
    {
        if (row.TransferBytes > 0)
        {
            return row.TransferBytes;
        }

        if (_busyRowWeights.TryGetValue(row, out var cached))
        {
            return cached;
        }

        var weight = 0L;
        if (row.FilePath.Length > 0)
        {
            try
            {
                weight = File.Exists(row.FilePath) ? new FileInfo(row.FilePath).Length : 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                weight = 0;
            }
        }

        if (weight <= 0)
        {
            weight = row.SizeBytes;
        }

        _busyRowWeights[row] = weight;
        return weight;
    }

    private void PublishByteStats()
    {
        if (_busyByteMeter.Total <= 0)
        {
            ClearBusyStats();
            return;
        }

        var template = _busyByteMeter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats";
        BusyStatsText = _busyByteMeter.FormatStats(_localizer.GetString(template));
        HasBusyStats = true;
    }

    private void PublishPercentStats()
    {
        if (IsOverallIndeterminate || OverallPercent <= 0)
        {
            ClearBusyStats();
            return;
        }

        var percent = string.Create(CultureInfo.CurrentCulture, $"{OverallPercent:0}%");
        _busyPercentMeter.ReportItems((long)Math.Round(OverallPercent * 100d), 10000);
        BusyStatsText = _busyPercentMeter.EstimatedRemaining is { } eta
            ? string.Format(CultureInfo.CurrentCulture, _localizer.GetString("Flash_BusyProgressEta"), percent,
                ProgressMeter.FormatEta(eta))
            : string.Format(CultureInfo.CurrentCulture, _localizer.GetString("Flash_BusyProgressPercent"), percent);
        HasBusyStats = true;
    }

    private void ClearBusyStats()
    {
        BusyStatsText = string.Empty;
        HasBusyStats = false;
    }
}