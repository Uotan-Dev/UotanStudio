using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>面向流式 DLOAD 后端的 EDL 分区刷写、回读与分区表读取；命令模式目标在没有 Firehose programmer 时
/// 走本服务，而不是把没有 programmer 的会话错误地交给 Firehose 路径。</para>
/// Runs named-partition EDL program, backup and partition-table reads against the Streaming DLOAD backend,
/// which a command-mode target reaches without a Firehose programmer instead of being routed to the
/// programmer-only path.
/// </summary>
/// <remarks>
/// <para>Every write and read is driven through the caller's <see cref="EdlSession.Streaming"/> instance; the
/// host never opens the transport itself. Flash programming hands the image file stream to
/// <see cref="StreamingProtocol.WriteFlashAsync"/>, which is an irreversible write and therefore always carries
/// an explicit confirmation. Backup reads the partition in bounded <see cref="StreamingConstants.MaxMemoryReadBytes"/>
/// chunks into a staging file and replaces the destination only once the staged byte count equals the requested
/// length, so a protocol error or cancellation leaves any existing image untouched. This mirrors the atomic
/// publication discipline the MTK preloader and full-dump services already use.</para>
/// <para>每次写入与读取都由调用方的 <see cref="EdlSession.Streaming"/> 实例驱动；宿主从不自行打开传输。刷写把镜像文件流
/// 交给 <see cref="StreamingProtocol.WriteFlashAsync"/>，该写入不可逆，因此始终携带显式确认。回读按不超过
/// <see cref="StreamingConstants.MaxMemoryReadBytes"/> 的有界分块写入暂存文件，只有暂存字节数与请求长度相等时才替换目标，
/// 因此协议错误或取消都会保留既有镜像。这与 MTK preloader 与整区转储服务已采用的原子发布纪律一致。</para>
/// </remarks>
internal static class EdlStreamingFlashService
{
    /// <summary>The reference streaming block size the on-device partition table counts sectors in
    /// (streaming.py get_partitions blocksize). 参考流式块大小；设备分区表按此单位计扇区（streaming.py get_partitions）。</summary>
    internal const long StreamingSectorBytes = 4096;

    /// <summary>
    /// <para>把镜像按分区名编程到流式后端上报的起始地址；写满源文件长度后由目标侧确认收束。</para>
    /// Programs an image file into the start address the streaming backend reports for the named partition,
    /// confirming the whole source length through the target.
    /// </summary>
    internal static async Task<FlashResult> FlashAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        ArgumentNullException.ThrowIfNull(partition);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        var length = new FileInfo(imagePath).Length;
        progress?.Report(new FlashProgress(partition.Name, 0,
            $"Programming {partition.Name} through the Streaming DLOAD backend at 0x{partition.StartLba:X8}... " +
            $"正在经流式 DLOAD 后端编程 {partition.Name}..."));
        await using (var source = File.OpenRead(imagePath))
        {
            await streaming.WriteFlashAsync(partition.StartLba, partition.Name, source, length,
                ToChunkProgress(progress, partition.Name), confirmWrite: true, ct).ConfigureAwait(false);
        }

        progress?.Report(new FlashProgress(partition.Name, 100, "Stream flash complete. 流式编程完成。"));
        return new FlashResult
        {
            Success = true,
            Summary = $"EDL partition '{partition.Name}' programmed through the Streaming DLOAD backend " +
                $"(start 0x{partition.StartLba:X8}, {length} bytes). 已经流式 DLOAD 后端写入分区 '{partition.Name}'。"
        };
    }

    /// <summary>
    /// <para>把分区按有界分块读回并原子发布到目标文件；暂存长度与请求长度一致时才替换既有内容。</para>
    /// Reads the partition back in bounded chunks and publishes it atomically to the destination, replacing any
    /// existing content only once the staged length equals the requested length.
    /// </summary>
    internal static async Task<BackupResult> BackupAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, string outPath, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);

        var destination = Path.GetFullPath(outPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The backup destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The backup destination directory '{directory}' does not exist.");
        }

        var totalBytes = checked((long)partition.Sectors * StreamingSectorBytes);
        ct.ThrowIfCancellationRequested();
        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured = 0;
        progress?.Report(new BackupProgress(partition.Name, 0,
            $"Reading {partition.Name} ({totalBytes} bytes) through the Streaming DLOAD backend... " +
            $"正在经流式 DLOAD 后端读取 {partition.Name}..."));
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var offset = 0L; offset < totalBytes; offset += StreamingConstants.MaxMemoryReadBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(StreamingConstants.MaxMemoryReadBytes, totalBytes - offset);
                    var address = checked((uint)(partition.StartLba + offset));
                    var data = await streaming.ReadMemoryAsync(address, chunk, StreamingMemoryLinkMode.Auto, ct)
                        .ConfigureAwait(false);
                    await staging.WriteAsync(data, ct).ConfigureAwait(false);
                    captured = checked(captured + data.Length);
                    var percent = totalBytes == 0 ? 100 : (int)Math.Clamp(captured * 99 / totalBytes, 0, 99);
                    progress?.Report(new BackupProgress(partition.Name, percent,
                        $"Captured {captured} of {totalBytes} bytes... 已捕获 {captured}/{totalBytes} 字节..."));
                }

                if (staging.Length != totalBytes)
                {
                    throw new IOException(
                        $"The staged streaming backup holds {staging.Length} bytes after a reported {captured} but " +
                        $"{totalBytes} were requested, so the destination was left untouched.");
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }

        progress?.Report(new BackupProgress(partition.Name, 100, "Streaming backup complete. 流式备份完成。"));
        return new BackupResult
        {
            Success = true,
            Summary = $"Backed up partition '{partition.Name}' ({captured} bytes) to '{destination}' through the " +
                "Streaming DLOAD backend. 已经流式 DLOAD 后端备份该分区。"
        };
    }

    /// <summary>
    /// <para>读取设备自身的流式分区表并按参考记录格式解析为条目列表。</para>
    /// Reads the device's own streaming partition table and parses it into entries through the reference record
    /// format.
    /// </summary>
    internal static async Task<IReadOnlyList<StreamingPartitionEntry>> ReadPartitionTableAsync(
        StreamingProtocol streaming, uint tableAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        var bytes = await streaming.ReadMemoryAsync(tableAddress, StreamingConstants.PartitionTableBytes,
            StreamingMemoryLinkMode.Auto, ct).ConfigureAwait(false);
        return StreamingPartitionTable.Parse(bytes).Entries;
    }

    /// <summary>按名字精确挑选流式分区条目；缺失时返回 null，不做模糊匹配或容量推断。</summary>
    internal static StreamingPartitionEntry? ResolvePartition(IReadOnlyList<StreamingPartitionEntry> entries,
        string partitionName) =>
        entries.FirstOrDefault(entry => string.Equals(entry.Name, partitionName, StringComparison.OrdinalIgnoreCase));

    private static IProgress<long>? ToChunkProgress(IProgress<FlashProgress>? progress, string partitionName)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<long>(captured =>
            progress.Report(new FlashProgress(partitionName, 50,
                $"Streamed {captured} bytes... 已流式写出 {captured} 字节...")));
    }

    private static void DiscardStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}