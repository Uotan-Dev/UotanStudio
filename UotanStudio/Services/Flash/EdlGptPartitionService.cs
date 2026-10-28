using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Everything one GPT extent write needs: the start sector, sector count, LUN, manifest filename and the
/// exact bytes to program.
/// <para>一次 GPT 范围写出所需的全部落位信息：起始扇区、扇区数、LUN、清单名与精确字节。</para>
/// </summary>
/// <param name="StartSector">First sector of the extent. <para>范围起始扇区。</para></param>
/// <param name="SectorCount">Sector count of the extent. <para>范围扇区数。</para></param>
/// <param name="Lun">Target physical partition number. <para>目标物理分区号。</para></param>
/// <param name="FileName">Filename token carried by the program command. <para>写入命令携带的文件名标记。</para></param>
/// <param name="Bytes">The exact bytes to program. <para>要写出的精确字节。</para></param>
internal sealed record EdlGptProgramRequest(long StartSector, long SectorCount, int Lun, string FileName,
    byte[] Bytes);

/// <summary>
/// Runs named-partition EDL program, erase and backup against the device's own GPT without any
/// rawprogram manifest.
/// <para>按分区名直接对设备自身的 GPT 执行 EDL 刷写、擦除与备份；不依赖 rawprogram 清单。</para>
/// </summary>
internal static class EdlGptPartitionService
{
    /// <summary>The DIAG read address of the streaming backend's on-device partition table, read from offset 0.
    /// <para>流式后端设备分区表的 DIAG 读取地址，从偏移 0 起读取。</para></summary>
    private const uint StreamingPartitionTableAddress = 0u;

    /// <summary>The highest LUN number probed by the by-name scan, spanning LUNs zero through seven.
    /// <para>按名扫描时尝试的最高 LUN 编号（0 至 7）。</para></summary>
    private const int MaxScannedLun = 7;

    /// <summary>
    /// Resolves the GPT snapshot holding the named partition: an explicit LUN reads only that LUN,
    /// while an omitted LUN scans LUNs zero through seven and returns the first table containing
    /// the name.
    /// <para>解析具名分区所在的 GPT 快照：显式给出 LUN 时只读该 LUN；为空时扫描 0 至 7 号
    /// LUN 并返回首个包含该名称的表。</para>
    /// </summary>
    /// <param name="firehose">The initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="partition">The partition name to locate (exact match). <para>要定位的分区名（精确匹配）。</para></param>
    /// <param name="lun">The explicit LUN; <see langword="null"/> scans by name. <para>显式 LUN；为空时按名扫描。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The owning snapshot and its partition entry. <para>所在快照与对应分区项。</para></returns>
    /// <exception cref="OperationCanceledException">The scan was cancelled. <para>扫描被取消。</para></exception>
    /// <exception cref="InvalidOperationException">No LUN produced a readable GPT. <para>没有任何 LUN 能读出 GPT。</para></exception>
    /// <exception cref="KeyNotFoundException">No readable GPT carries the partition name. <para>所有可读 GPT 中都没有该分区名。</para></exception>
    internal static async Task<(FirehoseGptSnapshot Snapshot, GptPartition Entry)> ResolvePartitionSnapshotAsync(
        FirehoseProtocol firehose, string partition, int? lun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        if (lun is { } explicitLun)
        {
            var snapshot = await firehose.ReadGptAsync(explicitLun, ct).ConfigureAwait(false);
            return (snapshot, snapshot.Table.FindPartition(partition));
        }

        Exception? firstFailure = null;
        var readableLunSeen = false;
        for (var candidate = 0; candidate <= MaxScannedLun; candidate++)
        {
            ct.ThrowIfCancellationRequested();
            FirehoseGptSnapshot scanned;
            try
            {
                scanned = await firehose.ReadGptAsync(candidate, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                and not ObjectDisposedException)
            {
                firstFailure ??= failure;
                continue;
            }

            readableLunSeen = true;
            if (scanned.Table.Partitions.Any(entry => string.Equals(entry.Name, partition, StringComparison.Ordinal)))
            {
                return (scanned, scanned.Table.FindPartition(partition));
            }
        }

        if (!readableLunSeen)
        {
            throw new InvalidOperationException(
                $"No LUN 0..{MaxScannedLun} reported a readable GPT. LUN 0..{MaxScannedLun} 均未能读出 GPT。",
                firstFailure);
        }

        throw new KeyNotFoundException(
            $"No readable GPT on LUN 0..{MaxScannedLun} carries a partition named '{partition}'. " +
            $"LUN 0..{MaxScannedLun} 的可读 GPT 均未上报名为 '{partition}' 的分区。");
    }

    /// <summary>
    /// Converts one device-GPT entry into a rawprogram-shaped entry so the existing entry-driven
    /// erase, readback and manifest flows can reuse it.
    /// <para>把设备 GPT 中的一项转成 rawprogram 形状的条目，供既有按条目流程（擦除、回读、清单生成）复用。</para>
    /// </summary>
    /// <param name="snapshot">The GPT snapshot owning the partition. <para>持有该分区的 GPT 快照。</para></param>
    /// <param name="partitionName">The partition name to convert. <para>要转换的分区名。</para></param>
    /// <returns>The rawprogram-shaped entry. <para>rawprogram 形状的条目。</para></returns>
    internal static RawProgramEntry ResolveEntry(FirehoseGptSnapshot snapshot, string partitionName)
    {
        var entry = snapshot.Table.FindPartition(partitionName);
        var sectors = checked((long)(entry.LastLba - entry.FirstLba + 1UL));
        return new RawProgramEntry
        {
            Label = partitionName,
            Filename = partitionName,
            StartSector = checked((long)entry.FirstLba).ToString(System.Globalization.CultureInfo.InvariantCulture),
            NumPartitionSectors = sectors,
            PhysicalPartitionNumber = snapshot.Geometry.PhysicalPartitionNumber,
            SectorSizeInBytes = snapshot.Geometry.SectorSize
        };
    }

    /// <summary>
    /// Writes a raw image into the named partition, located across LUNs when no LUN is given.
    /// <para>把镜像写入按名定位的分区；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name to flash. <para>要刷写的分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="verify">Whether to verify after programming. <para>编程后是否校验。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <param name="lun">The explicit LUN; null scans by name. <para>显式 LUN；为空时按名扫描。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    internal static async Task<FlashResult> FlashAsync(DeviceTarget device, string partition, string imagePath,
        bool verify, IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        progress?.Report(new FlashProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            if (session.Backend == EdlDownloadBackend.StreamDload && session.Streaming is { } streaming)
            {
                var entries = await EdlStreamingFlashService.ReadPartitionTableAsync(streaming,
                    StreamingPartitionTableAddress, ct).ConfigureAwait(false);
                var entry = EdlStreamingFlashService.ResolvePartition(entries, partition)
                    ?? throw new InvalidOperationException(
                        $"The Streaming DLOAD backend reported no partition named '{partition}'. 流式后端未上报名为 '{partition}' 的分区。");
                return await EdlStreamingFlashService.FlashAsync(streaming, entry, imagePath, progress, ct)
                    .ConfigureAwait(false);
            }

            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            var (snapshot, partitionEntry) = await ResolvePartitionSnapshotAsync(firehose, partition, lun, ct)
                .ConfigureAwait(false);
            progress?.Report(new FlashProgress(partition, 30,
                $"Programming {partition} at LBA {partitionEntry.FirstLba}... 正在编程 {partition}..."));
            var result = await firehose.ProgramRawPartitionFromFileAsync(snapshot, partition, imagePath,
                verify: verify, cancellationToken: ct).ConfigureAwait(false);
            progress?.Report(new FlashProgress(partition, 100, "Program complete. 编程完成。"));
            return new FlashResult
            {
                Success = true,
                Summary = $"EDL partition '{partition}' programmed from the device GPT " +
                    $"(LUN {snapshot.Geometry.PhysicalPartitionNumber}, LBA {partitionEntry.FirstLba}, " +
                    $"{result.AcknowledgedBytes} bytes, verified {result.VerifiedBytes}). " +
                    $"已按设备 GPT 写入分区 '{partition}'。"
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Erases the named partition using its reported range, located across LUNs when no LUN is given.
    /// <para>按分区上报的范围擦除该分区；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name to erase. <para>要擦除的分区名。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <param name="lun">The explicit LUN; null scans by name. <para>显式 LUN；为空时按名扫描。</para></param>
    /// <returns>The erase result. <para>擦除结果。</para></returns>
    internal static async Task<FlashResult> EraseAsync(DeviceTarget device, string partition,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        progress?.Report(new FlashProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            var (snapshot, entry) = await ResolvePartitionSnapshotAsync(firehose, partition, lun, ct)
                .ConfigureAwait(false);
            var start = checked((long)entry.FirstLba);
            var sectors = checked((long)(entry.LastLba - entry.FirstLba + 1UL));
            progress?.Report(new FlashProgress(partition, 30,
                $"Erasing {sectors} sectors of {partition}... 正在擦除 {partition} 的 {sectors} 个扇区..."));
            var response = await session.ErasePartitionAsync(start, sectors,
                snapshot.Geometry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (!response.IsAck)
            {
                return new FlashResult
                {
                    Success = false,
                    Summary = $"EDL erase of '{partition}' was rejected: {EdlFailureText.Describe(response)} 擦除被设备拒绝。"
                };
            }

            progress?.Report(new FlashProgress(partition, 100, "Erase complete. 分区擦除完成。"));
            return new FlashResult
            {
                Success = true,
                Summary = $"Erased partition '{partition}' ({sectors} sectors from LBA {start}) via Firehose erase. " +
                    "已通过 Firehose 擦除该分区。"
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the partitions named exactly <c>userdata</c> and <c>metadata</c> against the device's
    /// own GPT and erases each one whole; absent partitions are skipped and listed in the summary.
    /// <para>按设备自身 GPT 精确解析名为 <c>userdata</c> 与 <c>metadata</c> 的分区并整区擦除；
    /// 缺失的分区被跳过并在摘要中列出。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>The erase result whose summary lists the erased and the skipped partitions.
    /// <para>擦除结果，摘要列出已擦除与被跳过的分区。</para></returns>
    internal static async Task<FlashResult> FactoryResetAsync(DeviceTarget device,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
    {
        progress?.Report(new FlashProgress("factory-reset", 0,
            "Reading the device partition tables... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            var erased = new List<string>();
            var skipped = new List<string>();
            foreach (var partition in new[] { "userdata", "metadata" })
            {
                ct.ThrowIfCancellationRequested();
                FirehoseGptSnapshot snapshot;
                GptPartition entry;
                try
                {
                    (snapshot, entry) = await ResolvePartitionSnapshotAsync(firehose, partition, null, ct)
                        .ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    skipped.Add(partition);
                    continue;
                }

                var start = checked((long)entry.FirstLba);
                var sectors = checked((long)(entry.LastLba - entry.FirstLba + 1UL));
                progress?.Report(new FlashProgress(partition, 30,
                    $"Erasing {sectors} sectors of {partition}... 正在擦除 {partition} 的 {sectors} 个扇区..."));
                var response = await session.ErasePartitionAsync(start, sectors,
                    snapshot.Geometry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
                if (!response.IsAck)
                {
                    return new FlashResult
                    {
                        Success = false,
                        Summary = (erased.Count > 0
                                ? $"EDL factory reset erased '{string.Join("', '", erased)}' and then failed on "
                                : "EDL factory reset failed on ") +
                            $"'{partition}': {EdlFailureText.Describe(response)}. " +
                            "擦除该分区时被设备拒绝，工厂重置未完成。"
                    };
                }

                erased.Add(partition);
                progress?.Report(new FlashProgress(partition, 100, "Erase complete. 分区擦除完成。"));
            }

            if (erased.Count == 0)
            {
                return new FlashResult
                {
                    Success = false,
                    Summary = "No partition named 'userdata' or 'metadata' exists in any readable GPT, so the " +
                        "factory reset erased nothing. 可读 GPT 中不存在名为 'userdata' 或 'metadata' 的分区，未擦除任何内容。"
                };
            }

            progress?.Report(new FlashProgress("factory-reset", 100, "Factory reset complete. 工厂重置完成。"));
            return new FlashResult
            {
                Success = true,
                Summary = $"Erased '{string.Join("', '", erased)}' via Firehose erase" +
                    (skipped.Count > 0
                        ? $"; skipped absent '{string.Join("', '", skipped)}'. 已擦除所选分区；缺失的分区被跳过。"
                        : ". 已完成工厂重置擦除。")
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the named partition to a file, published atomically by the protocol; located across LUNs
    /// when no LUN is given.
    /// <para>把按名定位的分区复制到文件，由协议层原子发布；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name to back up. <para>要备份的分区名。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <param name="lun">The explicit LUN; null scans by name. <para>显式 LUN；为空时按名扫描。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    internal static async Task<BackupResult> BackupAsync(DeviceTarget device, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        progress?.Report(new BackupProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        await App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            if (session.Backend == EdlDownloadBackend.StreamDload && session.Streaming is { } streaming)
            {
                var entries = await EdlStreamingFlashService.ReadPartitionTableAsync(streaming,
                    StreamingPartitionTableAddress, ct).ConfigureAwait(false);
                var streamEntry = EdlStreamingFlashService.ResolvePartition(entries, partition)
                    ?? throw new InvalidOperationException(
                        $"The Streaming DLOAD backend reported no partition named '{partition}'. 流式后端未上报名为 '{partition}' 的分区。");
                await EdlStreamingFlashService.BackupAsync(streaming, streamEntry, outPath, progress, ct)
                    .ConfigureAwait(false);
                return true;
            }

            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            var (snapshot, entry) = await ResolvePartitionSnapshotAsync(firehose, partition, lun, ct)
                .ConfigureAwait(false);
            progress?.Report(new BackupProgress(partition, 10,
                $"Reading {entry.ByteLength / snapshot.Geometry.SectorSize} sectors of {partition}... 正在读取 {partition}..."));
            await firehose.ReadPartitionToFileAsync(snapshot, partition, outPath, cancellationToken: ct)
                .ConfigureAwait(false);
            progress?.Report(new BackupProgress(partition, 100, "Backup complete. 备份完成。"));
            return true;
        }, ct).ConfigureAwait(false);
        return new BackupResult
        {
            Success = true,
            Summary = $"Backed up partition '{partition}' to '{outPath}' using the device GPT. 已按设备 GPT 备份该分区。"
        };
    }

    /// <summary>
    /// Re-programs the primary and backup metadata extents of the device's own current GPT to repair
    /// a damaged backup table without touching partition payloads or accepting an external table.
    /// <para>把设备自身当前 GPT 的主、备元数据范围重新写回，用于修复被破坏的备份表；
    /// 不触碰分区数据，也不接受外部提供的表。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="confirmed">True when the partition-table rewrite was explicitly confirmed. <para>重写分区表已获显式确认时为 true。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <param name="physicalPartition">The physical partition (LUN) whose table is repaired; zero when none is selected. <para>要修复的物理分区号（LUN）；未选择时按 0。</para></param>
    /// <returns>The program result. <para>写入结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The selected LUN is negative. <para>所选 LUN 为负数。</para></exception>
    /// <exception cref="InvalidOperationException">The write was not confirmed, or the session has no Firehose protocol instance. <para>未确认写入，或会话缺少 Firehose 实例。</para></exception>
    internal static Task<FlashResult> RepairGptAsync(DeviceTarget device, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int physicalPartition = 0)
        => App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            if (!confirmed)
            {
                throw new InvalidOperationException(
                    "Rewriting the GPT requires explicit confirmation. 重写分区表需要显式确认。");
            }

            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            return await RepairGptCoreAsync(firehose, progress, physicalPartition, ct).ConfigureAwait(false);
        }, ct);

    internal static async Task<FlashResult> RepairGptCoreAsync(FirehoseProtocol firehose,
        IProgress<FlashProgress>? progress, int physicalPartition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        var snapshot = await firehose.ReadGptAsync(physicalPartition, cancellationToken: ct).ConfigureAwait(false);
        var requests = PlanGptWrite(snapshot.Table, snapshot.Geometry.SectorSize,
            snapshot.Geometry.SectorCount, snapshot.Geometry.PhysicalPartitionNumber);
        var programmed = 0L;
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            using var data = new MemoryStream(request.Bytes, writable: false);
            var response = await firehose.ProgramAsync(data, request.StartSector, request.SectorCount,
                request.FileName, request.Lun, ct).ConfigureAwait(false);
            if (response.IsNak)
            {
                throw new InvalidOperationException(
                    $"Firehose rejected the GPT extent starting at LBA {request.StartSector}: " +
                    $"{EdlFailureText.Describe(response)}. Firehose 拒绝了从 LBA {request.StartSector} 开始的分区表范围。");
            }

            programmed = checked(programmed + request.Bytes.Length);
            progress?.Report(new FlashProgress("gpt", 50,
                $"Programmed {request.Bytes.Length} bytes at LBA {request.StartSector}. 已在 LBA {request.StartSector} 写入 {request.Bytes.Length} 字节。"));
        }

        var reread = await firehose.ReadGptAsync(physicalPartition, cancellationToken: ct).ConfigureAwait(false);
        if (!string.Equals(reread.Table.MetadataSha256, snapshot.Table.MetadataSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The partition table read back after programming does not match the table that was written. 写入后读回的分区表与被写入的表不一致。");
        }

        progress?.Report(new FlashProgress("gpt", 100, "Partition table repaired. 分区表已修复。"));
        return new FlashResult
        {
            Success = true,
            Summary = $"{requests.Count} GPT extents programmed on LUN {snapshot.Geometry.PhysicalPartitionNumber} " +
                $"({programmed} bytes, readback fingerprint matched). 已按设备自身分区表写回 {requests.Count} 个 GPT 范围并回读校验一致。"
        };
    }

    /// <summary>
    /// Turns one GPT table into LUN-exact program requests, rejecting a table that disagrees with the
    /// device-reported geometry before any command is issued.
    /// <para>把一张 GPT 表转成按 LUN 精确落位的写请求，并在发出任何命令前拒绝与设备上报几何不符的表。</para>
    /// </summary>
    /// <param name="table">The partition table to write. <para>要写出的分区表。</para></param>
    /// <param name="sectorSize">The device-reported sector size. <para>设备上报的扇区大小。</para></param>
    /// <param name="sectorCount">The device-reported sector count. <para>设备上报的扇区数。</para></param>
    /// <param name="lun">The target physical partition number. <para>目标物理分区号。</para></param>
    /// <returns>The program requests, in order. <para>按序执行的写请求。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The sector size or sector count is not positive. <para>扇区大小或扇区数非正。</para></exception>
    /// <exception cref="InvalidDataException">The table claims more sectors than this LUN holds, or its block size differs from the reported sector size. <para>表声称的容量超出该 LUN，或块大小与上报扇区大小不一致。</para></exception>
    internal static IReadOnlyList<EdlGptProgramRequest> PlanGptWrite(GptTable table, long sectorSize,
        long sectorCount, int lun)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorCount);
        if (table.BlockSize != sectorSize)
        {
            throw new InvalidDataException(
                $"The table was read with {table.BlockSize}-byte blocks while the device reports {sectorSize}-byte sectors. 表的块大小与设备上报的扇区大小不一致。");
        }

        if ((long)table.BlockCount > sectorCount)
        {
            throw new InvalidDataException(
                $"The table claims {table.BlockCount} blocks but this LUN only holds {sectorCount} sectors. 该表声称的块数超出此 LUN 的扇区数。");
        }

        return
        [
            .. GptDeviceImage.BuildExtents(table).Select(extent => new EdlGptProgramRequest(
                checked((long)extent.StartLba),
                checked((long)extent.SectorCount),
                lun,
                $"gpt-{extent.StartLba}.img",
                extent.Bytes))
        ];
    }

    internal static Stream? OpenProgrammer(string? programmerPath) =>
        programmerPath is { } loader && File.Exists(loader) ? File.OpenRead(loader) : null;
}

/// <summary>
/// The result of a whole-LUN region read-back: the published path, the transferred byte count and the
/// byte-exact SHA-256 of the region.
/// <para>整 LUN 区域回读结果：落盘路径、读取字节数与逐字 SHA-256。</para>
/// </summary>
/// <param name="PhysicalPartitionNumber">The physical partition (LUN on UFS) that was read. <para>被读取的物理分区号（UFS 上即 LUN）。</para></param>
/// <param name="SectorCount">The sector count read, equal to the target-reported geometry capacity. <para>读取的扇区数，等于目标上报的几何容量。</para></param>
/// <param name="BytesTransferred">The exact bytes written to disk. <para>落盘的精确字节数。</para></param>
/// <param name="Sha256Hex">The lowercase hex SHA-256 of the region bytes. <para>区域字节的十六进制小写 SHA-256。</para></param>
/// <param name="DestinationPath">The final published destination path. <para>最终落盘路径。</para></param>
internal sealed record EdlRegionReadResult(int PhysicalPartitionNumber, long SectorCount, long BytesTransferred,
    string Sha256Hex, string DestinationPath);

/// <summary>
/// Provides EDL whole-region read-back over the device's own GPT; ranges come only from the
/// device-reported GPT and geometry.
/// <para>在设备自身 GPT 之上提供 EDL 侧整区回读；范围只来自设备上报的 GPT 与几何。</para>
/// </summary>
internal static class EdlGptRegionService
{
    private const int MaxScannedLun = 7;

    /// Reads one LUN whole, from sector 0 to the geometry-reported total sector count, into a file
    /// written chunk by chunk to a temporary path and atomically replaced on success.
    /// <para>把某个 LUN 从扇区 0 到几何上报的总扇区数整区回读到文件：按分块写入临时文件，
    /// 成功后原子替换目标，失败时保留既有文件。</para>
    /// </summary>
    /// <param name="firehose">An initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="physicalPartition">The LUN to read. <para>要读取的 LUN。</para></param>
    /// <param name="destinationPath">The full destination file path. <para>目标文件完整路径。</para></param>
    /// <param name="ct">The cancellation token for the geometry query and every read. <para>贯穿几何查询与每次读取的取消令牌。</para></param>
    /// <returns>The whole-region read-back result. <para>整区回读结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The physical partition number is negative. <para>物理分区号为负。</para></exception>
    /// <exception cref="FirehoseException">The target reported no usable geometry. <para>目标未报出可用几何。</para></exception>
    internal static async Task<EdlRegionReadResult> ReadLunRegionToFileAsync(FirehoseProtocol firehose,
        int physicalPartition, string destinationPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var geometry = await firehose.GetStorageGeometryAsync(physicalPartition, ct).ConfigureAwait(false);
        var temporaryPath = destinationPath + ".tmp";
        string sha256Hex;
        long bytes;
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var read = await firehose.ReadPartitionToStreamAsync(output, 0, geometry.SectorCount,
                    $"lun{physicalPartition}.bin", physicalPartition, cancellationToken: ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                sha256Hex = read.Sha256Hex;
                bytes = read.BytesWritten;
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new EdlRegionReadResult(physicalPartition, geometry.SectorCount, bytes, sha256Hex, destinationPath);
    }

    /// <summary>
    /// Queries geometry across LUNs zero through seven and reads each present LUN whole into its own
    /// file; unreadable LUNs are skipped and cancellation or failure preserves the temporary file.
    /// <para>跨 0 至 7 号 LUN 查询几何并把每个存在的 LUN 整区回读为各自的文件；读不出的
    /// LUN 被跳过，取消或失败时保留临时文件。</para>
    /// </summary>
    /// <param name="firehose">An initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="outputDirectory">The directory that receives one image per LUN. <para>按每个 LUN 接收一个镜像的目录。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The results for the LUNs that were read back. <para>每个已回读 LUN 的结果。</para></returns>
    /// <exception cref="ArgumentException">The directory path is blank. <para>目录路径为空白。</para></exception>
    internal static async Task<IReadOnlyList<EdlRegionReadResult>> ReadWholeFlashAsync(FirehoseProtocol firehose,
        string outputDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var results = new List<EdlRegionReadResult>();
        for (var lun = 0; lun <= MaxScannedLun; lun++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = Path.Combine(outputDirectory, $"lun{lun}.bin");
                results.Add(await ReadLunRegionToFileAsync(firehose, lun, path, ct).ConfigureAwait(false));
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not ObjectDisposedException)
            {
                continue;
            }
        }

        return results;
    }
}