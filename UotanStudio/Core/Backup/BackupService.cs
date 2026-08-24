using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Core.Flash;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Core.Backup;

/// <summary>
/// <para>备份/还原服务：读取设备分区镜像落盘，以及反向刷回。Fastboot 路径用
/// <c>fetch</c> 流式读取（保留 sparse 原样），EDL 路径用 Firehose <c>read</c>；还原复用
/// 刷写能力。禁止在此直接操作 USB 细节。</para>
/// Backup/restore service: reads a device partition image to disk and flashes it back.
/// The Fastboot path streams via <c>fetch</c> (sparse preserved as-is), the EDL path uses
/// the Firehose <c>read</c>; restore reuses the flash primitives. USB details must never be
/// touched here directly.
/// </summary>
public static class BackupService
{
    /// <summary>
    /// <para>备份分区镜像到本地文件；异常被捕获并转为失败结果。</para>
    /// Backs up a partition image to a local file; exceptions are captured and converted
    /// into a failure result.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名（如 boot）。</para>Target partition name (e.g. boot).</param>
    /// <param name="outPath"><para>落盘文件完整路径。</para>Full path of the output file.</param>
    /// <param name="progress"><para>可选的备份进度报告器。</para>Optional backup progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>备份结果。</returns>
    public static async Task<BackupResult> BackupPartitionAsync(
        DeviceInfo device, string partition, string outPath,
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
                    DeviceProtocolKind.Edl => throw new InvalidOperationException(
                        "EDL backup requires the rawprogram geometry; use BackupEdlPartitionAsync(device, partition, outPath, rawProgramPath, ...). EDL 备份需要 rawprogram 分区几何信息。"),
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
    /// <para>还原（刷回）分区；异常被捕获并转为失败结果。</para>
    /// Restores (flashes back) a partition; exceptions are captured and converted into a
    /// failure result.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名（如 boot）。</para>Target partition name (e.g. boot).</param>
    /// <param name="imagePath"><para>待刷回的镜像文件完整路径。</para>Full path of the image file.</param>
    /// <param name="progress"><para>可选的还原进度报告器。</para>Optional restore progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>还原结果。</returns>
    public static async Task<BackupResult> RestorePartitionAsync(
        DeviceInfo device, string partition, string imagePath,
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
                    DeviceProtocolKind.Edl => throw new InvalidOperationException(
                        "EDL restore requires the rawprogram geometry; use FlashService.FlashEdlPartitionAsync or RestoreEdlPartitionAsync. EDL 还原需要 rawprogram 分区几何信息。"),
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

    /// <summary>
    /// <para>Fastboot 设备级备份：打开会话后委托给
    /// <see cref="BackupFastbootPartition"/>。</para>
    /// Fastboot device-level backup: opens a session and delegates to
    /// <see cref="BackupFastbootPartition"/>.
    /// </summary>
    private static BackupResult BackupFastbootDevice(
        DeviceInfo device, string partition, string outPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        var usb = DeviceMonitorService.Instance.OpenUsbSession(device.Device);
        if (usb is null)
        {
            throw new InvalidOperationException("No USB transport available for the fastboot device. 无法获取 fastboot 设备的 USB 传输。");
        }

        var factory = new FastbootFactory();
        if (!factory.TryCreate(usb, out var protocolSession) || protocolSession is not FastbootSession fastboot)
        {
            throw new InvalidOperationException("The device does not expose a fastboot interface. 设备未暴露 fastboot 接口。");
        }

        using var session = fastboot;
        return !session.IsConnected
            ? throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。")
            : BackupFastbootPartition(session, partition, outPath, progress, ct);
    }

    /// <summary>
    /// <para>Fastboot 备份核心：<c>getvar partition-size</c> 获取大小后用
    /// <c>fetch</c> 流式读取分区并原样落盘（sparse 不解析）。</para>
    /// Fastboot backup core: reads the partition size via <c>getvar partition-size</c> and
    /// streams the partition with <c>fetch</c> straight to disk (sparse preserved as-is).
    /// </summary>
    /// <param name="session"><para>已连接的 fastboot 会话。</para>A connected fastboot session.</param>
    /// <param name="partition"><para>目标分区名。</para>Target partition name.</param>
    /// <param name="outPath"><para>落盘文件完整路径。</para>Full path of the output file.</param>
    /// <param name="progress"><para>可选的备份进度报告器。</para>Optional backup progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>备份结果。</returns>
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
    /// <para>Fastboot 设备级还原：打开会话后委托给
    /// <see cref="RestoreFastbootPartition"/>。</para>
    /// Fastboot device-level restore: opens a session and delegates to
    /// <see cref="RestoreFastbootPartition"/>.
    /// </summary>
    private static BackupResult RestoreFastbootDevice(
        DeviceInfo device, string partition, string imagePath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        var usb = DeviceMonitorService.Instance.OpenUsbSession(device.Device);
        if (usb is null)
        {
            throw new InvalidOperationException("No USB transport available for the fastboot device. 无法获取 fastboot 设备的 USB 传输。");
        }

        var factory = new FastbootFactory();
        if (!factory.TryCreate(usb, out var protocolSession) || protocolSession is not FastbootSession fastboot)
        {
            throw new InvalidOperationException("The device does not expose a fastboot interface. 设备未暴露 fastboot 接口。");
        }

        using var session = fastboot;
        return !session.IsConnected
            ? throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。")
            : RestoreFastbootPartition(session, partition, imagePath, progress, ct);
    }

    /// <summary>
    /// <para>Fastboot 还原核心：<c>flash &lt;partition&gt;</c> 反向写回。</para>
    /// Fastboot restore core: writes the image back with <c>flash &lt;partition&gt;</c>.
    /// </summary>
    /// <param name="session"><para>已连接的 fastboot 会话。</para>A connected fastboot session.</param>
    /// <param name="partition"><para>目标分区名。</para>Target partition name.</param>
    /// <param name="imagePath"><para>待刷回的镜像文件路径。</para>Full path of the image file.</param>
    /// <param name="progress"><para>可选的还原进度报告器。</para>Optional restore progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>还原结果。</returns>
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

    /// <summary>
    /// <para>EDL 设备级备份：解析 rawprogram 定位目标分区几何信息，打开会话后委托给
    /// <see cref="BackupEdlPartitionAsync(EdlSession, RawProgramEntry, string, IProgress{BackupProgress}?, CancellationToken)"/>。</para>
    /// EDL device-level backup: resolves the target partition geometry from a rawprogram,
    /// opens a session and delegates to the session-level overload.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名（按 label/filename 匹配 rawprogram）。</para>
    /// Target partition name (matched by label/filename in the rawprogram).</param>
    /// <param name="outPath"><para>落盘文件完整路径。</para>Full path of the output file.</param>
    /// <param name="rawProgramPath"><para>rawprogram*.xml 路径（提供分区几何信息）。</para>
    /// Path of the rawprogram*.xml providing the partition geometry.</param>
    /// <param name="progress"><para>可选的备份进度报告器。</para>Optional backup progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>备份结果。</returns>
    public static async Task<BackupResult> BackupEdlPartitionAsync(
        DeviceInfo device, string partition, string outPath, string rawProgramPath,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(rawProgramPath) || !File.Exists(rawProgramPath))
        {
            throw new FileNotFoundException($"rawprogram file not found: {rawProgramPath} rawprogram 文件不存在。", rawProgramPath);
        }

        var imageDir = Path.GetDirectoryName(Path.GetFullPath(rawProgramPath)) ?? string.Empty;
        var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
        var entry = FlashService.FindEdlTarget(entries, partition, imagePath: null)
            ?? throw new InvalidOperationException(
                $"Partition '{partition}' not found in rawprogram: {rawProgramPath}. 分区 '{partition}' 未在 rawprogram 中匹配到。");

        var session = DeviceMonitorService.Instance.OpenEdlSession(device.Device)
            ?? throw new InvalidOperationException("No EDL session available for the selected device. 无法为所选设备建立 EDL 会话。");
        using var edl = session;
        return await BackupEdlPartitionAsync(edl, entry, outPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>EDL 备份核心：经 Firehose <c>read</c> 读取分区并流式落盘。</para>
    /// EDL backup core: reads the partition with a Firehose <c>read</c> and streams it to
    /// disk.
    /// </summary>
    /// <param name="session"><para>EDL 会话（按需初始化）。</para>The EDL session, initialized on demand.</param>
    /// <param name="entry"><para>目标 rawprogram 条目（提供扇区几何信息）。</para>
    /// The target rawprogram entry (provides the sector geometry).</param>
    /// <param name="outPath"><para>落盘文件完整路径。</para>Full path of the output file.</param>
    /// <param name="progress"><para>可选的备份进度报告器。</para>Optional backup progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>备份结果。</returns>
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
        var startSector = FlashService.ParseStartSectorValue(entry.StartSector);
        if (entry.NumPartitionSectors <= 0)
        {
            throw new InvalidOperationException(
                $"rawprogram entry '{displayName}' has no num_partition_sectors; EDL backup cannot determine the partition size. 无法确定分区扇区数。");
        }

        var totalBytes = entry.NumPartitionSectors * sectorSize;
        Report(progress, displayName, 10, $"Reading {displayName} via Firehose read... 正在通过 Firehose 读取分区...");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? string.Empty);
        using var fs = File.Create(outPath);
        var response = await firehose.ReadToStreamAsync(
            fs, startSector, entry.NumPartitionSectors, entry.Filename, entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
        if (!response.IsAck)
        {
            throw new InvalidOperationException($"Firehose read failed for '{displayName}'. Response: {response.RawValue}");
        }

        Report(progress, displayName, 100, $"EDL backup complete: {displayName}. EDL 分区备份完成。");
        return new BackupResult { Success = true, Summary = $"Backed up EDL partition '{displayName}' ({fs.Length} bytes).", Bytes = fs.Length };
    }

    /// <summary>
    /// <para>EDL 还原：委托 FlashService 的 EDL 单分区刷写能力反向刷回。</para>
    /// EDL restore: delegates to the FlashService EDL single-partition flash.
    /// </summary>
    /// <param name="session"><para>EDL 会话。</para>The EDL session.</param>
    /// <param name="entry"><para>目标 rawprogram 条目。</para>The target rawprogram entry.</param>
    /// <param name="imageDir"><para>镜像文件所在目录。</para>The image file directory.</param>
    /// <param name="progress"><para>可选的还原进度报告器。</para>Optional restore progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>还原结果。</returns>
    public static async Task<BackupResult> RestoreEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);

        var bridge = new Progress<FlashProgress>(fp => Report(progress, fp.Partition, fp.Percent, fp.Status));
        var result = await FlashService.FlashEdlPartitionAsync(session, entry, imageDir, bridge, ct).ConfigureAwait(false);
        return new BackupResult { Success = result.Success, Summary = result.Summary };
    }

    /// <summary>
    /// <para>上报一条进度：写入调用方提供的 <see cref="IProgress{T}"/>，并发送
    /// <see cref="BackupProgressMessage"/> 供输出面板 Backup 通道展示。</para>
    /// Reports a progress entry: writes to the caller-provided <see cref="IProgress{T}"/>
    /// and sends a <see cref="BackupProgressMessage"/> for the output panel's Backup channel.
    /// </summary>
    private static void Report(IProgress<BackupProgress>? progress, string? partition, int percent, string status)
    {
        progress?.Report(new BackupProgress(partition, percent, status));
        WeakReferenceMessenger.Default.Send(new BackupProgressMessage(partition, percent, status));
    }
}

/// <summary>
/// <para>备份/还原操作结果。</para>
/// Backup/restore operation result.
/// </summary>
public sealed class BackupResult
{
    /// <summary>
    /// <para>获取或设置操作是否成功。</para>
    /// Gets or sets whether the operation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// <para>获取或设置结果摘要（成功时的描述或失败原因）。</para>
    /// Gets or sets the result summary (description on success, or the failure reason).
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置处理的总字节数。</para>
    /// Gets or sets the total number of bytes processed.
    /// </summary>
    public long Bytes { get; set; }
}

/// <summary>
/// <para>备份/还原进度通知，与 <see cref="BackupProgressMessage"/> 字段对齐。</para>
/// Backup/restore progress notification, aligned with <see cref="BackupProgressMessage"/>
/// fields.
/// </summary>
/// <remarks>
/// <para>初始化备份/还原进度。</para>
/// Initializes the backup/restore progress.
/// </remarks>
public sealed class BackupProgress(string? partition, int percent, string status)
{
    /// <summary>
    /// <para>正在处理的分区；非分区级时为 null。</para>
    /// Gets the partition being processed; null when not partition-specific.
    /// </summary>
    public string? Partition { get; } = partition;

    /// <summary>
    /// <para>获取进度百分比（0-100）。</para>
    /// Gets the progress percent (0-100).
    /// </summary>
    public int Percent { get; } = percent;

    /// <summary>
    /// <para>获取状态文本。</para>
    /// Gets the status text.
    /// </summary>
    public string Status { get; } = status;
}
