using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>批量刷写服务：把多分区镜像按顺序逐分区刷写到设备，复用
/// <see cref="FlashService.FlashImageAsync"/> 现有公共刷写 API；单分区失败仅记日志警告并继续
/// 后续分区，最终汇总成功/失败数量。</para>
/// Batch flash service: flashes multiple partition images to a device sequentially by reusing the
/// existing public <see cref="FlashService.FlashImageAsync"/> API; a single-partition failure only
/// logs a warning and continues with the next partition, then summarizes succeeded/failed counts.
/// </summary>
public static class BatchFlashService
{
    /// <summary>
    /// <para>顺序刷写由调用方筛选出的选中分区镜像；任一分区失败不会中断后续分区。每个分区的进度经
    /// <paramref name="progress"/> 回报，同时发送 <see cref="FlashProgressMessage"/> 供输出面板展示。
    /// 每个分区在开始前和结束后各写一条日志，内容同时给出镜像文件名与目标分区名，逐条即可还原本次刷写
    /// 清单。</para>
    /// Flashes the selected partition images filtered by the caller in order; a single-partition failure
    /// never aborts the rest. Per-partition progress is reported through <paramref name="progress"/> and
    /// broadcast as <see cref="FlashProgressMessage"/> for the output panel. Each partition also writes
    /// one log line when it starts and one when it finishes, both naming the image file and the target
    /// partition, so the run can be traced image by image.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="entries">
    /// <para>调用方筛选后的选中分区列表：(分区名, 镜像文件完整路径)。</para>
    /// The caller-filtered selected partitions to flash: (partition name, full image file path).
    /// </param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌；取消后停止刷写剩余分区。</para>
    /// Cancellation token; cancels the remaining partitions when requested.</param>
    /// <returns>
    /// <para>汇总结果：全部分区成功时 <see cref="FlashResult.Success"/> 为 true，否则为 false 并列出失败分区。</para>
    /// Aggregate result: <see cref="FlashResult.Success"/> is true when every partition succeeded,
    /// otherwise false with the failed partitions listed.
    /// </returns>
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