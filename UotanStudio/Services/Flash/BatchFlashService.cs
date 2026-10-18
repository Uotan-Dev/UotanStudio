using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Flashes multiple partition images to a device sequentially; a single-partition failure only
/// logs a warning and continues with the next partition, then summarizes succeeded/failed counts.
/// <para>批量刷写服务：把多分区镜像按顺序逐分区刷写到设备；单分区失败仅记日志警告并继续后续分区，
/// 最终汇总成功/失败数量。</para>
/// </summary>
public static class BatchFlashService
{
    /// <summary>Flashes the given partition images in order; a single-partition failure never aborts the rest, and per-partition progress is reported through <paramref name="progress"/> and broadcast as <see cref="FlashProgressMessage"/>.
    /// <para>顺序刷写给定的分区镜像；任一分区失败都不会中断其余分区，逐分区进度经 <paramref name="progress"/> 回报并以 <see cref="FlashProgressMessage"/> 广播。</para></summary>
    /// <param name="device">The target device.<para>目标设备。</para></param>
    /// <param name="entries">The partitions to flash: (partition name, full image file path).
    /// <para>待刷写分区列表：(分区名, 镜像文件完整路径)。</para></param>
    /// <param name="progress">Optional flash progress reporter.<para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token; stops the remaining partitions when requested.
    /// <para>取消令牌；请求取消后停止刷写剩余分区。</para></param>
    /// <returns>Aggregate result: success only when every partition succeeded, otherwise the failed partitions are listed.
    /// <para>汇总结果：仅当全部分区成功时为成功，否则列出失败分区。</para></returns>
    public static async Task<FlashResult> FlashSelectedAsync(
        DeviceInfo device,
        IReadOnlyList<(string Partition, string ImagePath)> entries,
        IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (entries is null || entries.Count == 0)
        {
            return new FlashResult { Success = false, Summary = "No partitions to flash.  没有待刷写的分区。" };
        }

        var succeeded = 0;
        var failures = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (partition, imagePath) = entries[i];
            var step = $"[{i + 1}/{entries.Count}]";
            var target = $"{Path.GetFileName(imagePath)} -> {partition}";
            var status = $"{step} Flashing {target}  正在刷写 {target}";
            progress?.Report(new FlashProgress(partition, 0, status));
            WeakReferenceMessenger.Default.Send(new FlashProgressMessage(partition, 0, status));
            LogService.Instance.Write(LogLevel.Info, "BatchFlash", status);

            var result = await FlashService.Default.FlashImageAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false);
            if (result.Success)
            {
                succeeded++;
                LogService.Instance.Write(LogLevel.Info, "BatchFlash",
                    $"{step} Flashed {target} OK: {result.Summary}  刷写成功：{target}");
            }
            else
            {
                failures.Add($"{target}: {result.Summary}");
                LogService.Instance.Write(LogLevel.Warn, "BatchFlash",
                    $"{step} Flash {target} failed, continuing with the next partition: {result.Summary}  刷写失败：{target}，继续刷写后续分区：{result.Summary}");
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }
        }

        var summary = failures.Count == 0
            ? $"Batch flash done: {succeeded}/{entries.Count} partition(s) OK  选中文件刷入完成：{succeeded}/{entries.Count} 个分区成功"
            : $"Batch flash done: {succeeded}/{entries.Count} OK, failed: {string.Join(", ", failures)}  选中文件刷入完成：{succeeded}/{entries.Count} 成功，失败：{string.Join("；", failures)}";
        return new FlashResult { Success = failures.Count == 0, Summary = summary };
    }
}