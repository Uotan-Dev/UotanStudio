using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Adb;
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
/// <para>真实刷写服务：通过 UotanStudio.Comm 协议层把镜像刷写到设备分区，
/// 统一封装 Fastboot / EDL / RKDL / ADB 四条路径并回报进度。禁止在此直接操作 USB 细节。
/// 实现 <see cref="IFlashService"/>，可通过 DI 注入；<see cref="Default"/> 提供默认单例，
/// 静态方法委托到单例以保持后向兼容。</para>
/// Real flashing service: writes an image to a device partition through the
/// UotanStudio.Comm protocol layer, unifying the Fastboot / EDL / RKDL / ADB paths with
/// progress reporting. Implements <see cref="IFlashService"/> for DI; <see cref="Default"/>
/// provides the default singleton; static methods delegate to it for backward compatibility.
/// </summary>
public sealed class FlashService : IFlashService
{
    /// <summary><para>默认单例实例。</para>The default singleton instance.</summary>
    public static FlashService Default { get; } = new();

    private FlashService() { }

    /// <inheritdoc />
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
    /// <para>擦除单个分区（仅 fastboot 协议）：经 <see cref="FastbootFactory"/> 打开会话并执行
    /// <c>erase &lt;partition&gt;</c>。非 fastboot 设备返回失败结果。</para>
    /// Erases a single partition (fastboot protocol only): opens a session via
    /// <see cref="FastbootFactory"/> and executes <c>erase &lt;partition&gt;</c>. Non-fastboot
    /// devices yield a failure result.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>要擦除的分区名。</para>The partition name to erase.</param>
    /// <param name="progress"><para>可选的擦除进度报告器。</para>Optional erase progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>擦除结果。</returns>
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

            if (device.Protocol != DeviceProtocolKind.Fastboot)
            {
                return new FlashResult { Success = false, Summary = "Erase only supported via fastboot. 擦除仅支持 fastboot 模式。" };
            }

            Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
            return await Task.Run(() =>
            {
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
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                }

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
            }).ConfigureAwait(false);
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
    /// <para>擦除设备上的全部分区（仅 fastboot 协议）：通过 <c>getvar:all</c> 枚举
    /// <c>partition-type</c>/<c>partition-size</c> 上报的分区，逐个执行 erase 并汇报累计进度。
    /// 非 fastboot 设备返回失败结果。</para>
    /// Erases every partition on the device (fastboot protocol only): enumerates the partitions
    /// reported via <c>getvar:all</c> as <c>partition-type</c>/<c>partition-size</c> keys and erases
    /// each in turn, reporting cumulative progress. Non-fastboot devices yield a failure result.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="progress"><para>可选的擦除进度报告器。</para>Optional erase progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>擦除结果。</returns>
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
            return await Task.Run(() =>
            {
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
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                }

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
            }).ConfigureAwait(false);
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
    /// <para>通过 ADB 把 OTA 包推送到 recovery sideload 模式：连接 ADB 会话后重启到 sideload、
    /// 等待设备重新出现，再把包流式推送到 <c>/sideload/package.zip</c>。</para>
    /// Pushes an OTA package to the device in recovery sideload mode via ADB: connects the ADB
    /// session, reboots into sideload, waits for the device to reappear, then streams the package
    /// to <c>/sideload/package.zip</c>.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="otaPackagePath"><para>OTA 镜像（zip）完整路径。</para>Full path of the OTA (zip) package.</param>
    /// <param name="progress"><para>可选的推送进度报告器。</para>Optional push progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>侧载结果。</returns>
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
            var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device)
                ?? throw new InvalidOperationException("No ADB session available for the selected device. 无法为所选设备建立 ADB 会话。");
            using var adb = session;
            adb.Connect();
            if (!adb.IsConnected)
            {
                throw new InvalidOperationException("ADB session is not connected. ADB 会话未连接。");
            }

            Report(progress, "sideload", 5, "Rebooting into sideload mode... 正在重启进入 sideload 模式...");
            RebootToSideload(adb);
            await WaitForSideloadReconnectAsync(adb, ct).ConfigureAwait(false);
            await Task.Run(() => PushSideloadFile(adb, otaPackagePath, progress, ct), ct).ConfigureAwait(false);
            Report(progress, "sideload", 100, "Sideload complete. OTA 侧载完成。");
            return new FlashResult
            {
                Success = true,
                Summary = $"Sideloaded {Path.GetFileName(otaPackagePath)} via ADB."
            };
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
    /// <para>查询设备属性：fastboot 走 <c>getvar &lt;name&gt;</c>（<see cref="FastbootSession.GetVariable"/>），
    /// ADB 走 <c>getprop &lt;name&gt;</c>（<see cref="AdbSession.CreateDeviceServices"/> 的 GetProp），
    /// 结果以 <c>name=value</c> 形式放入 <see cref="FlashResult.Summary"/>。</para>
    /// Queries a device property: <c>getvar &lt;name&gt;</c> (<see cref="FastbootSession.GetVariable"/>)
    /// on fastboot, <c>getprop &lt;name&gt;</c> (GetProp on <see cref="AdbSession.CreateDeviceServices"/>)
    /// on ADB; the value is placed into <see cref="FlashResult.Summary"/> as <c>name=value</c>.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="propertyName"><para>属性/变量名。</para>The property/variable name.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>查询结果（值在 <see cref="FlashResult.Summary"/> 中）。</returns>
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

            return await Task.Run(() =>
            {
                if (device.Protocol == DeviceProtocolKind.Fastboot)
                {
                    Report(null, "fastboot", 0, $"Querying fastboot variable '{propertyName}'... 正在查询 fastboot 变量...");
                    var fbSession = DeviceMonitorService.Instance.OpenFastbootSession(device.Device)
                        ?? throw new InvalidOperationException("No fastboot session available for the selected device. 无法为所选设备建立 fastboot 会话。");
                    using var fastboot = fbSession;
                    if (!fastboot.IsConnected)
                    {
                        throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
                    }

                    var fbValue = fastboot.GetVariable(propertyName, useCache: false);
                    Report(null, "fastboot", 100, $"Variable '{propertyName}' = '{fbValue}'. 变量查询完成。");
                    return new FlashResult { Success = true, Summary = $"{propertyName}={fbValue}" };
                }

                if (device.Protocol == DeviceProtocolKind.Adb)
                {
                    Report(null, "adb", 0, $"Querying property '{propertyName}'... 正在查询系统属性...");
                    var adbSession = DeviceMonitorService.Instance.OpenAdbSession(device.Device)
                        ?? throw new InvalidOperationException("No ADB session available for the selected device. 无法为所选设备建立 ADB 会话。");
                    using var adb = adbSession;
                    adb.Connect();
                    if (!adb.IsConnected)
                    {
                        throw new InvalidOperationException("ADB session is not connected. ADB 会话未连接。");
                    }

                    var value = adb.CreateDeviceServices().GetProp(propertyName);
                    Report(null, "adb", 100, $"Property '{propertyName}' = '{value}'. 属性查询完成。");
                    return new FlashResult { Success = true, Summary = $"{propertyName}={value}" };
                }

                return new FlashResult { Success = false, Summary = "Get property is only supported via adb or fastboot. 属性查询仅支持 ADB 或 fastboot 模式。" };
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
    /// <para>枚举 fastboot 设备上报的分区名：解析 <c>getvar:all</c> 中 <c>partition-type:</c> /
    /// <c>partition-size:</c> 前缀的变量键，去重后返回。</para>
    /// Enumerates the partition names reported by a fastboot device: parses the
    /// <c>partition-type:</c> / <c>partition-size:</c> prefixed variable keys from
    /// <c>getvar:all</c>, deduplicated.
    /// </summary>
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

        return names.ToList();
    }

    /// <summary>
    /// <para>Fastboot 路径：经 <see cref="FastbootFactory"/> 打开会话并执行
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>，逐块上报进度。</para>
    /// Fastboot path: opens a session via <see cref="FastbootFactory"/> and executes
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>, reporting progress per chunk.
    /// </summary>
    private static async Task<FlashResult> FlashFastbootAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening fastboot session... 正在打开 fastboot 会话...");
        return await Task.Run(() =>
        {
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
            if (!session.IsConnected)
            {
                throw new InvalidOperationException("Fastboot session is not connected. fastboot 会话未连接。");
            }

            TryBackupBeforeFlash(session, partition, progress, ct);

            var summary = FlashImageChunked(session, partition, imagePath, progress, ct);
            Report(progress, partition, 100, "Flash complete. 刷写完成。");
            return new FlashResult { Success = true, Summary = summary };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>按 fastboot 的 max-download-size 刷写镜像：不超过上限时单次 download+flash（稀疏镜像
    /// 原样交由 bootloader 解析）；超过上限时把镜像转为/拆分为若干不超过上限的稀疏分片并顺序刷写
    /// （bootloader 在当前偏移处追加写入，与 AOSP fastboot 行为一致）。分片临时文件用后即删。</para>
    /// Flashes an image honoring the fastboot max-download-size: images within the limit are sent in a
    /// single download+flash (sparse payloads are passed through for the bootloader to decode); larger
    /// images are converted to/split into sparse chunks no larger than the limit and flashed sequentially
    /// (the bootloader appends each chunk at the current offset, matching AOSP fastboot behavior).
    /// Chunk temp files are deleted afterwards.
    /// </summary>
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
            return fs.Read(header) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) == UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseFormat.SparseHeaderMagic;
        }

        if (imageLength <= maxDownloadSize)
        {
            using var image = File.OpenRead(imagePath);
            Report(progress, partition, 5, "Uploading image... 正在上传镜像...");
            var byteProgress = new Progress<long>(written =>
                Report(progress, partition, imageLength > 0 ? (int)Math.Min(100, written * 100L / imageLength) : 0, "Uploading image... 正在上传镜像..."));
            var response = session.FlashPartition(partition, image, imageLength, byteProgress, ct);
            response.ThrowIfFailed();
            return $"Flashed {partition} ({imageLength} bytes) via fastboot.";
        }

        var tempDir = Directory.CreateTempSubdirectory("uotan-flash-").FullName;
        try
        {
            // 原始镜像先转稀疏；已是稀疏则直接拆分。
            // Convert a raw image to sparse first; already-sparse images are split directly.
            var sparsePath = IsSparseImage(imagePath)
                ? imagePath
                : Path.Combine(tempDir, "source.sparse");
            if (!string.Equals(sparsePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, partition, 2, "Converting to sparse... 正在转换为稀疏格式...");
                UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseImageConverter.ConvertRawToSparse(imagePath, sparsePath);
            }

            Report(progress, partition, 4, "Splitting sparse image... 正在拆分稀疏镜像...");
            UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseImageConverter.ResparseImage(
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
                var chunkResponse = session.FlashPartition(partition, chunk, chunkLength, chunkProgress, ct);
                chunkResponse.ThrowIfFailed();
                bytesDone += chunkLength;
            }

            return $"Flashed {partition} ({totalBytes} bytes in {chunks.Length} sparse chunk(s)) via fastboot.";
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败不阻断刷写结果 */ }
        }
    }

    /// <summary>
    /// <para>若设置「刷写前自动备份」已启用，在刷写前把 fastboot 目标分区备份到
    /// 工程会话目录 <c>.uotan/backup/</c>（未打开工程时退化到临时目录）。
    /// 备份进度并入现有刷写进度通道；备份失败仅记录警告日志，绝不中断刷写。</para>
    /// If the "auto-backup before flash" setting is enabled, backs up the fastboot target
    /// partition to the solution session directory <c>.uotan/backup/</c> before flashing
    /// (falls back to the temp directory when no solution is open). Backup progress is
    /// merged into the existing flash progress channel; a failed backup only logs a warning
    /// and never aborts the flash.
    /// </summary>
    private static void TryBackupBeforeFlash(
        FastbootSession session, string partition,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        try
        {
            if (!SettingsPersistenceService.Instance.LoadSettings().AutoBackupBeforeFlash)
            {
                return;
            }

            var stateDir = ProjectContextService.Instance.StateDirectory;
            var backupDir = Path.Combine(stateDir ?? Path.GetTempPath(), "backup");
            Directory.CreateDirectory(backupDir);

            var invalid = Path.GetInvalidFileNameChars();
            var safePartition = partition.IndexOfAny(invalid) < 0
                ? partition
                : string.Concat(partition.Select(c => invalid.Contains(c) ? '_' : c));
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
    /// <para>EDL 路径：解析镜像目录中的 rawprogram*.xml，按分区匹配执行单分区直刷；
    /// 分区未命中时返回失败（软件未发布，无旧整包回退兼容需求）。无
    /// rawprogram 时给出清晰错误。</para>
    /// EDL path: parses the rawprogram*.xml in the image directory and performs a
    /// single-partition flash when the partition matches; an unmatched partition returns a
    /// failure result (no legacy full-bundle fallback is kept for unreleased builds).
    /// A clear error is raised when no rawprogram is present.
    /// </summary>
    private static async Task<FlashResult> FlashEdlAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Looking for EDL bundle... 正在查找 EDL 刷写包...");
        var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? string.Empty;
        var rawProgramPath = Directory.EnumerateFiles(imageDir, "rawprogram*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (rawProgramPath is null)
        {
            throw new InvalidOperationException(
                "No rawprogram*.xml found in the image directory; EDL flashing requires a rawprogram (single-partition or full bundle). 镜像目录中未找到 rawprogram*.xml。");
        }

        var entries = RawProgramParser.ParseAll([rawProgramPath], imageDir);
        var target = FindEdlTarget(entries, partition, imagePath);
        if (target is null)
        {
            Report(progress, partition, 0,
                $"Partition '{partition}' not found in rawprogram. 分区 '{partition}' 未在 rawprogram 中匹配到。");
            return new FlashResult { Success = false, Summary = $"Partition '{partition}' not found in rawprogram. 分区 '{partition}' 未在 rawprogram 中匹配到。" };
        }

        var session = DeviceMonitorService.Instance.OpenEdlSession(device.Device)
            ?? throw new InvalidOperationException("No EDL session available for the selected device. 无法为所选设备建立 EDL 会话。");
        using var edl = session;
        return await Default.FlashEdlPartitionAsync(edl, target, imageDir, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>EDL 单分区直刷：确保会话初始化后把单个 rawprogram 条目经 Firehose program
    /// 单发刷写。</para>
    /// EDL single-partition flash: initializes the session and flashes a single rawprogram
    /// entry with one Firehose program.
    /// </summary>
    /// <param name="session"><para>已（或尚未）初始化的 EDL 会话。</para>The EDL session, initialized on demand.</param>
    /// <param name="entry"><para>目标 rawprogram 条目。</para>The target rawprogram entry.</param>
    /// <param name="imageDir"><para>镜像文件所在目录。</para>The image file directory.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.EnsureInitializedAsync(cancellationToken: ct).ConfigureAwait(false);
        var firehose = session.Firehose
            ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
        return await FlashEdlPartitionAsync(firehose, entry, imageDir, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>EDL 单分区直刷核心：直接对 <see cref="FirehoseProtocol"/> 单发 program 命令
    /// （跳过会话/Sahara，供测试与高级调用）。</para>
    /// EDL single-partition flash core: issues a single Firehose program directly against
    /// a <see cref="FirehoseProtocol"/> (bypassing the session/Sahara, for tests and advanced
    /// callers).
    /// </summary>
    /// <param name="firehose"><para>已配置的 Firehose 协议实例。</para>A configured Firehose protocol instance.</param>
    /// <param name="entry"><para>目标 rawprogram 条目。</para>The target rawprogram entry.</param>
    /// <param name="imageDir"><para>镜像文件所在目录。</para>The image file directory.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        FirehoseProtocol firehose, RawProgramEntry entry, string imageDir,
        IProgress<FlashProgress>? progress, CancellationToken ct)
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

        if (entry.SectorSizeInBytes > 0)
        {
            firehose.Configuration.SectorSize = entry.SectorSizeInBytes;
        }

        var sectorSize = firehose.Configuration.SectorSize > 0 ? firehose.Configuration.SectorSize : 4096;
        var startSector = ParseStartSectorValue(entry.StartSector);

        Report(progress, displayName, 30, $"Sending program for {displayName}... 正在发送 program 命令...");
        using var stream = File.OpenRead(sourceFile);
        if (entry.IsSparse || IsAndroidSparseImage(stream))
        {
            stream.Position = 0;
            var response = await firehose.ProgramSparseAsync(
                stream, startSector, entry.Filename, entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new InvalidOperationException($"EDL sparse program failed for '{displayName}'. Response: {response.RawValue}");
            }
        }
        else
        {
            if (entry.FileSectorOffset > 0)
            {
                stream.Position = Math.Min(entry.FileSectorOffset * sectorSize, stream.Length);
            }

            var lengthFromOffset = stream.Length - stream.Position;
            var numSectors = entry.NumPartitionSectors > 0
                ? entry.NumPartitionSectors
                : (lengthFromOffset / sectorSize) + (lengthFromOffset % sectorSize != 0 ? 1 : 0);
            if (numSectors <= 0)
            {
                throw new InvalidOperationException($"Image for '{displayName}' is empty; nothing to program. 镜像为空。");
            }

            var response = await firehose.ProgramAsync(
                stream, startSector, numSectors, entry.Filename, entry.PhysicalPartitionNumber, ct).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new InvalidOperationException($"EDL program failed for '{displayName}'. Response: {response.RawValue}");
            }
        }

        Report(progress, displayName, 100, $"EDL partition '{displayName}' flash complete. EDL 单分区刷写完成。");
        return new FlashResult
        {
            Success = true,
            Summary = $"EDL partition '{displayName}' flashed via Firehose program."
        };
    }

    /// <summary>
    /// <para>在 rawprogram 条目列表中按 label/filename 匹配目标分区。</para>
    /// Matches the target partition in a list of rawprogram entries by label/filename.
    /// </summary>
    /// <param name="entries"><para>rawprogram 条目列表。</para>The rawprogram entries.</param>
    /// <param name="partition"><para>目标分区名（如 boot）。</para>Target partition name (e.g. boot).</param>
    /// <param name="imagePath"><para>可选镜像路径；其文件名也参与匹配。</para>Optional image path; its filename also participates in matching.</param>
    /// <returns>命中的条目；未命中返回 <c>null</c>。 The matching entry, or <c>null</c>.</returns>
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
    /// <para>RKDL 路径：打开 RkdlSession，可选下载 loader（maskrom 模式必需），解析
    /// Rockchip parameter 分区表，将镜像写入目标分区 LBA（核心写入见
    /// <see cref="FlashRkdlPartition"/>）。</para>
    /// RKDL path: opens an RkdlSession, optionally downloads the loader (required in
    /// maskrom mode), parses the Rockchip parameter partition table and writes the image to
    /// the target partition LBA (core write in <see cref="FlashRkdlPartition"/>).
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名（如 boot）。</para>Target partition name (e.g. boot).</param>
    /// <param name="imagePath"><para>镜像文件完整路径（loader 与 parameter 在镜像目录中自动发现）。</para>
    /// Full path of the image file (loader and parameter are auto-discovered in its directory).</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
    public static async Task<FlashResult> FlashRkdlAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashRkdlAsync(device, partition, imagePath, loaderPath: null, progress, ct).ConfigureAwait(false);

    /// <summary>
    /// <para>RKDL 路径（显式 loader 路径）：行为与无 loader 参数的重载一致，但 loader 路径
    /// 由调用方显式指定（为 null 时在镜像目录自动发现）。</para>
    /// RKDL path with an explicit loader path: behaves like the overload without a loader
    /// argument, but the loader path is supplied by the caller (auto-discovered when null).
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名。</para>Target partition name.</param>
    /// <param name="imagePath"><para>镜像文件完整路径。</para>Full path of the image file.</param>
    /// <param name="loaderPath"><para>可选 Rockchip loader（MiniLoaderAll / *loader*.bin）；为 null 时在镜像目录自动发现。</para>
    /// Optional Rockchip loader (MiniLoaderAll / *loader*.bin); auto-discovered in the image directory when null.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
    public static async Task<FlashResult> FlashRkdlAsync(
        DeviceInfo device, string partition, string imagePath, string? loaderPath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening RKDL session... 正在打开 RKDL 会话...");
        var session = DeviceMonitorService.Instance.OpenRkdlSession(device.Device)
            ?? throw new InvalidOperationException("No RKDL session available for the selected device. 无法为所选设备建立 RKDL 会话。");
        using var rkdl = session;

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
        return await Task.Run(() => FlashRkdlPartition(rkdl, target, imagePath, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>RKDL 单分区写入核心：用 <see cref="RkdlSession.WriteLBA"/> 把镜像写入分区 LBA。</para>
    /// RKDL single-partition write core: writes the image to the partition LBA with
    /// <see cref="RkdlSession.WriteLBA"/>.
    /// </summary>
    /// <param name="session"><para>已打开的 RKDL 会话。</para>An open RKDL session.</param>
    /// <param name="partitionInfo"><para>目标分区信息（来自 Rockchip parameter）。</para>
    /// The target partition info (from the Rockchip parameter).</param>
    /// <param name="imagePath"><para>镜像文件完整路径。</para>Full path of the image file.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
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
    /// <para>ADB 路径：recovery sideload（.zip OTA / 显式 sideload 目标）或在线安装
    /// （push 到 /data/local/tmp + pm install / apply update）。</para>
    /// ADB path: recovery sideload (for .zip OTA or explicit "sideload" targets) or online
    /// install (push to /data/local/tmp + pm install / apply update).
    /// </summary>
    private static async Task<FlashResult> FlashAdbAsync(
        DeviceInfo device, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        Report(progress, partition, 0, "Opening ADB session... 正在打开 ADB 会话...");
        var session = DeviceMonitorService.Instance.OpenAdbSession(device.Device)
            ?? throw new InvalidOperationException("No ADB session available for the selected device. 无法为所选设备建立 ADB 会话。");
        using var adb = session;
        adb.Connect();
        if (!adb.IsConnected)
        {
            throw new InvalidOperationException("ADB session is not connected. ADB 会话未连接。");
        }

        if (IsSideloadTarget(partition, imagePath))
        {
            Report(progress, partition, 5, "Rebooting into sideload mode... 正在重启进入 sideload 模式...");
            RebootToSideload(adb);
            await WaitForSideloadReconnectAsync(adb, ct).ConfigureAwait(false);
            await Task.Run(() => PushSideloadFile(adb, imagePath, progress, ct), ct).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => InstallOnline(adb, imagePath, progress, ct), ct).ConfigureAwait(false);
        }

        Report(progress, partition, 100, "ADB flash complete. ADB 刷写完成。");
        return new FlashResult
        {
            Success = true,
            Summary = $"Flashed {Path.GetFileName(imagePath)} via ADB."
        };
    }

    /// <summary>
    /// <para>判断目标是否为 recovery sideload（显式分区名 "sideload" 或 .zip OTA 镜像）。</para>
    /// Determines whether the target is a recovery sideload (explicit "sideload" partition
    /// or a .zip OTA image).
    /// </summary>
    public static bool IsSideloadTarget(string partition, string imagePath)
        => string.Equals(partition, "sideload", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetExtension(imagePath), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>通过 ADB 设备服务重启设备进入 sideload 模式（<c>reboot:sideload</c>）。</para>
    /// Reboots the device into sideload mode via the ADB device services
    /// (<c>reboot:sideload</c>).
    /// </summary>
    /// <param name="session"><para>已连接的 ADB 会话。</para>A connected ADB session.</param>
    public static void RebootToSideload(AdbSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.CreateDeviceServices().Reboot("sideload");
    }

    /// <summary>
    /// <para>流式推送 OTA 到 sideload 设备节点（<c>/sideload/package.zip</c>）。</para>
    /// Streams the OTA to the sideload device node (<c>/sideload/package.zip</c>).
    /// </summary>
    /// <param name="session"><para>已连接到 sideload 模式的 ADB 会话。</para>An ADB session connected to sideload mode.</param>
    /// <param name="imagePath"><para>OTA 镜像路径。</para>The OTA image path.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    public static void PushSideloadFile(AdbSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"OTA image not found: {imagePath} OTA 镜像不存在。", imagePath);
        }

        using var sync = session.CreateSyncClient();
        using var fs = File.OpenRead(imagePath);
        var totalBytes = fs.Length;
        using var reporting = new ProgressReportingStream(fs, totalBytes, written =>
            Report(progress, "sideload", totalBytes > 0 ? (int)Math.Min(100, written * 100L / totalBytes) : 0, "Streaming OTA to /sideload/package.zip... 正在推送 OTA..."));
        sync.PushStream(reporting, SideloadRemotePath);
    }

    /// <summary>
    /// <para>在线安装：push 到 <c>/data/local/tmp</c>，APK 走 <c>pm install -r</c>，其余
    /// （如 OTA zip）尝试 <c>apply update</c>（部分 recovery 暴露该命令）。</para>
    /// Online install: pushes to <c>/data/local/tmp</c> and runs <c>pm install -r</c> for
    /// APKs; other images (e.g. OTA zips) try <c>apply update</c> (exposed by some
    /// recoveries).
    /// </summary>
    /// <param name="session"><para>已连接的 ADB 会话。</para>A connected ADB session.</param>
    /// <param name="imagePath"><para>待安装镜像路径。</para>The image path to install.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    public static void InstallOnline(AdbSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
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
        using (var sync = session.CreateSyncClient())
        {
            sync.Push(imagePath, remotePath);
        }

        var isApk = string.Equals(Path.GetExtension(fileName), ".apk", StringComparison.OrdinalIgnoreCase);
        var command = isApk
            ? $"pm install -r \"{remotePath}\""
            : $"apply update \"{remotePath}\"";
        Report(progress, "adb", 70, $"Running '{command}'... 正在执行安装命令...");
        var result = session.ExecuteCommand(command);
        if (result.ExitCode != 0)
        {
            var stderr = Encoding.UTF8.GetString(result.Stderr).Trim();
            throw new InvalidOperationException(
                $"ADB install command failed (exit {result.ExitCode}): {stderr} ADB 安装命令失败。");
        }
    }

    /// <summary>
    /// <para>等待设备在 sideload 模式下重新出现并重新连接（最多约 30 秒）。</para>
    /// Waits for the device to reappear in sideload mode and reconnect (up to ~30 s).
    /// </summary>
    private static async Task WaitForSideloadReconnectAsync(AdbSession session, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await session.ConnectAsync(ct).ConfigureAwait(false);
                if (session.IsConnected && session.Connection.PeerVersion > 0)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Device is still rebooting; keep retrying.
                // 设备仍在重启，继续重试。
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Device did not reappear in sideload mode; enter recovery sideload manually and retry. 设备未在 sideload 模式下重新出现。");
    }

    /// <summary>
    /// <para>按分区记录的上次进度上报，用于 100ms 节流去重。</para>
    /// Last reported progress per partition, used for 100 ms throttling.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Percent, string Status, long Ticks)> LastReportByPartition = new();

    /// <summary>
    /// <para>上报一条进度：既写入调用方提供的 <see cref="IProgress{T}"/>，
    /// 也发送 <see cref="FlashProgressMessage"/> 供输出面板 Flash 通道展示。
    /// 相同状态在 100ms 内重复上报会被合并（100% 终值必达），避免 USB 传输层
    /// 每次写块都触发消息广播风暴。</para>
    /// Reports a progress entry: writes to the caller-provided <see cref="IProgress{T}"/>
    /// and sends a <see cref="FlashProgressMessage"/> for the output panel's Flash channel.
    /// Duplicate reports with the same status within 100 ms are coalesced (the final 100%
    /// always gets through) to avoid a message storm on every USB write chunk.
    /// </summary>
    private static void Report(IProgress<FlashProgress>? progress, string? partition, int percent, string status)
    {
        var key = partition ?? string.Empty;
        var now = Environment.TickCount64;
        if (LastReportByPartition.TryGetValue(key, out var last)
            && percent != 100
            && string.Equals(status, last.Status, StringComparison.Ordinal)
            && now - last.Ticks < 100)
        {
            return;
        }
        LastReportByPartition[key] = (percent, status, now);
        progress?.Report(new FlashProgress(partition, percent, status));
        WeakReferenceMessenger.Default.Send(new FlashProgressMessage(partition, percent, status));
    }

    /// <summary>
    /// <para>解析 rawprogram 条目的源文件路径。</para>
    /// Resolves the source image path of a rawprogram entry.
    /// </summary>
    private static string ResolveEntrySource(RawProgramEntry entry, string imageDir)
    {
        return !string.IsNullOrEmpty(entry.SourceFile) && File.Exists(entry.SourceFile)
            ? entry.SourceFile
            : string.IsNullOrEmpty(entry.Filename)
            ? string.Empty
            : Path.Combine(imageDir, entry.Filename);
    }

    /// <summary>
    /// <para>解析 rawprogram 的 start_sector（支持十进制与 0x 十六进制）。</para>
    /// Parses a rawprogram start_sector (decimal or 0x-hex).
    /// </summary>
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
    /// <para>检测 Android sparse 镜像魔数（0xED26FF3A）。</para>
    /// Detects the Android sparse image magic (0xED26FF3A).
    /// </summary>
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
    /// <para>在镜像目录中查找 Rockchip loader 文件。</para>
    /// Locates a Rockchip loader file in the image directory.
    /// </summary>
    private static string? ResolveLoaderPath(string? loaderPath, string imageDir)
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
    /// <para>在镜像目录中查找 Rockchip parameter 文件（parameter / parameter.txt）。</para>
    /// Locates a Rockchip parameter file (parameter / parameter.txt) in the image directory.
    /// </summary>
    private static string? FindParameterFile(string imageDir)
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
    /// <para>sideload 模式下的远端推送目标。</para>
    /// Remote push target in sideload mode.
    /// </summary>
    private const string SideloadRemotePath = "/sideload/package.zip";

    /// <summary>
    /// <para>包装源流，按读取字节数上报进度。</para>
    /// Wraps a source stream, reporting progress by bytes read.
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
}

/// <summary>
/// <para>刷写操作结果。</para>
/// Flash operation result.
/// </summary>
public sealed class FlashResult
{
    /// <summary>
    /// <para>获取或设置刷写是否成功。</para>
    /// Gets or sets whether the flash succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// <para>获取或设置结果摘要（成功时的描述或失败原因）。</para>
    /// Gets or sets the result summary (description on success, or the failure reason).
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// <para>刷写进度通知，与 <see cref="FlashProgressMessage"/> 字段对齐。</para>
/// Flash progress notification, aligned with <see cref="FlashProgressMessage"/> fields.
/// </summary>
/// <remarks>
/// <para>初始化刷写进度。</para>
/// Initializes the flash progress.
/// </remarks>
public sealed class FlashProgress(string? partition, int percent, string status)
{
    /// <summary>
    /// <para>正在刷写的分区；非分区级时为 null。</para>
    /// Gets the partition being flashed; null when not partition-specific.
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