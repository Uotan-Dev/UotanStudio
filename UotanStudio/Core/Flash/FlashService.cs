using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Core.Flash;

/// <summary>
/// <para>真实刷写服务：通过 UotanStudio.Comm 协议层把镜像刷写到设备分区，
/// 统一封装 Fastboot 与 EDL 两条路径并回报进度。禁止在此直接操作 USB 细节。</para>
/// Real flashing service: writes an image to a device partition through the
/// UotanStudio.Comm protocol layer, unifying the Fastboot and EDL paths with progress
/// reporting. USB details must never be touched here directly.
/// </summary>
public static class FlashService
{
    /// <summary>
    /// <para>把指定镜像刷写到设备分区；异常被捕获并转为失败结果。</para>
    /// Flashes the specified image to a device partition; exceptions are captured and
    /// converted into a failure result.
    /// </summary>
    /// <param name="device"><para>目标设备。</para>The target device.</param>
    /// <param name="partition"><para>目标分区名（如 boot）。</para>Target partition name (e.g. boot).</param>
    /// <param name="imagePath"><para>镜像文件完整路径。</para>Full path of the image file.</param>
    /// <param name="progress"><para>可选的刷写进度报告器。</para>Optional flash progress reporter.</param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>刷写结果。</returns>
    public static async Task<FlashResult> FlashImageAsync(
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

            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Image file not found: {imagePath}", imagePath);
            }

            return device.Protocol switch
            {
                DeviceProtocolKind.Fastboot => await FlashFastbootAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
                DeviceProtocolKind.Edl => await FlashEdlAsync(device, partition, imagePath, progress, ct).ConfigureAwait(false),
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

            using var image = File.OpenRead(imagePath);
            var totalBytes = image.Length;
            Report(progress, partition, 5, "Uploading image... 正在上传镜像...");
            var byteProgress = new Progress<long>(written =>
                Report(progress, partition, totalBytes > 0 ? (int)Math.Min(100, written * 100L / totalBytes) : 0, "Uploading image... 正在上传镜像..."));
            var response = session.FlashPartition(partition, image, totalBytes, byteProgress, ct);
            response.ThrowIfFailed();
            Report(progress, partition, 100, "Flash complete. 刷写完成。");
            return new FlashResult
            {
                Success = true,
                Summary = $"Flashed {partition} ({totalBytes} bytes) via fastboot."
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>EDL 路径：在镜像目录中查找 rawprogram*.xml 刷写包并经
    /// <see cref="FlashBundleOrchestrator"/> 编排整包刷写，适配其阶段进度。</para>
    /// EDL path: locates a rawprogram*.xml bundle in the image directory and orchestrates
    /// the full bundle flash through <see cref="FlashBundleOrchestrator"/>, adapting its
    /// phase progress.
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
                "No rawprogram*.xml found in the image directory; an EDL bundle (rawprogram + images) is required. 镜像目录中未找到 rawprogram*.xml，EDL 刷写需要完整刷写包。");
        }

        var patchPath = Directory.EnumerateFiles(imageDir, "patch*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var session = DeviceMonitorService.Instance.OpenEdlSession(device.Device)
            ?? throw new InvalidOperationException("No EDL session available for the selected device. 无法为所选设备建立 EDL 会话。");
        using var edl = session;

        var bundleProgress = new Progress<FlashBundleProgress>(p =>
        {
            var percent = p.Total > 0 ? (int)(p.Current * 100L / p.Total) : 0;
            Report(progress, partition, Math.Clamp(percent, 0, 100), p.Message);
        });

        var result = await new FlashBundleOrchestrator(edl)
            .FlashBundleAsync(rawProgramPath, patchPath, imageDir, bundleProgress, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var detail = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : "Unknown EDL failure.";
            throw new InvalidOperationException(detail);
        }

        Report(progress, partition, 100, "EDL bundle flash complete. EDL 刷写包刷写完成。");
        return new FlashResult
        {
            Success = true,
            Summary = $"EDL bundle flashed ({result.ProgramEntriesProcessed} program entries, {result.TotalBytesWritten} bytes)."
        };
    }

    /// <summary>
    /// <para>上报一条进度：既写入调用方提供的 <see cref="IProgress{T}"/>，
    /// 也发送 <see cref="FlashProgressMessage"/> 供输出面板 Flash 通道展示。</para>
    /// Reports a progress entry: writes to the caller-provided <see cref="IProgress{T}"/>
    /// and sends a <see cref="FlashProgressMessage"/> for the output panel's Flash channel.
    /// </summary>
    private static void Report(IProgress<FlashProgress>? progress, string? partition, int percent, string status)
    {
        progress?.Report(new FlashProgress(partition, percent, status));
        WeakReferenceMessenger.Default.Send(new FlashProgressMessage(partition, percent, status));
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