using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Contracts;
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
    /// <para>擦除单个分区（仅 fastboot 协议）：经 <see cref="DeviceMonitorService"/> 打开会话并执行
    /// <c>erase &lt;partition&gt;</c>。非 fastboot 设备返回失败结果。</para>
    /// Erases a single partition (fastboot protocol only): opens a session via
    /// <see cref="DeviceMonitorService"/> and executes <c>erase &lt;partition&gt;</c>. Non-fastboot
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
            return await App.DeviceMonitor.RunAdbAsync(device.Device, async adb =>
            {
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
    /// <para>查询设备属性：fastboot 走 <c>getvar &lt;name&gt;</c>（<see cref="FastbootSession.GetVariable"/>），
    /// ADB 走 <c>getprop &lt;name&gt;</c>（<see cref="IAdbDeviceSession.CreateDeviceServices"/> 的 GetProp），
    /// 结果以 <c>name=value</c> 形式放入 <see cref="FlashResult.Summary"/>。</para>
    /// Queries a device property: <c>getvar &lt;name&gt;</c> (<see cref="FastbootSession.GetVariable"/>)
    /// on fastboot, <c>getprop &lt;name&gt;</c> (GetProp on <see cref="IAdbDeviceSession.CreateDeviceServices"/>)
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
    /// <para>Fastboot 路径：经 <see cref="DeviceMonitorService"/> 打开会话并执行
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>，逐块上报进度。</para>
    /// Fastboot path: opens a session via <see cref="DeviceMonitorService"/> and executes
    /// <c>flash &lt;partition&gt; &lt;image&gt;</c>, reporting progress per chunk.
    /// </summary>
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

            // fastbootd 会拒绝大于当前逻辑分区的镜像（FAIL EOVERFLOW "Value too large for defined
            // data type"），因此必须先按 AOSP fastboot do_flash 语义把分区调整到镜像尺寸再刷写。
            // fastbootd rejects an image larger than the current logical partition (FAIL EOVERFLOW),
            // so the partition is resized to the image size first, following AOSP fastboot do_flash.
            var target = PrepareFlashTarget(session, partition, GetImageLogicalSize(imagePath), progress);
            var summary = FlashImageChunked(session, target, imagePath, progress, ct);
            Report(progress, target, 100, "Flash complete. 刷写完成。");
            return new FlashResult { Success = true, Summary = summary };
        }, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>解析刷写目标分区（A/B 槽位）并按 AOSP fastboot <c>do_flash</c> 语义准备逻辑分区：
    /// 目标为逻辑分区时先发送 <c>resize-logical-partition:&lt;分区&gt;:&lt;镜像逻辑尺寸&gt;</c>，
    /// 使 fastbootd 的 <c>flash</c> 接受大于当前分区的镜像，而非返回 EOVERFLOW
    /// （"Value too large for defined data type"）或越界写入失败。非逻辑分区和设备不支持
    /// <c>is-logical</c> 时不做任何调整。</para>
    /// Resolves the flash target (A/B slot) and prepares a logical partition following AOSP fastboot
    /// <c>do_flash</c>: a logical partition is resized to the image's logical size with
    /// <c>resize-logical-partition:&lt;partition&gt;:&lt;size&gt;</c> beforehand, so fastbootd's
    /// <c>flash</c> accepts an oversized image instead of rejecting it with EOVERFLOW
    /// ("Value too large for defined data type") or failing past the mapped range. Physical
    /// partitions and devices without <c>is-logical</c> support are left untouched.
    /// </summary>
    /// <param name="session"><para>已连接的 fastboot 会话。</para>A connected fastboot session.</param>
    /// <param name="partition"><para>目标分区名（可能尚未带槽位后缀）。</para>The target partition name (may lack a slot suffix).</param>
    /// <param name="imageSize"><para>镜像的逻辑尺寸（字节，由 <see cref="GetImageLogicalSize"/> 得到）。</para>
    /// The image's logical size in bytes, as returned by <see cref="GetImageLogicalSize"/>.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <returns><para>后续 <c>flash:</c> 应使用的槽位解析后分区名。</para>The slot-resolved name to use for the following <c>flash:</c>.</returns>
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
            // AOSP fastboot ignores a failed resize and still flashes: a partition that is already
            // large enough can be written without resizing.
            // AOSP fastboot 忽略调整失败并继续刷写：分区已足够大时无需调整也可写入。
            LogService.Instance.Write(LogLevel.Warn, "FlashService",
                $"Resize-logical-partition {target} to {imageSize} failed: {response.Info}; continuing flash. 逻辑分区大小调整失败，继续刷写。");
        }

        return target;
    }

    /// <summary>
    /// <para>获取镜像的逻辑尺寸：稀疏镜像取头部声明的展开大小（<c>total_blks × blk_sz</c>），
    /// 原始镜像取文件长度。设备侧校验的是展开后的大小（fastbootd 用下载数据长度对比分区大小），
    /// 因此逻辑分区调整必须按展开尺寸而不是稀疏文件体积进行。</para>
    /// Gets the image's logical size: the expanded size declared by a sparse header
    /// (<c>total_blks × blk_sz</c>) or the file length of a raw image. The device compares its
    /// partition size against the expanded payload, so a logical partition must be resized to the
    /// expanded size rather than the smaller sparse file size.
    /// </summary>
    /// <param name="imagePath"><para>镜像文件完整路径。</para>Full path of the image file.</param>
    /// <returns><para>镜像逻辑尺寸（字节）。</para>The image's logical size in bytes.</returns>
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
            // 文件短于稀疏头（或读取失败）：按原始镜像处理。
            // Shorter than a sparse header (or unreadable): treated as a raw image.
        }

        return new FileInfo(imagePath).Length;
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
    /// <remarks>
    /// <paramref name="partition"/> must already be slot-resolved (see
    /// <see cref="PrepareFlashTarget"/>), because every piece is flashed with
    /// <c>resolveSlot: false</c>.
    /// <para><paramref name="partition"/> 必须已由 <see cref="PrepareFlashTarget"/> 完成槽位解析，
    /// 因为每个分片都以 <c>resolveSlot: false</c> 刷写。</para>
    /// </remarks>
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
            var response = session.FlashPartition(partition, image, imageLength, byteProgress, ct, resolveSlot: false);
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
                var chunkResponse = session.FlashPartition(partition, chunk, chunkLength, chunkProgress, ct, resolveSlot: false);
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
    /// 子项目状态目录 <c>.uotan/backup/</c>（未打开工程时退化到临时目录）。
    /// 备份进度并入现有刷写进度通道；备份失败仅记录警告日志，绝不中断刷写。</para>
    /// If the "auto-backup before flash" setting is enabled, backs up the fastboot target
    /// partition to the captured sub-project state directory <c>.uotan/backup/</c> before flashing
    /// (falls back to the temp directory when no solution is open). Backup progress is
    /// merged into the existing flash progress channel; a failed backup only logs a warning
    /// and never aborts the flash.
    /// </summary>
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

        return await App.DeviceMonitor.RunEdlAsync(device.Device,
            edl => Default.FlashEdlPartitionAsync(edl, target, imageDir, progress, ct), ct).ConfigureAwait(false);
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
        => await FlashEdlPartitionAsync(session, entry, imageDir, null, progress, ct).ConfigureAwait(false);

    /// <summary>Uploads a package-supplied Firehose programmer before programming a manifest entry.</summary>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir, string? programmerPath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
        => await FlashEdlPartitionAsync(session, entry, imageDir, programmerPath, progress, ct, verify: false).ConfigureAwait(false);

    /// <summary>
    /// <para>上传包内 Firehose programmer（可选）后编程清单条目，并可在写入成功后执行回读校验。</para>
    /// Uploads a package-supplied Firehose programmer before programming a manifest entry, with an
    /// optional read-back verification after a successful write.
    /// </summary>
    public async Task<FlashResult> FlashEdlPartitionAsync(
        EdlSession session, RawProgramEntry entry, string imageDir, string? programmerPath,
        IProgress<FlashProgress>? progress, CancellationToken ct, bool verify)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (programmerPath is not null && !File.Exists(programmerPath))
        {
            throw new FileNotFoundException("Firehose programmer is missing.", programmerPath);
        }
        using var programmer = programmerPath is null ? null : File.OpenRead(programmerPath);
        await session.EnsureInitializedAsync(programmer, ct).ConfigureAwait(false);
        var firehose = session.Firehose
            ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
        return await FlashEdlPartitionAsync(firehose, entry, imageDir, progress, ct, verify).ConfigureAwait(false);
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
        => await FlashEdlPartitionAsync(firehose, entry, imageDir, progress, ct, verify: false).ConfigureAwait(false);

    /// <summary>
    /// <para>EDL 单分区直刷核心（带回读校验门控）：直接对 <see cref="FirehoseProtocol"/> 单发 program 命令
    /// （跳过会话/Sahara，供测试与高级调用）。verify 为 true 且写入走整扇区原始数据路径、比对基准与源文件
    /// 逐字节一致时，用 <see cref="FirehoseProtocol.VerifyProgramAsync"/> 回读校验；稀疏编程、非零
    /// file_sector_offset 或镜像长于编程范围时安全跳过并在 Summary 说明。</para>
    /// EDL single-partition flash core with an optional read-back gate: issues a single Firehose program
    /// directly against a <see cref="FirehoseProtocol"/> (bypassing the session/Sahara, for tests and
    /// advanced callers). When <paramref name="verify"/> is set and the write used the whole-sector raw
    /// path whose byte-for-byte source comparison is well defined, the programmed range is read back with
    /// <see cref="FirehoseProtocol.VerifyProgramAsync"/>; sparse programming, a non-zero file sector offset
    /// or an image longer than the programmed range skip verification with a note in the summary.
    /// </summary>
    /// <param name="firehose"><para>已配置的 Firehose 协议实例。</para>A configured Firehose protocol instance.</param>
    /// <param name="entry"><para>目标 rawprogram 条目。</para>The target rawprogram entry.</param>
    /// <param name="imageDir"><para>镜像文件所在目录。</para>The image file directory.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <param name="verify"><para>写入成功后是否把该扇区范围回读并与源文件逐字节比对。</para>
    /// Whether to read the programmed range back after a successful write and compare it with the source file.</param>
    /// <returns>刷写结果。</returns>
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

        // 回读校验只在整扇区原始写入且比对基准可从源文件首字节对齐时可行；其余路径记录跳过原因。
        // Read-back verification is only defined for a whole-sector raw write whose source comparison
        // starts at the file's first byte; other paths record a skip reason instead.
        long? verifyNumSectors = null;
        long verifyStartSector = 0;
        string? verifySkipReason = null;

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
                throw new InvalidOperationException($"EDL sparse program failed for '{displayName}'. Response: {response.RawValue}");
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
            var numSectors = entry.NumPartitionSectors > 0
                ? entry.NumPartitionSectors
                : (lengthFromOffset / sectorSize) + (lengthFromOffset % sectorSize != 0 ? 1 : 0);
            if (numSectors <= 0)
            {
                throw new InvalidOperationException($"Image for '{displayName}' is empty; nothing to program. 镜像为空。");
            }

            if (checked(numSectors * sectorSize) > lengthFromOffset)
            {
                throw new InvalidDataException("The image is shorter than the requested whole-sector range.");
            }

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
                throw new InvalidOperationException($"EDL program failed for '{displayName}'. Response: {response.RawValue}");
            }

            if (sourceOffset != 0)
            {
                verifySkipReason = "the entry programs from a non-zero file_sector_offset, so the device range does not start at the source file's first byte";
            }
            else if (checked(numSectors * sectorSize) != stream.Length)
            {
                verifySkipReason = "the image is longer than the programmed whole-sector range";
            }
            else
            {
                verifyNumSectors = numSectors;
                verifyStartSector = resolvedStart;
            }
        }

        var summary = $"EDL partition '{displayName}' flashed via Firehose program.";
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
    /// <param name="verify"><para>写入成功后是否把该分区区间回读并与源镜像逐字节比对。</para>
    /// Whether to read the written range back from the device after a successful write and compare it
    /// byte for byte with the source image.</param>
    /// <returns>刷写结果。</returns>
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
        return await App.DeviceMonitor.RunAdbAsync(device.Device, async adb =>
        {
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
        }, ct).ConfigureAwait(false);
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
    public static void RebootToSideload(IAdbDeviceSession session)
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
    public static void PushSideloadFile(IAdbDeviceSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
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
    public static void InstallOnline(IAdbDeviceSession session, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
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
    private static readonly PartitionProgressThrottle ProgressThrottle = new();

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
        if (!ProgressThrottle.ShouldReport(partition, percent, status, Environment.TickCount64)) return;
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
    /// <para>在镜像目录中查找 Rockchip parameter 文件（parameter / parameter.txt）。</para>
    /// Locates a Rockchip parameter file (parameter / parameter.txt) in the image directory.
    /// </summary>
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
    /// <para>判断目录中是否存在 Rockchip parameter 分区表文件；目录不存在时为 false，不抛异常。</para>
    /// Determines whether the directory contains a Rockchip parameter partition-table file;
    /// returns false for a missing directory without throwing.
    /// </summary>
    /// <param name="imageDir"><para>待检查的目录。</para>The directory to inspect.</param>
    /// <returns><para>存在 parameter 文件时为 true。</para>True when a Rockchip parameter file is present.</returns>
    internal static bool HasRockchipParameter(string imageDir)
        => FindParameterFile(imageDir) is not null;

    /// <summary>
    /// <para>把设备回读流与源文件逐字节比对：64 KiB 有界缓冲，源文件短于比对长度或任一块不一致
    /// 时提前返回 false，禁止整读入内存。</para>
    /// Compares a device read-back stream byte for byte against the source file using a bounded
    /// 64 KiB buffer, returning false early when the source is shorter than the compared length or
    /// any block differs; the payload is never buffered as a whole.
    /// </summary>
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

    /// <summary>
    /// <para>把 <see cref="RkdlSession.ReadLBA"/> 的区间读取包装为拉取式只读流：每次以有界的
    /// 64 KiB 整扇区块向设备取数，避免整段回读进入内存。</para>
    /// Wraps a range of <see cref="RkdlSession.ReadLBA"/> as a pull-based read-only stream, fetching
    /// device data in bounded 64 KiB whole-sector blocks so a full read-back never lands in memory.
    /// </summary>
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

                // 除末段外每次取整 64 KiB（扇区大小的倍数），保证下一段的起始 LBA 仍落在扇区边界上。
                // Every block but the tail is a whole 64 KiB (a sector-size multiple), keeping each
                // follow-up LBA on a sector boundary.
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