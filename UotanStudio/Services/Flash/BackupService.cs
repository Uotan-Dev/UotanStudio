using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Messages;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Reads a device partition image to disk and flashes it back.
/// <para>读取设备分区镜像落盘，并反向刷回。</para>
/// </summary>
public static class BackupService
{
    /// <summary>
    /// Backs up a partition image to a local file; exceptions are captured and converted
    /// into a failure result.
    /// <para>备份分区镜像到本地文件；异常被捕获并转为失败结果。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">Target partition name, such as boot. <para>目标分区名（如 boot）。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    public static async Task<BackupResult> BackupPartitionAsync(
        DeviceTarget device, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new ArgumentException("Partition must not be empty.", nameof(partition));
            }

            return string.IsNullOrWhiteSpace(outPath)
                ? throw new ArgumentException("Output path must not be empty.", nameof(outPath))
                : device.Protocol switch
                {
                    DeviceProtocolKind.Fastboot => await Task.Run(() => BackupFastbootDevice(device, partition, outPath, progress, ct), ct).ConfigureAwait(false),
                    DeviceProtocolKind.Edl => await EdlGptPartitionService.BackupAsync(device, partition, outPath, progress, ct).ConfigureAwait(false),
                    DeviceProtocolKind.Rkdl => throw new InvalidOperationException(
                        "RKDL backup requires the Rockchip partition table; use RkdlSession.ReadLBA directly. RKDL 备份需自行提供分区表。"),
                    _ => throw new InvalidOperationException($"Unsupported protocol for backup: {device.Mode}")
                };
        }
        catch (OperationCanceledException)
        {
            return new BackupResult { Success = false, Summary = "Backup canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "BackupService", $"Backup {partition} failed: {ex.Message}");
            return new BackupResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Restores (flashes back) a partition; exceptions are captured and converted into a
    /// failure result.
    /// <para>还原（刷回）分区；异常被捕获并转为失败结果。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">Target partition name, such as boot. <para>目标分区名（如 boot）。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>待刷回的镜像文件完整路径。</para></param>
    /// <param name="progress">Optional restore progress reporter. <para>可选的还原进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The restore result. <para>还原结果。</para></returns>
    public static async Task<BackupResult> RestorePartitionAsync(
        DeviceTarget device, string partition, string imagePath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new ArgumentException("Partition must not be empty.", nameof(partition));
            }

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Image path must not be empty.", nameof(imagePath));
            }

            return !File.Exists(imagePath)
                ? throw new FileNotFoundException($"Image file not found: {imagePath}", imagePath)
                : device.Protocol switch
                {
                    DeviceProtocolKind.Fastboot => await Task.Run(() => RestoreFastbootDevice(device, partition, imagePath, progress, ct), ct).ConfigureAwait(false),
                    DeviceProtocolKind.Edl => MapEdlResult(await EdlGptPartitionService
                        .FlashAsync(device, partition, imagePath, verify: true, null, ct).ConfigureAwait(false)),
                    _ => throw new InvalidOperationException($"Unsupported protocol for restore: {device.Mode}")
                };
        }
        catch (OperationCanceledException)
        {
            return new BackupResult { Success = false, Summary = "Restore canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "BackupService", $"Restore {partition} failed: {ex.Message}");
            return new BackupResult { Success = false, Summary = ex.Message };
        }
    }

    private static BackupResult MapEdlResult(FlashResult result) => new()
    {
        Success = result.Success,
        Summary = result.Summary
    };

    /// <summary>
    /// Fastboot device-level backup: opens a session and delegates to
    /// <see cref="BackupFastbootPartition"/>.
    /// <para>Fastboot 设备级备份：打开会话后委托给
    /// <see cref="BackupFastbootPartition"/>。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">Target partition name. <para>目标分区名。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    private static BackupResult BackupFastbootDevice(
        DeviceTarget device, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        return App.DeviceMonitor.RunFastboot(device.Endpoint,
            session => BackupFastbootPartition(session, partition, outPath, progress, ct), ct);
    }

    /// <summary>
    /// Fastboot backup core: reads the partition size via <c>getvar partition-size</c> and
    /// streams the partition with <c>fetch</c> straight to disk (sparse preserved as-is).
    /// <para>Fastboot 备份核心：<c>getvar partition-size</c> 获取大小后用
    /// <c>fetch</c> 流式读取分区并原样落盘（sparse 不解析）。</para>
    /// </summary>
    /// <param name="session">A connected fastboot session. <para>已连接的 fastboot 会话。</para></param>
    /// <param name="partition">Target partition name. <para>目标分区名。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    public static BackupResult BackupFastbootPartition(
        FastbootSession session, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("Partition must not be empty.", nameof(partition));
        }

        var size = session.GetPartitionSizeLong(partition);
        if (size <= 0)
        {
            throw new InvalidOperationException(
                $"The device did not report a size for partition '{partition}'; fetch aborted. 设备未上报分区 '{partition}' 的大小。");
        }

        Report(progress, partition, 10, $"Fetching {partition} ({size} bytes)... 正在读取分区...");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? string.Empty);
        using var fs = File.Create(outPath);
        var byteProgress = new Progress<long>(read =>
            Report(progress, partition, (int)Math.Min(100, read * 100L / size), "Fetching partition... 正在读取分区..."));
        var response = session.FetchToStream(partition, fs, 0, size, byteProgress, ct);
        response.ThrowIfFailed();
        Report(progress, partition, 100, $"Backup complete: {partition}. 分区备份完成。");
        return new BackupResult { Success = true, Summary = $"Backed up '{partition}' ({fs.Length} bytes) via fastboot fetch.", Bytes = fs.Length };
    }

    /// <summary>
    /// Fastboot device-level restore: opens a session and delegates to
    /// <see cref="RestoreFastbootPartition"/>.
    /// <para>Fastboot 设备级还原：打开会话后委托给
    /// <see cref="RestoreFastbootPartition"/>。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">Target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>待刷回的镜像文件路径。</para></param>
    /// <param name="progress">Optional restore progress reporter. <para>可选的还原进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The restore result. <para>还原结果。</para></returns>
    private static BackupResult RestoreFastbootDevice(
        DeviceTarget device, string partition, string imagePath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        return App.DeviceMonitor.RunFastboot(device.Endpoint,
            session => RestoreFastbootPartition(session, partition, imagePath, progress, ct), ct);
    }

    /// <summary>
    /// Fastboot restore core: writes the image back with <c>flash &lt;partition&gt;</c>.
    /// <para>Fastboot 还原核心：<c>flash &lt;partition&gt;</c> 反向写回。</para>
    /// </summary>
    /// <param name="session">A connected fastboot session. <para>已连接的 fastboot 会话。</para></param>
    /// <param name="partition">Target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>待刷回的镜像文件路径。</para></param>
    /// <param name="progress">Optional restore progress reporter. <para>可选的还原进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The restore result. <para>还原结果。</para></returns>
    public static BackupResult RestoreFastbootPartition(
        FastbootSession session, string partition, string imagePath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        using var fs = File.OpenRead(imagePath);
        var totalBytes = fs.Length;
        Report(progress, partition, 10, $"Restoring {partition} via fastboot flash... 正在通过 fastboot 还原...");
        var byteProgress = new Progress<long>(written =>
            Report(progress, partition, totalBytes > 0 ? (int)Math.Min(100, written * 100L / totalBytes) : 0, "Restoring... 正在还原..."));
        var response = session.FlashPartition(partition, fs, totalBytes, byteProgress, ct);
        response.ThrowIfFailed();
        Report(progress, partition, 100, $"Restore complete: {partition}. 分区还原完成。");
        return new BackupResult { Success = true, Summary = $"Restored '{partition}' ({totalBytes} bytes) via fastboot flash.", Bytes = totalBytes };
    }

    /// <summary>EDL device-level backup: resolves the target partition geometry from a rawprogram via <see cref="ResolveEdlPartitionGeometry"/>, opens a session and delegates to the geometry-based session-level overload.
    /// <para>EDL 设备级备份：通过 <see cref="ResolveEdlPartitionGeometry"/> 解析 rawprogram 定位目标分区几何信息，打开会话后委托给几何参数重载 <see cref="BackupEdlPartitionAsync(EdlSession, string, long, long, string, IProgress{BackupProgress}?, CancellationToken)"/>。</para></summary>
    /// <param name="device">The target device.<para>目标设备。</para></param>
    /// <param name="partition">Target partition name (matched by label/filename in the rawprogram).
    /// <para>目标分区名（按 label/filename 匹配 rawprogram）。</para></param>
    /// <param name="outPath">Full path of the output file.<para>落盘文件完整路径。</para></param>
    /// <param name="rawProgramPath">Path of the rawprogram XML file providing the partition geometry.
    /// <para>提供分区几何信息的 rawprogram XML 文件路径。</para></param>
    /// <param name="progress">Optional backup progress reporter.<para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The backup result.<para>备份结果。</para></returns>
    public static async Task<BackupResult> BackupEdlPartitionAsync(
        DeviceTarget device, string partition, string outPath, string rawProgramPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("Partition must not be empty.", nameof(partition));
        }

        var (startSector, sectorCount) = ResolveEdlPartitionGeometry(rawProgramPath, partition);
        return await App.DeviceMonitor.RunEdlAsync(device.Endpoint,
            session => BackupEdlPartitionAsync(session, partition, startSector, sectorCount, outPath, progress, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// EDL backup core (rawprogram-entry based): resolves the sector geometry from the entry
    /// and streams the partition to disk via <see cref="BackupEdlToFileAsync"/>.
    /// <para>EDL 备份核心（rawprogram 条目版）：从条目解析扇区几何后经
    /// <see cref="BackupEdlToFileAsync"/> 流式落盘。</para>
    /// </summary>
    /// <param name="session">The EDL session, initialized on demand. <para>EDL 会话（按需初始化）。</para></param>
    /// <param name="entry">The target rawprogram entry (provides the sector geometry). <para>目标 rawprogram 条目（提供扇区几何信息）。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    public static async Task<BackupResult> BackupEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);

        await session.EnsureInitializedAsync(cancellationToken: ct).ConfigureAwait(false);
        var firehose = session.Firehose
            ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");

        var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;
        var sectorSize = entry.SectorSizeInBytes > 0
            ? entry.SectorSizeInBytes
            : (firehose.Configuration.SectorSize > 0 ? firehose.Configuration.SectorSize : 4096);
        if (entry.NumPartitionSectors <= 0)
        {
            throw new InvalidOperationException(
                $"rawprogram entry '{displayName}' has no num_partition_sectors; EDL backup cannot determine the partition size. 无法确定分区扇区数。");
        }

        var startSector = FlashService.IsRelativeStartSector(entry.StartSector)
            ? FlashService.ParseStartSectorValue(entry.StartSector, (
                await firehose.GetStorageGeometryAsync(entry.PhysicalPartitionNumber, ct).ConfigureAwait(false)).SectorCount)
            : FlashService.ParseStartSectorValue(entry.StartSector);
        return await BackupEdlToFileAsync(
            session, displayName, entry.Filename, entry.PhysicalPartitionNumber,
            startSector, entry.NumPartitionSectors, sectorSize,
            outPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>EDL backup core (geometry-based): streams a partition to disk with an explicit start sector and sector count via a Firehose <c>read</c>.
    /// <para>EDL 备份核心（几何参数版）：按显式起始扇区与扇区数经 Firehose <c>read</c> 流式落盘。</para></summary>
    /// <param name="session">The EDL session, initialized on demand.<para>EDL 会话（按需初始化）。</para></param>
    /// <param name="partition">Target partition name (used for progress reporting and the read filename).
    /// <para>目标分区名（用于进度展示与 read 文件名）。</para></param>
    /// <param name="startSector">The start sector.<para>起始扇区。</para></param>
    /// <param name="sectorCount">The number of sectors to read.<para>要读取的扇区数。</para></param>
    /// <param name="outPath">Full path of the output file.<para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter.<para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The backup result.<para>备份结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the start sector is negative or the sector count is not positive.
    /// <para>起始扇区为负或扇区数为非正时抛出。</para></exception>
    public static async Task<BackupResult> BackupEdlPartitionAsync(
        EdlSession session, string partition, long startSector, long sectorCount,
        string outPath, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("Partition must not be empty.", nameof(partition));
        }

        if (startSector < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSector), "Start sector must not be negative. 起始扇区不能为负。");
        }

        if (sectorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Sector count must be greater than zero. 扇区数必须大于 0。");
        }

        await session.EnsureInitializedAsync(cancellationToken: ct).ConfigureAwait(false);
        var firehose = session.Firehose
            ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
        var sectorSize = firehose.Configuration.SectorSize > 0 ? firehose.Configuration.SectorSize : 4096;

        return await BackupEdlToFileAsync(
            session, partition, partition, 0, startSector, sectorCount, sectorSize,
            outPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the geometry (start sector and sector count) of a target partition from a
    /// rawprogram XML file.
    /// <para>从 rawprogram XML 解析目标分区的几何信息（分区名→起始扇区/扇区数）。</para>
    /// </summary>
    /// <param name="rawProgramPath">Path of the rawprogram XML file providing the partition geometry. <para>提供分区几何信息的 rawprogram XML 文件路径。</para></param>
    /// <param name="partition">Target partition name (matched by label/filename). <para>目标分区名（按 label/filename 匹配）。</para></param>
    /// <returns>The partition geometry: start sector and sector count. <para>分区几何：起始扇区与扇区数。</para></returns>
    /// <exception cref="FileNotFoundException">Thrown when the rawprogram file is missing. <para>rawprogram 文件不存在时抛出。</para></exception>
    /// <exception cref="InvalidOperationException">Thrown when the partition is not found in the rawprogram or has no sector count. <para>分区未在 rawprogram 中匹配到，或无扇区数时抛出。</para></exception>
    public static (long StartSector, long SectorCount) ResolveEdlPartitionGeometry(string rawProgramPath, string partition)
    {
        if (string.IsNullOrWhiteSpace(rawProgramPath) || !File.Exists(rawProgramPath))
        {
            throw new FileNotFoundException($"rawprogram file not found: {rawProgramPath} rawprogram 文件不存在。", rawProgramPath);
        }

        var imageDir = Path.GetDirectoryName(Path.GetFullPath(rawProgramPath)) ?? string.Empty;
        var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
        var entry = FlashService.FindEdlTarget(entries, partition, imagePath: null)
            ?? throw new InvalidOperationException(
                $"Partition '{partition}' not found in rawprogram: {rawProgramPath}. 分区 '{partition}' 未在 rawprogram 中匹配到。");
        if (entry.NumPartitionSectors <= 0)
        {
            throw new InvalidOperationException(
                $"rawprogram entry for '{partition}' has no num_partition_sectors; EDL backup cannot determine the partition size. 无法确定分区扇区数。");
        }

        if (FlashService.IsRelativeStartSector(entry.StartSector))
        {
            throw new InvalidOperationException(
                $"rawprogram entry for '{partition}' uses a NUM_DISK_SECTORS-relative start_sector; resolve it against the live device geometry before restoring. 相对 start_sector 需先按设备几何解析后再还原。");
        }

        return (FlashService.ParseStartSectorValue(entry.StartSector), entry.NumPartitionSectors);
    }

    /// <summary>
    /// EDL backup-to-file core: initializes the session on demand and streams the partition to
    /// disk with a Firehose <c>read</c>.
    /// <para>EDL 备份落盘核心：确保会话初始化后经 Firehose <c>read</c> 流式读取分区并写入文件。</para>
    /// </summary>
    /// <param name="session">The EDL session. <para>EDL 会话。</para></param>
    /// <param name="displayName">Partition name shown in progress. <para>进度展示用分区名。</para></param>
    /// <param name="filename">The target filename for the Firehose read. <para>Firehose read 的目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The start sector. <para>起始扇区。</para></param>
    /// <param name="sectorCount">The number of sectors to read. <para>要读取的扇区数。</para></param>
    /// <param name="sectorSize">The sector size in bytes. <para>扇区大小（字节）。</para></param>
    /// <param name="outPath">Full path of the output file. <para>落盘文件完整路径。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    private static async Task<BackupResult> BackupEdlToFileAsync(
        EdlSession session, string displayName, string filename, int physicalPartition,
        long startSector, long sectorCount, long sectorSize,
        string outPath, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (sectorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Sector count must be greater than zero. 扇区数必须大于 0。");
        }

        await session.EnsureInitializedAsync(cancellationToken: ct).ConfigureAwait(false);

        Report(progress, displayName, 10, $"Reading {displayName} via Firehose read... 正在通过 Firehose 读取分区...");
        var fullPath = Path.GetFullPath(outPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        Directory.CreateDirectory(directory);
        var stagingPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".partial");
        var totalBytes = checked(sectorCount * sectorSize);
        var read = await SessionStreamReadAsync(session, stagingPath, displayName, filename, physicalPartition,
            startSector, sectorCount, totalBytes, progress, ct).ConfigureAwait(false);
        File.Move(read.tempPath, fullPath, true);

        Report(progress, displayName, 100, $"EDL backup complete: {displayName}. EDL 分区备份完成。");
        return new BackupResult
        {
            Success = true,
            Summary = $"Backed up EDL partition '{displayName}' ({read.result.BytesWritten} bytes, SHA-256 {read.result.Sha256Hex}).",
            Bytes = read.result.BytesWritten,
            Sha256Hex = read.result.Sha256Hex
        };
    }

    /// <summary>
    /// Drives the Firehose streaming read into a staging file; on failure or cancellation the
    /// staging file is deleted so an incomplete backup is never published.
    /// <para>把 Firehose 流式读取写入临时文件；失败或取消时删除临时文件，绝不发布不完整备份。</para>
    /// </summary>
    /// <param name="session">The EDL session. <para>EDL 会话。</para></param>
    /// <param name="stagingPath">Path of the temporary staging file. <para>临时暂存文件路径。</para></param>
    /// <param name="displayName">Partition name shown in progress. <para>进度展示用分区名。</para></param>
    /// <param name="filename">The target filename for the Firehose read. <para>Firehose read 的目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="startSector">The start sector. <para>起始扇区。</para></param>
    /// <param name="sectorCount">The number of sectors to read. <para>要读取的扇区数。</para></param>
    /// <param name="totalBytes">The expected total byte count. <para>预期总字节数。</para></param>
    /// <param name="progress">Optional backup progress reporter. <para>可选的备份进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The structured read result and the staging file path. <para>结构化读取结果与临时文件路径。</para></returns>
    private static async Task<(EdlStreamReadResult result, string tempPath)> SessionStreamReadAsync(
        EdlSession session, string stagingPath, string displayName, string filename, int physicalPartition,
        long startSector, long sectorCount, long totalBytes, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var byteProgress = new PartitionByteProgress(displayName, totalBytes, progress);
            var result = await session.ReadPartitionToStreamAsync(fs, startSector, sectorCount, filename,
                physicalPartition, byteProgress, expectedSha256Hex: null, ct).ConfigureAwait(false);
            if (!result.DeviceAcknowledged)
            {
                throw new IOException($"Firehose read of '{displayName}' completed without a device acknowledgement. 设备未确认本次读取。");
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
            return (result, stagingPath);
        }
        catch
        {
            TryDeleteStaging(stagingPath);
            throw;
        }
    }

    private static void TryDeleteStaging(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Bridges byte-level read progress into the backup percentage progress model.
    /// <para>把字节级读取进度桥接为备份百分比进度。</para>
    /// </summary>
    /// <param name="displayName">Partition name shown in progress. <para>进度展示用分区名。</para></param>
    /// <param name="totalBytes">Total byte count for the percentage calculation. <para>用于百分比计算的总字节数。</para></param>
    /// <param name="progress">The backup progress reporter. <para>备份进度报告器。</para></param>
    private sealed class PartitionByteProgress(string displayName, long totalBytes,
        IProgress<BackupProgress>? progress) : IProgress<long>
    {
        public void Report(long value)
        {
            if (progress is null || totalBytes <= 0)
            {
                return;
            }

            var percent = 10 + (int)Math.Clamp(value * 85 / totalBytes, 0, 85);
            BackupService.Report(progress, displayName, percent, $"Read {value}/{totalBytes} bytes of {displayName}. 已读取 {displayName} 的 {value}/{totalBytes} 字节。");
        }
    }

    /// <summary>
    /// EDL restore: delegates to the FlashService EDL single-partition flash.
    /// <para>EDL 还原：委托 FlashService 的 EDL 单分区刷写能力反向刷回。</para>
    /// </summary>
    /// <param name="session">The EDL session. <para>EDL 会话。</para></param>
    /// <param name="entry">The target rawprogram entry. <para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <param name="progress">Optional restore progress reporter. <para>可选的还原进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The restore result. <para>还原结果。</para></returns>
    public static async Task<BackupResult> RestoreEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);

        var bridge = new Progress<FlashProgress>(fp => Report(progress, fp.Partition, fp.Percent, fp.Status));
        var result = await FlashService.Default.FlashEdlPartitionAsync(session, entry, imageDir, bridge, ct).ConfigureAwait(false);
        return new BackupResult { Success = result.Success, Summary = result.Summary };
    }

    /// <summary>
    /// Last reported progress per partition, used for 100 ms throttling.
    /// <para>按分区记录的上次进度上报，用于 100ms 节流去重。</para>
    /// </summary>
    private static readonly PartitionProgressThrottle ProgressThrottle = new();

    /// <summary>
    /// Reports a progress entry through the given reporter and the progress message channel.
    /// Same-status reports within 100 ms are coalesced and the final 100% always gets through.
    /// <para>上报一条进度：写入进度报告器并广播进度消息。相同状态在 100ms 内重复上报会被合并，100% 终值必达。</para>
    /// </summary>
    /// <param name="progress">Optional progress reporter. <para>可选的进度报告器。</para></param>
    /// <param name="partition">The partition being reported, or null when not partition-specific. <para>正在上报的分区；非分区级时为 null。</para></param>
    /// <param name="percent">The progress percent, 0 to 100. <para>进度百分比（0 到 100）。</para></param>
    /// <param name="status">The status text. <para>状态文本。</para></param>
    private static void Report(IProgress<BackupProgress>? progress, string? partition, int percent, string status)
    {
        if (!ProgressThrottle.ShouldReport(partition, percent, status, Environment.TickCount64)) return;
        progress?.Report(new BackupProgress(partition, percent, status));
        WeakReferenceMessenger.Default.Send(new BackupProgressMessage(partition, percent, status));
    }
}

/// <summary>
/// Backup/restore operation result.
/// <para>备份/还原操作结果。</para>
/// </summary>
public sealed class BackupResult
{
    /// <summary>
    /// Gets or sets whether the operation succeeded.
    /// <para>获取或设置操作是否成功。</para>
    /// </summary>
    /// <value>True when the operation succeeded, otherwise false. <para>操作成功时为 true，否则为 false。</para></value>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result summary (description on success, or the failure reason).
    /// <para>获取或设置结果摘要（成功时的描述或失败原因）。</para>
    /// </summary>
    /// <value>The description on success or the failure reason. <para>成功时的描述或失败原因。</para></value>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the measured lowercase SHA-256 digest of an EDL streamed backup; null for
    /// non-EDL or unhashed paths.
    /// <para>获取或设置 EDL 流式备份实测的 SHA-256 十六进制摘要；非 EDL 或未哈希路径为 null。</para>
    /// </summary>
    /// <value>The measured lowercase SHA-256 digest of an EDL streamed backup. <para>EDL 流式备份实测的小写 SHA-256 摘要。</para></value>
    public string? Sha256Hex { get; set; }

    /// <summary>
    /// Gets or sets the total number of bytes processed.
    /// <para>获取或设置处理的总字节数。</para>
    /// </summary>
    /// <value>The total number of bytes processed. <para>处理的总字节数。</para></value>
    public long Bytes { get; set; }
}

/// <summary>
/// Backup/restore progress notification, aligned with <see cref="BackupProgressMessage"/>
/// fields.
/// <para>备份/还原进度通知，与 <see cref="BackupProgressMessage"/> 字段对齐。</para>
/// </summary>
/// <param name="partition">The partition being processed, or null. <para>正在处理的分区，可为 null。</para></param>
/// <param name="percent">The progress percent. <para>进度百分比。</para></param>
/// <param name="status">The status text. <para>状态文本。</para></param>
public sealed class BackupProgress(string? partition, int percent, string status)
{
    /// <summary>
    /// Gets the partition being processed; null when not partition-specific.
    /// <para>正在处理的分区；非分区级时为 null。</para>
    /// </summary>
    /// <value>The partition name, or null when not partition-specific. <para>分区名；非分区级时为 null。</para></value>
    public string? Partition { get; } = partition;

    /// <summary>
    /// Gets the progress percent (0-100).
    /// <para>获取进度百分比（0-100）。</para>
    /// </summary>
    /// <value>The progress percent from 0 to 100. <para>0 到 100 的进度百分比。</para></value>
    public int Percent { get; } = percent;

    /// <summary>
    /// Gets the status text.
    /// <para>获取状态文本。</para>
    /// </summary>
    /// <value>The status text. <para>状态文本。</para></value>
    public string Status { get; } = status;
}