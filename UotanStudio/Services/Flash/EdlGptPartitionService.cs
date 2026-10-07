using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>一次 GPT 范围写出所需的全部落位信息：起始扇区、扇区数、LUN、清单名与精确字节。</para>
/// Everything one GPT extent write needs: the start sector, sector count, LUN, manifest filename and the
/// exact bytes to program.
/// </summary>
/// <param name="StartSector"><para>范围起始扇区。</para>First sector of the extent.</param>
/// <param name="SectorCount"><para>范围扇区数。</para>Sector count of the extent.</param>
/// <param name="Lun"><para>目标物理分区号。</para>Target physical partition number.</param>
/// <param name="FileName"><para>写入命令携带的文件名标记。</para>Filename token carried by the program command.</param>
/// <param name="Bytes"><para>要写出的精确字节。</para>The exact bytes to program.</param>
internal sealed record EdlGptProgramRequest(long StartSector, long SectorCount, int Lun, string FileName,
    byte[] Bytes);

/// <summary>
/// <para>按分区名直接对设备自身的 GPT 执行 EDL 刷写、擦除与备份；不依赖 rawprogram 清单。</para>
/// Runs named-partition EDL program, erase and backup against the device's own GPT without any
/// rawprogram manifest.
/// </summary>
/// <remarks>
/// <para>分区范围只来自设备上报的 GPT 与存储几何；槽位沿用当前 Firehose 配置，不做名字模糊匹配、
/// 不做容量推断，也不在失败后重试或回滚。</para>
/// <para>Ranges come only from the device-reported GPT and storage geometry; the slot stays as configured
/// in Firehose. Names are matched exactly, and no retry or rollback happens after a failure.</para>
/// </remarks>
internal static class EdlGptPartitionService
{
    /// <summary><para>流式后端设备分区表的 DIAG 读取地址（参考 read_partition_table 从 0 起读表）。</para>
    /// The DIAG read address of the streaming backend's on-device partition table; the reference reads it from
    /// offset 0.</summary>
    private const uint StreamingPartitionTableAddress = 0u;

    /// <summary><para>按名扫描时尝试的最高 LUN 编号，与刷写页 EdlLunBox 暴露的 0..7 一致。</para>
    /// The highest LUN number probed by the by-name scan, matching the 0..7 range the flash page's
    /// EdlLunBox exposes.</summary>
    private const int MaxScannedLun = 7;

    /// <summary>
    /// <para>解析具名分区所在的 GPT 快照：显式给出 LUN 时只读该 LUN；为空时按 0..<see cref="MaxScannedLun"/>
    /// 逐个扫描，返回第一个表内含该名称的 LUN。单个 LUN 的读取失败视为该 LUN 不可用并继续扫描；
    /// 全部 LUN 都读不出 GPT 时把首次失败作为内部异常抛出，以保留真实故障原因。</para>
    /// Resolves the GPT snapshot holding the named partition: an explicit LUN reads only that LUN, while a
    /// <see langword="null"/> LUN scans 0..<see cref="MaxScannedLun"/> and returns the first table that
    /// contains the name. A per-LUN read failure marks that LUN unusable and the scan continues; when no LUN
    /// yields a readable GPT the first failure is rethrown as the inner exception to keep the real cause.
    /// </summary>
    /// <param name="firehose"><para>已初始化的 Firehose 协议实例。</para>The initialized Firehose protocol.</param>
    /// <param name="partition"><para>要定位的分区名（精确匹配）。</para>The partition name to locate (exact match).</param>
    /// <param name="lun"><para>显式 LUN；为空时按名扫描。</para>The explicit LUN; <see langword="null"/> scans by name.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns><para>所在快照与对应分区项。</para>The owning snapshot and its partition entry.</returns>
    /// <exception cref="OperationCanceledException"><para>扫描被取消。</para>The scan was cancelled.</exception>
    /// <exception cref="InvalidOperationException"><para>没有任何 LUN 能读出 GPT。</para>
    /// No LUN produced a readable GPT.</exception>
    /// <exception cref="KeyNotFoundException"><para>所有可读 GPT 中都没有该分区名。</para>
    /// No readable GPT carries the partition name.</exception>
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
    /// <para>把设备 GPT 中的一项转成 rawprogram 形状的条目，供既有按条目流程（擦除、回读、清单生成）复用。</para>
    /// Converts one device-GPT entry into a rawprogram-shaped entry so the existing entry-driven
    /// erase, readback and manifest flows can reuse it.
    /// </summary>
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
    /// <para>把镜像写入按名定位的分区；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// Writes a raw image into the named partition, located across LUNs when no LUN is given.
    /// </summary>
    internal static async Task<FlashResult> FlashAsync(DeviceInfo device, string partition, string imagePath,
        bool verify, IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        progress?.Report(new FlashProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
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
    /// <para>按分区上报的范围擦除该分区；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// Erases the named partition using its reported range, located across LUNs when no LUN is given.
    /// </summary>
    internal static async Task<FlashResult> EraseAsync(DeviceInfo device, string partition,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        progress?.Report(new FlashProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
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
    /// <para>按设备自身 GPT 精确解析名为 <c>userdata</c> 与 <c>metadata</c> 的分区并整区擦除，用同一条
    /// Firehose 擦除路径把用户数据恢复到空状态。分区名精确匹配，LUN 传空时按名跨 LUN 扫描定位；
    /// 缺失的分区被跳过并在结果摘要中报告，两个分区都不存在时返回失败结果，不做任何猜测。</para>
    /// Resolves the partitions named exactly <c>userdata</c> and <c>metadata</c> against the device's own
    /// GPT and erases each one whole through the same Firehose erase path, returning the user area to an
    /// empty state. Names are matched exactly and located across LUNs when the LUN is null; an absent
    /// partition is skipped and reported in the result summary, and a device reporting neither returns a
    /// failure result instead of a guess.
    /// </summary>
    /// <param name="device"><para>目标 EDL 设备。</para>The target EDL device.</param>
    /// <param name="progress"><para>阶段进度。</para>Phase progress.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <param name="programmerPath"><para>可选的编程器覆盖路径。</para>Optional programmer override path.</param>
    /// <param name="initialization"><para>可选的会话认证选项。</para>Optional session authentication options.</param>
    /// <returns><para>擦除结果，摘要列出已擦除与被跳过的分区。</para>The erase result whose summary lists
    /// the erased and the skipped partitions.</returns>
    internal static async Task<FlashResult> FactoryResetAsync(DeviceInfo device,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
    {
        progress?.Report(new FlashProgress("factory-reset", 0,
            "Reading the device partition tables... 正在读取设备分区表..."));
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
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
    /// <para>把按名定位的分区复制到文件，由协议层原子发布；LUN 为空时按名跨 LUN 扫描定位。</para>
    /// Copies the named partition to a file, published atomically by the protocol; located across LUNs
    /// when no LUN is given.
    /// </summary>
    internal static async Task<BackupResult> BackupAsync(DeviceInfo device, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null, int? lun = null)
    {
        progress?.Report(new BackupProgress(partition, 0, "Reading the device partition table... 正在读取设备分区表..."));
        await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
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
    /// <para>把设备自身当前有效的 GPT 重新写回主、备两份元数据范围，用于修复被破坏的备份表头；
    /// 不触碰任何分区数据，也不接受外部提供的表。</para>
    /// Re-programs the primary and backup metadata extents of the device's own current GPT, which repairs a
    /// damaged backup table without touching any partition payload and without accepting an externally
    /// supplied table.
    /// </summary>
    /// <remarks>
    /// <para>范围只来自设备上报的几何与表布局，写入后重新读取并按元数据指纹比对；指纹变化即视为失败，
    /// 不重试、不回滚，也不在两个范围之间猜测顺序。调用方必须显式确认，因为写主表会立刻改变设备的
    /// 分区布局字节。</para>
    /// <para>Extents come only from the device-reported geometry and table layout; after programming the table is
    /// read again and compared by metadata fingerprint. A changed fingerprint is a failure, with no retry,
    /// rollback or guessed ordering between the two extents. The caller must confirm explicitly, because
    /// programming the primary extent immediately rewrites the device's layout bytes.</para>
    /// </remarks>
    /// <param name="device"><para>目标 EDL 设备。</para>The target EDL device.</param>
    /// <param name="confirmed"><para>调用方是否已显式确认写入分区表。</para>
    /// Whether the caller explicitly confirmed rewriting the partition table.</param>
    /// <param name="progress"><para>阶段进度。</para>Phase progress.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <param name="programmerPath"><para>可选的编程器覆盖路径。</para>Optional programmer override path.</param>
    /// <param name="initialization"><para>可选的会话认证选项。</para>Optional session authentication options.</param>
    /// <returns><para>写入结果。</para>The program result.</returns>
    /// <exception cref="InvalidOperationException"><para>未确认，或会话缺少 Firehose 实例。</para>
    /// The write was not confirmed, or the session has no Firehose protocol instance.</exception>
    internal static Task<FlashResult> RepairGptAsync(DeviceInfo device, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
        => App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
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
            return await RepairGptCoreAsync(firehose, progress, physicalPartition: 0, ct).ConfigureAwait(false);
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
    /// <para>把一张 GPT 表转成按 LUN 精确落位的写请求，并在发出任何命令前拒绝与设备上报几何不符的表。</para>
    /// Turns one GPT table into LUN-exact program requests, rejecting a table that disagrees with the
    /// device-reported geometry before any command is issued.
    /// </summary>
    /// <remarks>
    /// <para>Both extents must land wholly inside the reported sector count, and the table's block size must
    /// equal the reported sector size, because the programmed byte length is derived from that product. A
    /// table that claims more sectors than the LUN holds would otherwise push the backup extent past the
    /// end of the device area.</para>
    /// <para>两个范围都必须完整落在上报的扇区数之内，且表的块大小必须等于上报的扇区大小，因为写出的字节长度
    /// 正是由该乘积推得。若表声称的扇区数多于 LUN 实际容量，备份范围就会被推到设备区域之外。</para>
    /// </remarks>
    /// <param name="table"><para>要写出的分区表。</para>The partition table to write.</param>
    /// <param name="sectorSize"><para>设备上报的扇区大小。</para>The device-reported sector size.</param>
    /// <param name="sectorCount"><para>设备上报的扇区数。</para>The device-reported sector count.</param>
    /// <param name="lun"><para>目标物理分区号。</para>The target physical partition number.</param>
    /// <returns><para>按序执行的写请求。</para>The program requests, in order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><para>扇区大小或扇区数非正。</para>
    /// The sector size or sector count is not positive.</exception>
    /// <exception cref="InvalidDataException"><para>表声称的容量超出该 LUN，或块大小与上报扇区大小不一致。</para>
    /// The table claims more sectors than this LUN holds, or its block size differs from the reported
    /// sector size.</exception>
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

    private static Stream? OpenProgrammer(string? programmerPath) =>
        programmerPath is { } loader && File.Exists(loader) ? File.OpenRead(loader) : null;
}