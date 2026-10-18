using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Messages;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Flashing service that writes an image to a device partition over the Fastboot, EDL, RKDL and ADB
/// protocol paths and reports progress for each phase.
/// <para>刷写服务：按 Fastboot、EDL、RKDL 与 ADB 协议路径把镜像写入设备分区，并上报各阶段进度。</para>
/// </summary>
public sealed class FlashService : IFlashService
{
    /// <summary>The default singleton instance. <para>默认单例实例。</para></summary>
    /// <value>The shared instance the static entry points delegate to. <para>静态入口委托使用的共享实例。</para></value>
    public static FlashService Default { get; } = new();

    private FlashService() { }

    /// <summary>
    /// Flashes an image to a device partition over the protocol the device reports; a failure is
    /// captured and returned as a failure result.
    /// <para>按设备所上报的协议把镜像刷写到分区；失败会被捕获并作为失败结果返回。</para>
    /// </summary>
    /// <param name="device">The target device whose protocol selects the flash path. <para>决定刷写路径的目标设备。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file to flash. <para>待刷写镜像文件的完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result describing success or failure. <para>描述成功或失败的刷写结果。</para></returns>
    public async Task<FlashResult> FlashImageAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
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
                    DeviceProtocolKind.Fastboot => await FlashFastbootAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
                    DeviceProtocolKind.Edl => await FlashEdlAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
                    DeviceProtocolKind.Rkdl => await FlashRkdlAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
                    DeviceProtocolKind.Adb => await FlashAdbAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
                    _ => new FlashResult { Success = false, Summary = $"Unsupported protocol for flashing: {device.Mode}" }
                };
        }
        catch (OperationCanceledException)
        {
            return new FlashResult { Success = false, Summary = "Flash canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FlashService", $"Flash {partition} failed: {ex.Message}");
            return new FlashResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Erases a single partition (fastboot protocol only): opens a session via
    /// <see cref="DeviceMonitorService"/> and executes <c>erase &lt;partition&gt;</c>. Non-fastboot
    /// devices yield a failure result.
    /// <para>擦除单个分区（仅 fastboot 协议）：经 <see cref="DeviceMonitorService"/> 打开会话并执行
    /// <c>erase &lt;partition&gt;</c>。非 fastboot 设备返回失败结果。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">The partition name to erase. <para>要擦除的分区名。</para></param>
    /// <param name="progress">Optional erase progress reporter. <para>可选的擦除进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The erase result. <para>擦除结果。</para></returns>
    public async Task<FlashResult> ErasePartitionAsync(
        DeviceInfo device, string partition, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new ArgumentException("Partition must not be empty.", nameof(partition));
            }

            if (device.Protocol == DeviceProtocolKind.Edl)
            {
                return await EdlGptPartitionService.EraseAsync(device, partition, progress, ct).ConfigureAwait(false);
            }

            if (device.Protocol != DeviceProtocolKind.Fastboot)
            {
                return new FlashResult { Success = false, Summary = "Erase only supported via fastboot. 擦除仅支持 fastboot 模式。" };
            }

            Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
            return await Task.Run(() => App.DeviceMonitor.RunFastboot(device.Device, session =>
            {
                ct.ThrowIfCancellationRequested();
                Report(progress, partition, 30, $"Erasing {partition}... 正在擦除分区...");
                var response = session.ErasePartition(partition);
                response.ThrowIfFailed();
                Report(progress, partition, 100, $"Erase complete: {partition}. 分区擦除完成。");
                return new FlashResult
                {
                    Success = true,
                    Summary = $"Erased partition '{partition}' via fastboot."
                };
            }, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FlashResult { Success = false, Summary = "Flash canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FlashService", $"Erase {partition} failed: {ex.Message}");
            return new FlashResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Erases every partition on the device (fastboot protocol only): enumerates the partitions
    /// reported by <c>getvar:all</c> and erases each in turn with cumulative progress.
    /// <para>擦除设备上的全部分区（仅 fastboot 协议）：枚举 <c>getvar:all</c> 上报的分区，逐个擦除并汇报累计进度。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="progress">Optional erase progress reporter. <para>可选的擦除进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The erase result. <para>擦除结果。</para></returns>
    public async Task<FlashResult> EraseAllAsync(
        DeviceInfo device, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);

            if (device.Protocol != DeviceProtocolKind.Fastboot)
            {
                return new FlashResult { Success = false, Summary = "Erase all only supported via fastboot. 整机擦除仅支持 fastboot 模式。" };
            }

            Report(progress, null, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
            return await Task.Run(() => App.DeviceMonitor.RunFastboot(device.Device, session =>
            {
                var partitions = EnumeratePartitions(session);
                if (partitions.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No partitions reported by getvar:all; cannot erase all. getvar:all 未返回分区列表，无法整机擦除。");
                }

                var erased = 0;
                foreach (var partition in partitions)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, partition, (int)(erased * 100L / partitions.Count),
                        $"Erasing {partition}... 正在擦除分区 {partition}...");
                    var response = session.ErasePartition(partition);
                    response.ThrowIfFailed();
                    erased++;
                }

                Report(progress, null, 100, "Erase-all complete. 整机擦除完成。");
                return new FlashResult
                {
                    Success = true,
                    Summary = $"Erased {erased} partitions via fastboot."
                };
            }, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FlashResult { Success = false, Summary = "Flash canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FlashService", $"Erase-all failed: {ex.Message}");
            return new FlashResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Pushes an OTA package to the device in recovery sideload mode via ADB: it reboots into
    /// sideload, waits for the device to reappear, then streams the package to the sideload node.
    /// <para>通过 ADB 把 OTA 包推送到 recovery sideload 模式：先重启进入 sideload 并等待设备重新出现，
    /// 再把包流式推送到 sideload 设备节点。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="otaPackagePath">Full path of the OTA (zip) package. <para>OTA 镜像（zip）完整路径。</para></param>
    /// <param name="progress">Optional push progress reporter. <para>可选的推送进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The sideload result. <para>侧载结果。</para></returns>
    public async Task<FlashResult> FlashSideloadAsync(
        DeviceInfo device, string otaPackagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(otaPackagePath))
            {
                throw new ArgumentException("OTA package path must not be empty.", nameof(otaPackagePath));
            }

            if (!File.Exists(otaPackagePath))
            {
                throw new FileNotFoundException($"OTA image not found: {otaPackagePath} OTA 镜像不存在。", otaPackagePath);
            }

            Report(progress, "sideload", 0, "Opening ADB session... 正在打开 ADB 会话...");
            return await App.DeviceMonitor.RunAdbAsync(device.Device, async adb =>
            {
                Report(progress, "sideload", 5, "Rebooting into sideload mode... 正在重启进入 sideload 模式...");
                RebootToSideload(adb);
                await WaitForSideloadReconnectAsync(adb, ct).ConfigureAwait(false);
                await PushSideloadFileAsync(adb, otaPackagePath, progress, ct).ConfigureAwait(false);
                Report(progress, "sideload", 100, "Sideload complete. OTA 侧载完成。");
                return new FlashResult
                {
                    Success = true,
                    Summary = $"Sideloaded {Path.GetFileName(otaPackagePath)} via ADB."
                };
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FlashResult { Success = false, Summary = "Flash canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FlashService", $"Sideload {Path.GetFileName(otaPackagePath)} failed: {ex.Message}");
            return new FlashResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Queries a device property: <c>getvar &lt;name&gt;</c> (<see cref="FastbootSession.GetVariable"/>)
    /// on fastboot, <c>getprop &lt;name&gt;</c> (GetProp on <see cref="IAdbDeviceSession.CreateDeviceServices"/>)
    /// on ADB; the value is placed into <see cref="FlashResult.Summary"/> as <c>name=value</c>.
    /// <para>查询设备属性：fastboot 走 <c>getvar &lt;name&gt;</c>（<see cref="FastbootSession.GetVariable"/>），
    /// ADB 走 <c>getprop &lt;name&gt;</c>（<see cref="IAdbDeviceSession.CreateDeviceServices"/> 的 GetProp），
    /// 结果以 <c>name=value</c> 形式放入 <see cref="FlashResult.Summary"/>。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="propertyName">The property/variable name. <para>属性/变量名。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The query result whose summary holds the <c>name=value</c> pair. <para>摘要中给出 name=value 的查询结果。</para></returns>
    public async Task<FlashResult> GetDevicePropertyAsync(
        DeviceInfo device, string propertyName, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(device);
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("Property name must not be empty.", nameof(propertyName));
            }

            if (device.Protocol == DeviceProtocolKind.Hdc)
            {
                var value = await App.DeviceMonitor
                    .HdcPropertyAsync(device.Device, propertyName, ct).ConfigureAwait(false);
                return new FlashResult { Success = true, Summary = $"{propertyName}={value}" };
            }

            return await Task.Run(() =>
            {
                if (device.Protocol == DeviceProtocolKind.Fastboot)
                {
                    Report(null, "fastboot", 0, $"Querying fastboot variable '{propertyName}'... 正在查询 fastboot 变量...");
                    var fbValue = App.DeviceMonitor.RunFastboot(device.Device,
                        fastboot => fastboot.GetVariable(propertyName, useCache: false), ct);
                    Report(null, "fastboot", 100, $"Variable '{propertyName}' = '{fbValue}'. 变量查询完成。");
                    return new FlashResult { Success = true, Summary = $"{propertyName}={fbValue}" };
                }

                if (device.Protocol == DeviceProtocolKind.Adb)
                {
                    Report(null, "adb", 0, $"Querying property '{propertyName}'... 正在查询系统属性...");
                    var value = App.DeviceMonitor.RunAdb(device.Device,
                        adb => adb.CreateDeviceServices().GetProp(propertyName), ct);
                    Report(null, "adb", 100, $"Property '{propertyName}' = '{value}'. 属性查询完成。");
                    return new FlashResult { Success = true, Summary = $"{propertyName}={value}" };
                }

                return new FlashResult { Success = false, Summary = "Get property requires ADB, HDC or fastboot. 属性查询需要 ADB、HDC 或 fastboot。" };
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FlashResult { Success = false, Summary = "Flash canceled." };
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FlashService", $"Get property '{propertyName}' failed: {ex.Message}");
            return new FlashResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Enumerates the partition names reported by a fastboot device: parses the
    /// <c>partition-type:</c> / <c>partition-size:</c> prefixed variable keys from
    /// <c>getvar:all</c>, deduplicated.
    /// <para>枚举 fastboot 设备上报的分区名：解析 <c>getvar:all</c> 中 <c>partition-type:</c> /
    /// <c>partition-size:</c> 前缀的变量键，去重后返回。</para>
    /// </summary>
    /// <param name="session">An open fastboot session. <para>已打开的 fastboot 会话。</para></param>
    /// <returns>The deduplicated partition names. <para>去重后的分区名列表。</para></returns>
    private static List<string> EnumeratePartitions(FastbootSession session)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variables = session.GetAllVariables();
        foreach (var key in variables.Keys)
        {
            if (key.StartsWith(FastbootConstants.VarPartitionTypePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = key[FastbootConstants.VarPartitionTypePrefix.Length..];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            else if (key.StartsWith(FastbootConstants.VarPartitionSizePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = key[FastbootConstants.VarPartitionSizePrefix.Length..];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Fastboot path: opens a session via <see cref="DeviceMonitorService"/> and executes
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>, reporting progress per chunk.
    /// <para>Fastboot 路径：经 <see cref="DeviceMonitorService"/> 打开会话并执行
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>，逐块上报进度。</para>
    /// </summary>
    /// <param name="device">The target fastboot device. <para>目标 fastboot 设备。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    private static async Task<FlashResult> FlashFastbootAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var stateDir = ProjectContextService.Instance.Core.FindSubProject(imagePath)?.StateDirectory
            ?? ProjectContextService.Instance.StateDirectory;
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        return await Task.Run(() => App.DeviceMonitor.RunFastboot(device.Device, session =>
        {
            TryBackupBeforeFlash(session, partition, progress, ct, stateDir);

            var target = PrepareFlashTarget(session, partition, GetImageLogicalSize(imagePath), progress);
            var summary = FlashImageChunked(session, target, imagePath, progress, ct);
            Report(progress, target, 100, "Flash complete. 刷写完成。");
            return new FlashResult { Success = true, Summary = summary };
        }, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the flash target slot and prepares a logical partition by resizing it to the
    /// image's logical size so the download accepts an oversized image. Physical partitions are
    /// left untouched.
    /// <para>解析刷写目标槽位，并在目标为逻辑分区时把它调整为镜像的逻辑尺寸，使下载接受更大的镜像。</para>
    /// </summary>
    /// <param name="session">A connected fastboot session. <para>已连接的 fastboot 会话。</para></param>
    /// <param name="partition">The target partition name, which may lack a slot suffix. <para>目标分区名，可能尚未带槽位后缀。</para></param>
    /// <param name="imageSize">The image's logical size in bytes. <para>镜像的逻辑尺寸（字节）。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <returns>The slot-resolved name for the following <c>flash:</c>. <para>后续 <c>flash:</c> 使用的槽位解析后分区名。</para></returns>
    public static string PrepareFlashTarget(
        FastbootSession session, string partition, long imageSize, IProgress<FlashProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new ArgumentException("Partition must not be empty.", nameof(partition));
        }

        var target = session.ResolvePartition(partition);
        if (!session.IsLogical(target))
        {
            return target;
        }

        if (imageSize <= 0)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashService",
                $"Cannot determine the size of the image for logical partition {target}; flashing without resize. 无法确定逻辑分区镜像尺寸，跳过分区调整。");
            return target;
        }

        Report(progress, target, 1, $"Resizing logical partition {target} to {imageSize} bytes... 正在把逻辑分区调整到镜像大小...");
        var response = session.ResizeLogicalPartition(target, imageSize);
        if (!response.IsSuccess)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashService",
                $"Resize-logical-partition {target} to {imageSize} failed: {response.Info}; continuing flash. 逻辑分区大小调整失败，继续刷写。");
        }

        return target;
    }

    /// <summary>
    /// Gets the image's logical size: the expanded size declared by a sparse header
    /// (<c>total_blks × blk_sz</c>) or the file length of a raw image. Logical partition sizing
    /// uses this expanded size.
    /// <para>获取镜像的逻辑尺寸：稀疏镜像取头部声明的展开大小（<c>total_blks × blk_sz</c>），
    /// 原始镜像取文件长度。逻辑分区调整使用该展开尺寸。</para>
    /// </summary>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <returns>The image's logical size in bytes. <para>镜像逻辑尺寸（字节）。</para></returns>
    public static long GetImageLogicalSize(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        try
        {
            var header = UotanStudio.Firmware.BlockImage.Sparse.IO.SparseReader.PeekHeader(imagePath);
            if (header.IsValid())
            {
                return checked((long)header.TotalBlocks * header.BlockSize);
            }
        }
        catch (IOException)
        {
        }

        return new FileInfo(imagePath).Length;
    }

    /// <summary>
    /// Flashes an image honoring the fastboot download limit, sending it in a single download or
    /// splitting it into sparse chunks flashed sequentially, and deletes the chunk temp files
    /// afterwards.
    /// <para>按 fastboot 下载上限刷写镜像：一次性上传，或拆分为若干稀疏分片顺序刷写，随后删除分片临时文件。</para>
    /// </summary>
    /// <param name="session">An open fastboot session. <para>已打开的 fastboot 会话。</para></param>
    /// <param name="partition">The slot-resolved target partition. <para>已解析槽位的目标分区。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A summary line describing the bytes flashed. <para>描述已刷写字节数的摘要文本。</para></returns>
    private static string FlashImageChunked(
        FastbootSession session, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var maxDownloadSize = session.GetMaxDownloadSize();
        var imageLength = new FileInfo(imagePath).Length;

        static bool IsSparseImage(string path)
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return fs.Read(header) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) == UotanStudio.Firmware.BlockImage.Sparse.SparseFormat.SparseHeaderMagic;
        }

        if (imageLength <= maxDownloadSize)
        {
            using var image = File.OpenRead(imagePath);
            Report(progress, partition, 5, "Uploading image... 正在上传镜像...");
            var byteProgress = new Progress<long>(written =>
                Report(progress, partition, imageLength > 0 ? (int)Math.Min(100, written * 100L / imageLength) : 0, "Uploading image... 正在上传镜像..."));
            var response = session.FlashPartition(partition, image, imageLength, byteProgress, ct, resolveSlot: false,
                options: FlashPartitionOptions.PreparedTarget);
            response.ThrowIfFailed();
            return $"Flashed {partition} ({imageLength} bytes) via fastboot.";
        }

        var tempDir = Directory.CreateTempSubdirectory("uotan-flash-").FullName;
        try
        {
            var sparsePath = IsSparseImage(imagePath)
                ? imagePath
                : Path.Combine(tempDir, "source.sparse");
            if (!string.Equals(sparsePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, partition, 2, "Converting to sparse... 正在转换为稀疏格式...");
                UotanStudio.Firmware.BlockImage.Sparse.SparseImageConverter.ConvertRawToSparse(imagePath, sparsePath);
            }

            Report(progress, partition, 4, "Splitting sparse image... 正在拆分稀疏镜像...");
            UotanStudio.Firmware.BlockImage.Sparse.SparseImageConverter.ResparseImage(
                sparsePath, Path.Combine(tempDir, "chunk_{0}.sparse"), maxDownloadSize);

            var chunks = Directory.GetFiles(tempDir, "chunk_*.sparse")
                .OrderBy(c => int.TryParse(
                    Path.GetFileNameWithoutExtension(c).AsSpan("chunk_".Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
                .ToArray();

            var totalBytes = chunks.Sum(c => new FileInfo(c).Length);
            var bytesDone = 0L;
            for (var i = 0; i < chunks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var chunk = File.OpenRead(chunks[i]);
                var chunkLength = chunk.Length;
                var index = i;
                var chunkProgress = new Progress<long>(written =>
                    Report(progress, partition, totalBytes > 0 ? (int)Math.Min(100, (bytesDone + written) * 100L / totalBytes) : 0,
                        $"Uploading chunk {index + 1}/{chunks.Length}... 正在上传分片..."));
                var chunkResponse = session.FlashPartition(partition, chunk, chunkLength, chunkProgress, ct, resolveSlot: false,
                    options: FlashPartitionOptions.PreparedSparseChunks);
                chunkResponse.ThrowIfFailed();
                bytesDone += chunkLength;
            }

            return $"Flashed {partition} ({totalBytes} bytes in {chunks.Length} sparse chunk(s)) via fastboot.";
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Backs up the fastboot target partition to the project state directory before flashing when
    /// auto-backup is enabled. A failed backup only logs a warning and never aborts the flash.
    /// <para>启用自动备份时，在刷写前把 fastboot 目标分区备份到项目状态目录。备份失败仅记录警告，绝不中断刷写。</para>
    /// </summary>
    /// <param name="session">An open fastboot session. <para>已打开的 fastboot 会话。</para></param>
    /// <param name="partition">The partition to back up. <para>待备份的分区。</para></param>
    /// <param name="progress">Optional reporter sharing the flash progress channel. <para>与刷写共用进度通道的可选报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="stateDir">The state directory for backups, or null for the temp directory. <para>存放备份的状态目录，为 null 时使用临时目录。</para></param>
    private static void TryBackupBeforeFlash(
        FastbootSession session, string partition,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? stateDir)
    {
        try
        {
            if (!SettingsPersistenceService.Instance.LoadSettings().AutoBackupBeforeFlash)
            {
                return;
            }

            var backupDir = Path.Combine(stateDir ?? Path.GetTempPath(), "backup");
            Directory.CreateDirectory(backupDir);

            var safePartition = partition.AsSpan().ContainsAny(PathSearchValues.InvalidFileName)
                ? string.Concat(partition.Select(c => PathSearchValues.IsInvalidFileNameChar(c) ? '_' : c))
                : partition;
            var outPath = Path.Combine(backupDir, $"{safePartition}_{DateTime.Now:yyyyMMdd_HHmmss}.img");

            var backupProgress = new Progress<BackupProgress>(bp =>
                Report(progress, bp.Partition ?? partition, bp.Percent, bp.Status));
            var result = BackupService.BackupFastbootPartition(session, partition, outPath, backupProgress, ct);
            LogService.Instance.Write(
                result.Success ? LogLevel.Info : LogLevel.Warn,
                "FlashService",
                result.Success
                    ? $"Auto-backup before flash completed: {partition} -> {outPath}. 刷写前自动备份完成。"
                    : $"Auto-backup before flash failed: {partition} ({result.Summary}); continuing flash. 刷写前自动备份失败，继续刷写。");
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashService",
                "Auto-backup before flash canceled; continuing flash. 刷写前自动备份已取消，继续刷写。");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlashService",
                $"Auto-backup before flash failed: {partition} ({ex.Message}); continuing flash. 刷写前自动备份失败，继续刷写。");
        }
    }

    /// <summary>
    /// EDL path: matches the partition in the image directory's rawprogram manifest and programs
    /// that single entry, locating it through the device partition table when no manifest is
    /// present.
    /// <para>EDL 路径：在镜像目录的 rawprogram 清单中匹配分区并单发编程该条目；没有清单时改用设备分区表定位。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    private static async Task<FlashResult> FlashEdlAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Looking for EDL bundle... 正在查找 EDL 刷写包...");
        var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? string.Empty;
        var rawProgramPath = Directory.EnumerateFiles(imageDir, "rawprogram*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (rawProgramPath is null)
        {
            Report(progress, partition, 0,
                "No rawprogram in the image directory; using the device partition table. 镜像目录无 rawprogram，改用设备分区表定位。");
            return await EdlGptPartitionService.FlashAsync(device, partition, imagePath, verify: false, progress, ct)
                .ConfigureAwait(false);
        }

        var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
        var target = FindEdlTarget(entries, partition, imagePath);
        if (target is null)
        {
            Report(progress, partition, 0,
                $"Partition '{partition}' not found in rawprogram. 分区 '{partition}' 未在 rawprogram 中匹配到。");
            return new FlashResult { Success = false, Summary = $"Partition '{partition}' not found in rawprogram. 分区 '{partition}' 未在 rawprogram 中匹配到。" };
        }

        return await App.DeviceMonitor.RunEdlAsync(device.Device,
            edl => Default.FlashEdlPartitionAsync(edl, target, imageDir, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// EDL single-partition flash: initializes the session and flashes a single rawprogram
    /// entry with one Firehose program.
    /// <para>EDL 单分区直刷：确保会话初始化后把单个 rawprogram 条目经 Firehose program
    /// 单发刷写。</para>
    /// </summary>
    /// <param name="session">The EDL session, initialized on demand. <para>已（或尚未）初始化的 EDL 会话。</para></param>
    /// <param name="entry">The target rawprogram entry. <para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashEdlPartitionAsync(session, entry, imageDir, null, progress, ct).ConfigureAwait(false);

    /// <summary>
    /// Uploads a package-supplied Firehose programmer before programming a manifest entry.
    /// <para>编程清单条目前先上传包内自带的 Firehose programmer。</para>
    /// </summary>
    /// <param name="session">The EDL session, initialized on demand. <para>按需初始化的 EDL 会话。</para></param>
    /// <param name="entry">The target rawprogram entry. <para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <param name="programmerPath">Path of the Firehose programmer, or null to skip the upload. <para>Firehose programmer 路径，为 null 时跳过上传。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir, string? programmerPath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashEdlPartitionAsync(session, entry, imageDir, programmerPath, progress, ct, verify: false).ConfigureAwait(false);

    /// <summary>Uploads a package-supplied Firehose programmer before programming a manifest entry, with an optional read-back verification after a successful write.
    /// <para>上传包内 Firehose programmer（可选）后编程清单条目，并可在写入成功后执行回读校验。</para></summary>
    /// <param name="session">The EDL session, initialized on demand.<para>按需初始化的 EDL 会话。</para></param>
    /// <param name="entry">The target rawprogram entry.<para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory.<para>镜像文件所在目录。</para></param>
    /// <param name="programmerPath">Path of the Firehose programmer, or null to skip the upload.
    /// <para>Firehose programmer 路径，为 null 时跳过上传。</para></param>
    /// <param name="progress">Optional flash progress reporter.<para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <param name="verify">Whether to read the programmed range back and compare it with the source file.
    /// <para>是否回读已编程范围并与源文件比对。</para></param>
    /// <param name="initialization">Options governing session initialization, or null for the defaults.
    /// <para>控制会话初始化的选项，为 null 时使用默认值。</para></param>
    /// <returns>The flash result.<para>刷写结果。</para></returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir, string? programmerPath,
        IProgress<FlashProgress>? progress, CancellationToken ct, bool verify,
        EdlInitializationOptions? initialization = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (programmerPath is not null && !File.Exists(programmerPath))
        {
            throw new FileNotFoundException("Firehose programmer is missing.", programmerPath);
        }
        using var programmer = programmerPath is null ? null : File.OpenRead(programmerPath);
        await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
        var firehose = session.Firehose
            ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
        return await FlashEdlPartitionAsync(firehose, entry, imageDir, progress, ct, verify).ConfigureAwait(false);
    }

    /// <summary>
    /// EDL single-partition flash core: issues a single Firehose program directly against
    /// a <see cref="FirehoseProtocol"/> without a session or Sahara stage.
    /// <para>EDL 单分区直刷核心：不经过会话与 Sahara 阶段，直接对 Firehose 协议实例单发一条 program 命令。</para>
    /// </summary>
    /// <param name="firehose">A configured Firehose protocol instance. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="entry">The target rawprogram entry. <para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        FirehoseProtocol firehose, RawProgramEntry entry, string imageDir,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashEdlPartitionAsync(firehose, entry, imageDir, progress, ct, verify: false).ConfigureAwait(false);

    /// <summary>
    /// EDL single-partition flash core with an optional read-back gate: programs one Firehose range
    /// and skips verification with a note when a byte-for-byte comparison has no well-defined basis.
    /// <para>带可选回读门的 EDL 单分区直刷核心：单发编程一个 Firehose 范围，逐字节比对缺少明确基准时跳过回读并在结果中说明原因。</para>
    /// </summary>
    /// <param name="firehose">A configured Firehose protocol instance. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="entry">The target rawprogram entry. <para>目标 rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="verify">Whether to read the programmed range back and compare it with the source file. <para>写入成功后是否回读已编程范围并与源文件逐字节比对。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        FirehoseProtocol firehose, RawProgramEntry entry, string imageDir,
        IProgress<FlashProgress>? progress, CancellationToken ct, bool verify)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentNullException.ThrowIfNull(entry);

        var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;
        Report(progress, displayName, 10, $"EDL single-partition: {displayName}... EDL 单分区刷写：{displayName}...");

        if (string.IsNullOrEmpty(entry.Filename))
        {
            throw new InvalidOperationException(
                $"rawprogram entry '{displayName}' has no image filename; erase-only entries cannot be flashed as a single partition. program 条目无镜像文件。");
        }

        var sourceFile = ResolveEntrySource(entry, imageDir);
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException($"Image file not found for EDL partition '{displayName}': {sourceFile} 镜像文件不存在。", sourceFile);
        }

        var sectorSize = firehose.Configuration.SectorSize;
        if (sectorSize <= 0 || sectorSize > 65536)
        {
            throw new InvalidOperationException("Single-partition flashing requires a configured sector size.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(entry.SectorSizeInBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.NumPartitionSectors);
        if (entry.SectorSizeInBytes > 0 && entry.SectorSizeInBytes != sectorSize)
        {
            throw new InvalidDataException("The manifest sector size differs from the configured sector size.");
        }

        var startSector = entry.StartSector;

        long? verifyNumSectors = null;
        long verifyStartSector = 0;
        string? verifySkipReason = null;
        var truncated = false;

        Report(progress, displayName, 30, $"Sending program for {displayName}... 正在发送 program 命令...");
        using var stream = File.OpenRead(sourceFile);
        if (entry.IsSparse || IsAndroidSparseImage(stream))
        {
            verifySkipReason = "the image was programmed through the Android sparse path";
            if (entry.FileSectorOffset != 0)
            {
                throw new InvalidOperationException("Sparse program entries cannot use a raw source-sector offset.");
            }

            stream.Position = 0;
            var geometry = await firehose.GetStorageGeometryAsync(entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (geometry.SectorSize != sectorSize)
            {
                throw new InvalidDataException("The device sector size differs from the configured sector size.");
            }

            var resolvedStart = geometry.ResolveRange(startSector, 1);
            var maximumSectors = geometry.SectorCount - resolvedStart;
            if (entry.NumPartitionSectors > 0)
            {
                geometry.ResolveRange(startSector, entry.NumPartitionSectors);
                maximumSectors = entry.NumPartitionSectors;
            }

            var response = await firehose.ProgramSparseAsync(
                stream, startSector, entry.Filename, entry.PhysicalPartitionNumber,
                maximumSectors, ct).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new InvalidOperationException($"EDL sparse program failed for '{displayName}'. Response: {EdlFailureText.Describe(response)}");
            }
        }
        else
        {
            var sourceOffset = checked(entry.FileSectorOffset * sectorSize);
            if (sourceOffset < 0 || sourceOffset >= stream.Length)
            {
                throw new InvalidOperationException("The source-sector offset is outside the image.");
            }

            stream.Position = sourceOffset;
            var lengthFromOffset = stream.Length - stream.Position;
            var numSectors = FlashProgramLength.ResolveSectors(lengthFromOffset, entry.NumPartitionSectors, sectorSize);
            if (numSectors <= 0)
            {
                throw new InvalidOperationException($"Image for '{displayName}' is empty; nothing to program. 镜像为空。");
            }

            truncated = FlashProgramLength.IsTruncatedByDeclaration(lengthFromOffset, entry.NumPartitionSectors, sectorSize);

            var geometry = await firehose.GetStorageGeometryAsync(entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (geometry.SectorSize != sectorSize)
            {
                throw new InvalidDataException("The device sector size differs from the configured sector size.");
            }

            var resolvedStart = geometry.ResolveRange(startSector, numSectors);
            var response = await firehose.ProgramAsync(
                stream, startSector, numSectors, entry.Filename, entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new InvalidOperationException($"EDL program failed for '{displayName}'. Response: {EdlFailureText.Describe(response)}");
            }

            if (sourceOffset != 0)
            {
                verifySkipReason = "the entry programs from a non-zero file_sector_offset, so the device range does not start at the source file's first byte";
            }
            else if (checked(numSectors * sectorSize) > stream.Length)
            {
                verifySkipReason = "the final sector is zero-filled past the last image byte";
            }
            else if (checked(numSectors * sectorSize) < stream.Length)
            {
                verifySkipReason = "the image is longer than the programmed range";
            }
            else
            {
                verifyNumSectors = numSectors;
                verifyStartSector = resolvedStart;
            }
        }

        var summary = $"EDL partition '{displayName}' flashed via Firehose program.";
        if (truncated)
        {
            summary += $" Only {entry.NumPartitionSectors} declared sectors were programmed; the image tail was not written (truncated). 仅写入声明的 {entry.NumPartitionSectors} 个扇区，镜像尾部未写入。";
        }

        if (verify)
        {
            if (verifyNumSectors is null)
            {
                summary += $" Read-back verification skipped ({verifySkipReason}). 已跳过回读校验。";
            }
            else
            {
                Report(progress, displayName, 90, $"Verifying read-back for {displayName}... 正在回读校验...");
                try
                {
                    using var verificationStream = File.OpenRead(sourceFile);
                    await firehose.VerifyProgramAsync(
                        verificationStream, verifyStartSector, verifyNumSectors.Value,
                        entry.Filename, entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
                    summary += " Read-back verification passed. 回读校验通过。";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Instance.Write(LogLevel.Warn, "FlashService",
                        $"EDL read-back verification failed for '{displayName}': {ex.Message}");
                    Report(progress, displayName, 100, $"EDL read-back mismatch for {displayName}. {displayName} 回读校验不一致。");
                    return new FlashResult
                    {
                        Success = false,
                        Summary = $"EDL partition '{displayName}' was programmed but read-back verification failed (readback mismatch): {ex.Message} EDL 回读校验不一致。"
                    };
                }
            }
        }

        Report(progress, displayName, 100, $"EDL partition '{displayName}' flash complete. EDL 单分区刷写完成。");
        return new FlashResult
        {
            Success = true,
            Summary = summary
        };
    }

    /// <summary>
    /// Matches the target partition in a list of rawprogram entries by label/filename.
    /// <para>在 rawprogram 条目列表中按 label/filename 匹配目标分区。</para>
    /// </summary>
    /// <param name="entries">The rawprogram entries. <para>rawprogram 条目列表。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Optional image path whose filename also participates in matching. <para>文件名同样参与匹配的可选镜像路径。</para></param>
    /// <returns>The matching entry, or <c>null</c> when none matches. <para>命中的条目，未命中时为 null。</para></returns>
    public static RawProgramEntry? FindEdlTarget(IEnumerable<RawProgramEntry> entries, string partition, string? imagePath)
    {
        if (entries is null || string.IsNullOrWhiteSpace(partition))
        {
            return null;
        }

        var imageName = string.IsNullOrWhiteSpace(imagePath) ? null : Path.GetFileName(imagePath);
        var imageNameWithoutExt = imageName is null ? null : Path.GetFileNameWithoutExtension(imageName);
        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry.Filename);
            if (string.Equals(entry.Label, partition, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, partition, StringComparison.OrdinalIgnoreCase) ||
                (imageName is not null &&
                 (string.Equals(fileName, imageName, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(entry.Label, imageNameWithoutExt, StringComparison.OrdinalIgnoreCase))))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// RKDL path: opens an RKDL session, downloads the loader required in maskrom mode, parses
    /// the Rockchip parameter partition table and writes the image at the target partition LBA.
    /// <para>RKDL 路径：打开 RKDL 会话，下载 maskrom 模式必需的 loader，解析 Rockchip parameter 分区表，并按目标分区 LBA 写入镜像。</para>
    /// </summary>
    /// <param name="device">The target device. <para>目标设备。</para></param>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public static async Task<FlashResult> FlashRkdlAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashRkdlAsync(device, partition, imagePath, loaderPath: null, progress, ct).ConfigureAwait(false);

    /// <summary>RKDL path that programs a partition with an explicitly given loader path, discovered in the image directory when null.
    /// <para>RKDL 路径：按显式给出的 loader 路径刷写分区，为 null 时在镜像目录中自动发现。</para></summary>
    /// <param name="device">The target device.<para>目标设备。</para></param>
    /// <param name="partition">The target partition name.<para>目标分区名。</para></param>
    /// <param name="imagePath">Full path of the image file.<para>镜像文件完整路径。</para></param>
    /// <param name="loaderPath">Optional Rockchip loader file, auto-discovered in the image directory when null.
    /// <para>为 null 时在镜像目录自动发现的可选 Rockchip loader 文件。</para></param>
    /// <param name="progress">Optional flash progress reporter.<para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <param name="verify">Whether to read the written range back and compare it with the source image.
    /// <para>写入成功后是否回读写入范围并与源镜像比对。</para></param>
    /// <returns>The flash result.<para>刷写结果。</para></returns>
    public static async Task<FlashResult> FlashRkdlAsync(
        DeviceInfo device, string partition, string imagePath, string? loaderPath,
        IProgress<FlashProgress>? progress, CancellationToken ct, bool verify = false)
    {
        Report(progress, partition, 0, "Opening RKDL session... 正在打开 RKDL 会话...");
        return await App.DeviceMonitor.RunRkdlAsync(device.Device, async rkdl =>
        {
            var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? string.Empty;
            if (rkdl.IsMaskromMode)
            {
                var loader = ResolveLoaderPath(loaderPath, imageDir);
                if (loader is null)
                {
                    throw new InvalidOperationException(
                        "The device is in maskrom mode; a Rockchip loader (MiniLoaderAll*.bin / *loader*.bin) is required to enter loader mode. 设备处于 MaskROM 模式，需要 loader 才能进入 loader 模式。");
                }

                Report(progress, partition, 5, $"Downloading loader {Path.GetFileName(loader)}... 正在下载 loader...");
                var loaderBytes = await File.ReadAllBytesAsync(loader, ct).ConfigureAwait(false);
                await rkdl.DownloadBootLoaderAsync(loaderBytes, ct).ConfigureAwait(false);
            }

            var parameterPath = FindParameterFile(imageDir);
            if (parameterPath is null)
            {
                throw new InvalidOperationException(
                    "No Rockchip parameter file (parameter / parameter.txt) found in the image directory; RKDL flashing requires the partition table. 镜像目录中未找到 Rockchip parameter 分区表。");
            }

            var partitions = RockchipParameterParser.Parse(parameterPath);
            var imageName = Path.GetFileName(imagePath);
            var imageNameWithoutExt = Path.GetFileNameWithoutExtension(imageName);
            var target = partitions.FirstOrDefault(p =>
                string.Equals(p.Name, partition, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, imageName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, imageNameWithoutExt, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                var available = string.Join(", ", partitions.Select(p => p.Name));
                throw new InvalidOperationException(
                    $"Partition '{partition}' not found in the Rockchip partition table. Available: {available} 分区不在 Rockchip 分区表中。");
            }

            Report(progress, partition, 15, $"Writing {Path.GetFileName(imagePath)} to {target.Name} (LBA 0x{target.OffsetSectors:X})... 正在写入分区...");
            var result = await Task.Run(() => FlashRkdlPartition(rkdl, target, imagePath, progress, ct), ct).ConfigureAwait(false);
            if (!verify || !result.Success)
            {
                return result;
            }

            var imageLength = new FileInfo(imagePath).Length;
            if (imageLength <= 0)
            {
                result.Summary += " Read-back verification skipped (the image is empty). 已跳过回读校验（镜像为空）。";
                return result;
            }

            Report(progress, target.Name, 100, $"Verifying read-back for {target.Name} at LBA 0x{target.OffsetSectors:X}... 正在回读校验...");
            try
            {
                var matches = await Task.Run(async () =>
                {
                    using var readback = new RkdlReadbackStream(rkdl, target.OffsetSectors, imageLength);
                    return await CompareReadbackAsync(imagePath, readback, imageLength, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                if (!matches)
                {
                    LogService.Instance.Write(LogLevel.Warn, "FlashService",
                        $"RKDL read-back verification mismatch for '{target.Name}' at LBA 0x{target.OffsetSectors:X}.");
                    return new FlashResult
                    {
                        Success = false,
                        Summary = $"RKDL wrote {imageLength} bytes to '{target.Name}' but read-back verification failed (readback mismatch). RKDL 回读校验不一致。"
                    };
                }

                result.Summary += " Read-back verification passed. 回读校验通过。";
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Instance.Write(LogLevel.Warn, "FlashService",
                    $"RKDL read-back verification could not complete for '{target.Name}': {ex.Message}");
                return new FlashResult
                {
                    Success = false,
                    Summary = $"RKDL wrote '{target.Name}' but read-back verification could not complete (possible readback mismatch): {ex.Message} RKDL 回读校验失败。"
                };
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RKDL single-partition write core: writes the image to the partition LBA with
    /// <see cref="RkdlSession.WriteLBA"/>.
    /// <para>RKDL 单分区写入核心：用 <see cref="RkdlSession.WriteLBA"/> 把镜像写入分区 LBA。</para>
    /// </summary>
    /// <param name="session">An open RKDL session. <para>已打开的 RKDL 会话。</para></param>
    /// <param name="partitionInfo">The target partition entry from the Rockchip parameter table. <para>Rockchip parameter 分区表中的目标分区条目。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    public static FlashResult FlashRkdlPartition(
        RkdlSession session, RockchipPartition partitionInfo, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(partitionInfo);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        using var stream = File.OpenRead(imagePath);
        var totalBytes = stream.Length;
        Report(progress, partitionInfo.Name, 40, $"Writing image via RKDL WRITE_LBA at LBA 0x{partitionInfo.OffsetSectors:X}... 正在通过 RKDL 写入...");
        var byteProgress = new Progress<long>(written =>
            Report(progress, partitionInfo.Name,
                totalBytes > 0 ? 40 + (int)Math.Min(60, written * 60L / totalBytes) : 40,
                "Writing image via RKDL WRITE_LBA... 正在通过 RKDL 写入..."));
        var written = session.WriteLBA(partitionInfo.OffsetSectors, stream, totalBytes, byteProgress, ct);
        Report(progress, partitionInfo.Name, 100, $"RKDL flash complete: {partitionInfo.Name}. RKDL 刷写完成。");
        return new FlashResult
        {
            Success = true,
            Summary = $"Wrote {written} bytes to '{partitionInfo.Name}' (LBA 0x{partitionInfo.OffsetSectors:X}) via RKDL."
        };
    }

    /// <summary>
    /// ADB path: recovery sideload (for .zip OTA or explicit "sideload" targets) or online
    /// install (push to /data/local/tmp + pm install / apply update).
    /// <para>ADB 路径：recovery sideload（.zip OTA / 显式 sideload 目标）或在线安装
    /// （push 到 /data/local/tmp + pm install / apply update）。</para>
    /// </summary>
    /// <param name="device">The target ADB device. <para>目标 ADB 设备。</para></param>
    /// <param name="partition">The target partition or sideload name. <para>目标分区或 sideload 名。</para></param>
    /// <param name="imagePath">Full path of the image file. <para>镜像文件完整路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flash result. <para>刷写结果。</para></returns>
    private static async Task<FlashResult> FlashAdbAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening ADB session... 正在打开 ADB 会话...");
        return await App.DeviceMonitor.RunAdbAsync(device.Device, async adb =>
        {
            if (IsSideloadTarget(partition, imagePath))
            {
                Report(progress, partition, 5, "Rebooting into sideload mode... 正在重启进入 sideload 模式...");
                RebootToSideload(adb);
                await WaitForSideloadReconnectAsync(adb, ct).ConfigureAwait(false);
                await PushSideloadFileAsync(adb, imagePath, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await InstallOnlineAsync(adb, imagePath, progress, ct).ConfigureAwait(false);
            }

            Report(progress, partition, 100, "ADB flash complete. ADB 刷写完成。");
            return new FlashResult
            {
                Success = true,
                Summary = $"Flashed {Path.GetFileName(imagePath)} via ADB."
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the target is a recovery sideload (explicit "sideload" partition
    /// or a .zip OTA image).
    /// <para>判断目标是否为 recovery sideload（显式分区名 "sideload" 或 .zip OTA 镜像）。</para>
    /// </summary>
    /// <param name="partition">The target partition name. <para>目标分区名。</para></param>
    /// <param name="imagePath">The image file path. <para>镜像文件路径。</para></param>
    /// <returns>True when the target is a recovery sideload. <para>目标为 recovery sideload 时返回 true。</para></returns>
    public static bool IsSideloadTarget(string partition, string imagePath)
        => string.Equals(partition, "sideload", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetExtension(imagePath), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reboots the device into sideload mode via the ADB device services
    /// (<c>reboot:sideload</c>).
    /// <para>通过 ADB 设备服务重启设备进入 sideload 模式（<c>reboot:sideload</c>）。</para>
    /// </summary>
    /// <param name="session">A connected ADB session. <para>已连接的 ADB 会话。</para></param>
    public static void RebootToSideload(IAdbDeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.CreateDeviceServices().Reboot("sideload");
    }

    /// <summary>
    /// Streams the OTA to the sideload device node (<c>/sideload/package.zip</c>).
    /// <para>流式推送 OTA 到 sideload 设备节点（<c>/sideload/package.zip</c>）。</para>
    /// </summary>
    /// <param name="session">An ADB session connected to sideload mode. <para>已连接到 sideload 模式的 ADB 会话。</para></param>
    /// <param name="imagePath">The OTA image path. <para>OTA 镜像路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    public static async Task PushSideloadFileAsync(IAdbDeviceSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"OTA image not found: {imagePath} OTA 镜像不存在。", imagePath);
        }

        await using var sync = session.CreateSyncClient();
        await using var fs = File.OpenRead(imagePath);
        var totalBytes = fs.Length;
        await using var reporting = new ProgressReportingStream(fs, totalBytes, written =>
            Report(progress, "sideload", totalBytes > 0 ? (int)Math.Min(100, written * 100L / totalBytes) : 0, "Streaming OTA to /sideload/package.zip... 正在推送 OTA..."));
        await sync.PushStreamAsync(reporting, SideloadRemotePath, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Online install: pushes the image to <c>/data/local/tmp</c> and runs <c>pm install -r</c> for
    /// an APK, or the recovery <c>apply update</c> command otherwise.
    /// <para>在线安装：把镜像推送到 <c>/data/local/tmp</c>，APK 执行 <c>pm install -r</c>，其余走 recovery 的 <c>apply update</c>。</para>
    /// </summary>
    /// <param name="session">A connected ADB session. <para>已连接的 ADB 会话。</para></param>
    /// <param name="imagePath">The image path to install. <para>待安装镜像路径。</para></param>
    /// <param name="progress">Optional flash progress reporter. <para>可选的刷写进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    public static async Task InstallOnlineAsync(IAdbDeviceSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image not found: {imagePath} 镜像不存在。", imagePath);
        }

        var fileName = Path.GetFileName(imagePath);
        var remotePath = "/data/local/tmp/" + fileName;
        Report(progress, "adb", 30, $"Pushing {fileName} to /data/local/tmp... 正在推送镜像...");
        await using (var sync = session.CreateSyncClient())
        {
            await sync.PushAsync(imagePath, remotePath, cancellationToken: ct).ConfigureAwait(false);
        }

        var isApk = string.Equals(Path.GetExtension(fileName), ".apk", StringComparison.OrdinalIgnoreCase);
        var command = isApk
            ? $"pm install -r \"{remotePath}\""
            : $"apply update \"{remotePath}\"";
        Report(progress, "adb", 70, $"Running '{command}'... 正在执行安装命令...");
        var result = await session.ExecuteCommandAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var stderr = Encoding.UTF8.GetString(result.Stderr).Trim();
            throw new InvalidOperationException(
                $"ADB install command failed (exit {result.ExitCode}): {stderr} ADB 安装命令失败。");
        }
    }

    /// <summary>
    /// Waits for the device to reappear in sideload mode and reconnect (up to ~30 s).
    /// <para>等待设备在 sideload 模式下重新出现并重新连接（最多约 30 秒）。</para>
    /// </summary>
    /// <param name="session">The ADB session to reconnect. <para>待重连的 ADB 会话。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    private static async Task WaitForSideloadReconnectAsync(IAdbDeviceSession session, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await session.ConnectAsync(ct).ConfigureAwait(false);
                if (session.IsConnected && (session.UsesServer || session.PeerVersion > 0))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Device did not reappear in sideload mode; enter recovery sideload manually and retry. 设备未在 sideload 模式下重新出现。");
    }

    /// <summary>
    /// Last reported progress per partition, used for 100 ms throttling.
    /// <para>按分区记录的上次进度上报，用于 100ms 节流去重。</para>
    /// </summary>
    private static readonly PartitionProgressThrottle ProgressThrottle = new();

    /// <summary>
    /// Reports a progress entry through the given progress sink and the shared progress message
    /// channel. Duplicate reports with the same status within 100 ms are coalesced.
    /// <para>既写入给定的进度接收对象，也发送共享的进度消息。相同状态在 100 毫秒内的重复上报会被合并。</para>
    /// </summary>
    /// <param name="progress">Optional progress sink, which may be null. <para>可选的进度接收对象，可为 null。</para></param>
    /// <param name="partition">The partition the entry refers to, or null. <para>该条进度对应的分区，可为 null。</para></param>
    /// <param name="percent">The progress percent from 0 to 100. <para>0 到 100 的进度百分比。</para></param>
    /// <param name="status">The status text to show. <para>要显示的状态文本。</para></param>
    private static void Report(IProgress<FlashProgress>? progress, string? partition, int percent, string status)
    {
        if (!ProgressThrottle.ShouldReport(partition, percent, status, Environment.TickCount64)) return;
        progress?.Report(new FlashProgress(partition, percent, status));
        WeakReferenceMessenger.Default.Send(new FlashProgressMessage(partition, percent, status));
    }

    /// <summary>
    /// Resolves the source image path of a rawprogram entry.
    /// <para>解析 rawprogram 条目的源文件路径。</para>
    /// </summary>
    /// <param name="entry">The rawprogram entry. <para>rawprogram 条目。</para></param>
    /// <param name="imageDir">The image file directory. <para>镜像文件所在目录。</para></param>
    /// <returns>The resolved source path, or an empty string. <para>解析出的源文件路径，未找到时为空字符串。</para></returns>
    private static string ResolveEntrySource(RawProgramEntry entry, string imageDir)
    {
        return !string.IsNullOrEmpty(entry.SourceFile) && File.Exists(entry.SourceFile)
            ? entry.SourceFile
            : string.IsNullOrEmpty(entry.Filename)
            ? string.Empty
            : Path.Combine(imageDir, entry.Filename);
    }

    /// <summary>
    /// Parses a rawprogram start_sector (decimal or 0x-hex).
    /// <para>解析 rawprogram 的 start_sector（支持十进制与 0x 十六进制）。</para>
    /// </summary>
    /// <param name="value">The literal start_sector text. <para>清单里的 start_sector 原文。</para></param>
    /// <returns>The parsed sector number. <para>解析出的扇区号。</para></returns>
    public static long ParseStartSectorValue(string value)
    {
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dec))
        {
            return dec;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex)
            ? hex
            : throw new InvalidOperationException(
            $"Invalid start_sector value: '{value}'; single-partition EDL flash requires a numeric start_sector. start_sector 值无效。");
    }

    /// <summary>
    /// Whether a rawprogram start_sector is a <c>NUM_DISK_SECTORS-</c> relative expression (with an
    /// optional trailing dot), matching the address grammar used on the Firehose side.
    /// <para>判断 rawprogram 的 start_sector 是否为 <c>NUM_DISK_SECTORS-</c> 相对表达式
    /// （含可选结尾句点），与 Firehose 侧的地址文法保持一致。</para>
    /// </summary>
    /// <param name="value">The literal start_sector text. <para>清单里的 start_sector 原文。</para></param>
    /// <returns>True when the expression is disk-relative. <para>为相对表达式时返回 true。</para></returns>
    public static bool IsRelativeStartSector(string value)
        => value.Trim().TrimEnd('.').StartsWith("NUM_DISK_SECTORS-", StringComparison.Ordinal);

    /// <summary>Parses a rawprogram start_sector: absolute expressions return as-is, while <c>NUM_DISK_SECTORS-</c> relative expressions resolve against the LUN's sector count.
    /// <para>解析 rawprogram 的 start_sector：绝对表达式直接返回，<c>NUM_DISK_SECTORS-</c> 相对表达式 按该 LUN 的扇区总数求出绝对扇区。</para></summary>
    /// <param name="value">The literal start_sector text.<para>清单里的 start_sector 原文。</para></param>
    /// <param name="numDiskSectors">The physical partition's sector count used to resolve a relative expression.
    /// <para>用于解析相对表达式的物理分区扇区总数。</para></param>
    /// <returns>The absolute start sector.<para>绝对起始扇区。</para></returns>
    /// <exception cref="InvalidOperationException">A relative expression lacks a sector count, or the offset falls outside the disk.
    /// <para>相对表达式未提供扇区总数，或该偏移落在盘外。</para></exception>
    public static long ParseStartSectorValue(string value, long numDiskSectors)
    {
        if (!IsRelativeStartSector(value))
        {
            return ParseStartSectorValue(value);
        }

        if (numDiskSectors <= 0)
        {
            throw new InvalidOperationException(
                $"Relative start_sector '{value}' cannot be resolved without the LUN sector count. 相对 start_sector 需要该 LUN 的扇区总数。");
        }

        var offset = ParseStartSectorValue(value.Trim().TrimEnd('.')["NUM_DISK_SECTORS-".Length..]);
        if (offset <= 0 || offset > numDiskSectors)
        {
            throw new InvalidOperationException(
                $"Relative start_sector '{value}' falls outside a LUN of {numDiskSectors} sectors. 相对 start_sector 超出该 LUN 容量。");
        }

        return numDiskSectors - offset;
    }

    /// <summary>
    /// Detects the Android sparse image magic (0xED26FF3A).
    /// <para>检测 Android sparse 镜像魔数（0xED26FF3A）。</para>
    /// </summary>
    /// <param name="stream">The stream to probe at its current position. <para>在当前位置进行探测的流。</para></param>
    /// <returns>True when the sparse magic is present. <para>命中稀疏镜像魔数时返回 true。</para></returns>
    private static bool IsAndroidSparseImage(Stream stream)
    {
        var position = stream.Position;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 &&
                   magic[0] == 0x3A && magic[1] == 0xFF && magic[2] == 0x26 && magic[3] == 0xED;
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Locates a Rockchip loader file in the image directory.
    /// <para>在镜像目录中查找 Rockchip loader 文件。</para>
    /// </summary>
    /// <param name="loaderPath">The explicitly given loader path, which may be null. <para>显式给出的 loader 路径，可为 null。</para></param>
    /// <param name="imageDir">The directory to search for loader files. <para>搜索 loader 文件的目录。</para></param>
    /// <returns>The loader path, or null when none is found. <para>loader 路径，未找到时为 null。</para></returns>
    internal static string? ResolveLoaderPath(string? loaderPath, string imageDir)
    {
        if (!string.IsNullOrWhiteSpace(loaderPath) && File.Exists(loaderPath))
        {
            return Path.GetFullPath(loaderPath);
        }

        if (string.IsNullOrWhiteSpace(imageDir) || !Directory.Exists(imageDir))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(imageDir, "*.bin", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(imageDir, "*.loader", SearchOption.TopDirectoryOnly))
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("loader", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("MiniLoaderAll", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("rk", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("MiniLoaderAll", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>
    /// Locates a Rockchip parameter file (parameter / parameter.txt) in the image directory.
    /// <para>在镜像目录中查找 Rockchip parameter 文件（parameter / parameter.txt）。</para>
    /// </summary>
    /// <param name="imageDir">The directory to search. <para>待搜索的目录。</para></param>
    /// <returns>The parameter file path, or null when none is found. <para>parameter 文件路径，未找到时为 null。</para></returns>
    internal static string? FindParameterFile(string imageDir)
    {
        if (string.IsNullOrWhiteSpace(imageDir) || !Directory.Exists(imageDir))
        {
            return null;
        }

        var exact = Directory.EnumerateFiles(imageDir, "parameter", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(imageDir, "parameter.txt", SearchOption.TopDirectoryOnly))
            .FirstOrDefault();
        return exact is not null
            ? exact
            : Directory.EnumerateFiles(imageDir, "parameter*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => !Path.GetExtension(f).Equals(".xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether the directory contains a Rockchip parameter partition-table file;
    /// returns false for a missing directory without throwing.
    /// <para>判断目录中是否存在 Rockchip parameter 分区表文件；目录不存在时为 false，不抛异常。</para>
    /// </summary>
    /// <param name="imageDir">The directory to inspect. <para>待检查的目录。</para></param>
    /// <returns>True when a Rockchip parameter file is present. <para>存在 parameter 文件时为 true。</para></returns>
    internal static bool HasRockchipParameter(string imageDir)
        => FindParameterFile(imageDir) is not null;

    /// <summary>
    /// Compares a device read-back stream against the source file byte for byte using a bounded
    /// 64 KiB buffer, returning false early when the source is shorter or any block differs.
    /// <para>用 64 KiB 有界缓冲把设备回读流与源文件逐字节比对，源文件较短或任一块不一致时提前返回 false。</para>
    /// </summary>
    /// <param name="sourceFile">Path of the source image file. <para>源镜像文件路径。</para></param>
    /// <param name="deviceStream">The stream yielding device bytes. <para>提供设备回读字节的流。</para></param>
    /// <param name="length">The number of bytes to compare. <para>要比对的字节数。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>True when every compared byte matched. <para>所有比对字节都一致时为 true。</para></returns>
    private static async Task<bool> CompareReadbackAsync(string sourceFile, Stream deviceStream, long length, CancellationToken ct)
    {
        if (length <= 0)
        {
            return true;
        }

        const int ChunkSize = 65536;
        using var source = File.OpenRead(sourceFile);
        if (source.Length < length)
        {
            return false;
        }

        var sourceBuffer = new byte[ChunkSize];
        var deviceBuffer = new byte[ChunkSize];
        var remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ChunkSize, remaining);
            await source.ReadExactlyAsync(sourceBuffer.AsMemory(0, count), ct).ConfigureAwait(false);
            var filled = 0;
            while (filled < count)
            {
                var read = await deviceStream.ReadAsync(deviceBuffer.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                filled += read;
            }

            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(deviceBuffer.AsSpan(0, count)))
            {
                return false;
            }

            remaining -= count;
        }

        return true;
    }

    /// <summary>
    /// Remote push target in sideload mode.
    /// <para>sideload 模式下的远端推送目标。</para>
    /// </summary>
    private const string SideloadRemotePath = "/sideload/package.zip";

    /// <summary>
    /// Wraps a source stream, reporting progress by bytes read.
    /// <para>包装源流，按读取字节数上报进度。</para>
    /// </summary>
    private sealed class ProgressReportingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly Action<long> _onRead;
        private long _position;

        public ProgressReportingStream(Stream inner, long total, Action<long> onRead)
        {
            _inner = inner;
            _total = total;
            _onRead = onRead;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _position += n;
                _onRead(_position);
            }

            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            if (n > 0)
            {
                _position += n;
                _onRead(_position);
            }

            return n;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Wraps a range of <see cref="RkdlSession.ReadLBA"/> as a pull-based read-only stream, fetching
    /// device data in bounded 64 KiB whole-sector blocks so a full read-back never lands in memory.
    /// <para>把 <see cref="RkdlSession.ReadLBA"/> 的区间读取包装为拉取式只读流：每次以有界的
    /// 64 KiB 整扇区块向设备取数，避免整段回读进入内存。</para>
    /// </summary>
    /// <param name="session">The open RKDL session the range is read from. <para>读取该范围的已打开 RKDL 会话。</para></param>
    /// <param name="startLba">The first sector of the device range. <para>设备范围的首个扇区。</para></param>
    /// <param name="length">The total bytes the stream yields. <para>该流输出的总字节数。</para></param>
    private sealed class RkdlReadbackStream(RkdlSession session, long startLba, long length) : Stream
    {
        private const int BlockBytes = 65536;

        private readonly byte[] _block = new byte[BlockBytes];
        private int _blockLength;
        private int _blockPosition;
        private long _fetched;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _fetched; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_blockPosition >= _blockLength)
            {
                var remaining = length - _fetched;
                if (remaining <= 0)
                {
                    return 0;
                }

                var toFetch = (int)Math.Min(BlockBytes, remaining);
                using var blockStream = new MemoryStream(_block, 0, toFetch, writable: true);
                session.ReadLBA(startLba + _fetched / RkdlConstants.SectorSize, blockStream, toFetch);
                _blockLength = toFetch;
                _blockPosition = 0;
                _fetched += toFetch;
            }

            var copy = Math.Min(buffer.Length, _blockLength - _blockPosition);
            _block.AsSpan(_blockPosition, copy).CopyTo(buffer);
            _blockPosition += copy;
            return copy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Flash operation result.
/// <para>刷写操作结果。</para>
/// </summary>
public sealed class FlashResult
{
    /// <summary>
    /// Gets or sets whether the flash succeeded.
    /// <para>获取或设置刷写是否成功。</para>
    /// </summary>
    /// <value>Whether the flash succeeded. <para>刷写是否成功。</para></value>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result summary (description on success, or the failure reason).
    /// <para>获取或设置结果摘要（成功时的描述或失败原因）。</para>
    /// </summary>
    /// <value>The description on success or the failure reason. <para>成功时的描述或失败原因。</para></value>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Flash progress notification, aligned with <see cref="FlashProgressMessage"/> fields.
/// <para>刷写进度通知，与 <see cref="FlashProgressMessage"/> 字段对齐。</para>
/// </summary>
/// <param name="partition">The partition being flashed, or null. <para>正在刷写的分区，可为 null。</para></param>
/// <param name="percent">The progress percent from 0 to 100. <para>0 到 100 的进度百分比。</para></param>
/// <param name="status">The status text. <para>状态文本。</para></param>
public sealed class FlashProgress(string? partition, int percent, string status)
{
    /// <summary>
    /// Gets the partition being flashed; null when not partition-specific.
    /// <para>正在刷写的分区；非分区级时为 null。</para>
    /// </summary>
    /// <value>The partition name, or null for a phase-level entry. <para>分区名，阶段级条目时为 null。</para></value>
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
    /// <value>The status text describing the current phase. <para>描述当前阶段的状态文本。</para></value>
    public string Status { get; } = status;
}