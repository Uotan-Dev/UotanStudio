using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Streams one Rockchip partition to a file over an RKDL session. This is the reusable subset the existing
/// RKDL flash path does not provide: the caller supplies the parameter-table entry, the reader publishes the
/// destination through a same-directory temporary file and never opens a USB handle itself.
/// <para>经 RKDL 会话把一个 Rockchip 分区流式读到文件。这是现有 RKDL 刷写路径未提供的可复用子集：调用方提供
/// parameter 表条目，读取器经同目录临时文件发布目标，自身不打开 USB 句柄。</para>
/// </summary>
internal static class RkdlPartitionReader
{
    /// <summary>
    /// Reads the whole partition declared by <paramref name="partition"/> into <paramref name="destinationPath"/>.
    /// <para>把 <paramref name="partition"/> 声明的整个分区读入 <paramref name="destinationPath"/>。</para>
    /// </summary>
    /// <param name="device">Target RKDL endpoint.<para>目标 RKDL 端点。</para></param>
    /// <param name="partition">Parameter-table entry supplying the start LBA and sector count.<para>提供起始 LBA 与扇区数的 parameter 表条目。</para></param>
    /// <param name="destinationPath">Final output file; an existing file is replaced after a successful read.<para>最终输出文件；读取成功后替换既有文件。</para></param>
    /// <param name="progress">Optional cumulative byte progress.<para>可选的累计字节进度。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between transfer chunks.<para>在传输分块之间检查的取消令牌。</para></param>
    /// <returns>The number of bytes written.<para>写入的字节数。</para></returns>
    /// <exception cref="InvalidOperationException">The partition declares no sectors.<para>分区未声明扇区数。</para></exception>
    internal static async Task<long> ReadAsync(DetectedDevice device, RockchipPartition partition,
        string destinationPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (partition.SizeSectors <= 0)
        {
            throw new InvalidOperationException(
                $"Partition '{partition.Name}' declares no sectors. 分区 '{partition.Name}' 未声明扇区数。");
        }

        var length = checked(partition.SizeSectors * RockchipParameterParser.SectorSize);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The output path has no parent directory. 输出路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var written = await App.DeviceMonitor.RunRkdlAsync(device, async session =>
            {
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.SequentialScan);
                var bytes = await Task.Run(
                    () => session.ReadLBA(partition.OffsetSectors, file, length, progress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                return bytes;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destinationPath, overwrite: true);
            return written;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}