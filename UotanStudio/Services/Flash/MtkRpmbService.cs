using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Hash.Digests;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The addressing shape of an RPMB capture: whole frames, a byte range, or the entire area the device reported.
/// <para>RPMB 捕获的寻址方式：按帧、按字节范围，或转储设备上报的整块区域。</para>
/// </summary>
internal enum MtkRpmbCaptureMode
{
    /// <summary>Read a run of frames by index. <para>按序号读取一段连续帧。</para></summary>
    Frames,

    /// <summary>Read a byte range of the RPMB partition. <para>读取 RPMB 分区的字节范围。</para></summary>
    ByteRange,

    /// <summary>Read the whole RPMB area the device reported. <para>读取设备上报的整块 RPMB 区域。</para></summary>
    FullArea,
}

/// <summary>
/// One read-only RPMB capture: the target download-agent family, the addressing shape, the range, the bounds and
/// the destination file.
/// <para>一次只读 RPMB 捕获的要求：目标协议族、寻址形态、范围、边界与落盘路径。</para>
/// </summary>
internal sealed record MtkRpmbCaptureRequest
{
    /// <summary>Gets the download-agent family the session is running.
    /// <para>获取会话正在运行的下载代理家族。</para></summary>
    /// <value>There is no default; the family must be named explicitly and is never detected from the device.
    /// <para>没有默认值；必须显式指名家族，且此处绝不按设备探测。</para></value>
    public required MtkDaProtocol Family { get; init; }

    /// <summary>Gets the destination file for the captured bytes.
    /// <para>获取捕获字节的落盘文件。</para></summary>
    /// <value>An absolute or relative path; the parent directory must already exist.
    /// <para>绝对或相对路径；父目录必须已存在。</para></value>
    public required string DestinationPath { get; init; }

    /// <summary>Gets the addressing shape of the capture. <para>获取捕获的寻址形态。</para></summary>
    /// <value><see cref="MtkRpmbCaptureMode.Frames"/> by default. <para>默认
    /// <see cref="MtkRpmbCaptureMode.Frames"/>。</para></value>
    public MtkRpmbCaptureMode Mode { get; init; } = MtkRpmbCaptureMode.Frames;

    /// <summary>Gets the frame range for the frame and full-area shapes.
    /// <para>获取帧与整块区域形态使用的帧范围。</para></summary>
    /// <value>One frame at index zero by default; the session validates it against the reported capacity.
    /// <para>默认读取零号的一帧；会话会依据上报容量校验。</para></value>
    public MtkRpmbFrameRange FrameRange { get; init; } = new(0, 1);

    /// <summary>Gets the byte offset for the byte-range shape. <para>获取字节范围形态使用的字节偏移。</para></summary>
    /// <value>Zero by default; must be aligned to the family's block width.
    /// <para>默认为零；必须按该家族的块宽度对齐。</para></value>
    public long ByteOffset { get; init; }

    /// <summary>Gets the byte count for the byte-range shape. <para>获取字节范围形态使用的字节数。</para></summary>
    /// <value>Zero by default, which the capture refuses before touching the endpoint.
    /// <para>默认为零，捕获会在接触端点前拒绝。</para></value>
    public long ByteLength { get; init; }

    /// <summary>Gets the read ceilings and deadline for this capture, if any.
    /// <para>获取本次捕获的读取上限与截止时间，可为 <see langword="null"/>。</para></summary>
    /// <value>Null uses the read-only defaults; a record enabling authenticated writes is refused.
    /// <para>为 null 时使用只读默认值；开启认证写入的记录会被拒绝。</para></value>
    public MtkRpmbOptions? Options { get; init; }
}

/// <summary>
/// A finished read-only RPMB capture: the published byte count, its digest, the frame statistics and the reported
/// capacity.
/// <para>一次已完成的只读 RPMB 捕获报告：落盘字节数、摘要、帧统计与上报容量。</para>
/// </summary>
/// <param name="Family">The download-agent family that was used. <para>实际使用的下载代理家族。</para></param>
/// <param name="Mode">The addressing shape that was used. <para>实际使用的寻址形态。</para></param>
/// <param name="DestinationPath">The full path of the atomically published file.
/// <para>已原子替换的落盘文件完整路径。</para></param>
/// <param name="ByteLength">The number of bytes published. <para>落盘字节数。</para></param>
/// <param name="Sha256">The hexadecimal SHA-256 of the published bytes.
/// <para>落盘字节的 SHA-256 十六进制文本。</para></param>
/// <param name="DeliveredFrames">The frames the device accepted and delivered; zero for a byte-range read.
/// <para>设备接受并交付的帧数量；按字节读取时为零。</para></param>
/// <param name="RequestedFrames">The requested frame count; zero for a byte-range read.
/// <para>请求的帧数量；按字节读取时为零。</para></param>
/// <param name="Target">The device interface carrying RPMB, or <see langword="null"/> when the Legacy byte read
/// reports no interface. <para>设备侧承载 RPMB 的接口；Legacy 家族的字节读取不上报该值时为
/// <see langword="null"/>。</para></param>
/// <param name="ReportedCapacityBytes">The RPMB capacity the device reported. <para>设备上报的 RPMB 容量。</para></param>
/// <param name="IsComplete">Whether the requested range was fully delivered. <para>请求范围是否被完整交付。</para></param>
internal sealed record MtkRpmbCaptureResult(MtkDaProtocol Family, MtkRpmbCaptureMode Mode, string DestinationPath,
    long ByteLength, string Sha256, int DeliveredFrames, int RequestedFrames, MtkRpmbTarget? Target,
    long? ReportedCapacityBytes, bool IsComplete)
{
    /// <summary>Gets a single-language summary of the capture. <para>获取本次捕获的单语言摘要。</para></summary>
    /// <value>The family, shape, byte count, digest and completeness.
    /// <para>家族、形态、字节数、摘要与完整性。</para></value>
    public string Summary => $"{Family} RPMB capture in {Mode} shape published {ByteLength} byte(s) to " +
        $"{DestinationPath} (SHA-256 {Sha256}, {DeliveredFrames}/{RequestedFrames} frame(s), interface " +
        $"{Target?.ToString() ?? "not reported"}, reported capacity {ReportedCapacityBytes?.ToString() ?? "unknown"}, " +
        $"{(IsComplete ? "complete" : "stopped at the first refused frame")}).";
}

/// <summary>
/// Read-only RPMB capture entry point: it routes by <see cref="MtkDaProtocol"/> to the matching download-agent
/// read command and publishes the bytes through a staging file with an atomic replace.
/// <para>只读 RPMB 捕获入口：按 <see cref="MtkDaProtocol"/> 路由到对应下载代理家族的读取命令，并把字节
/// 以临时文件加原子替换的方式落盘。</para>
/// </summary>
internal static class MtkRpmbService
{
    /// <summary>Captures RPMB bytes on a session the device lease already owns.
    /// <para>在设备租约已持有的会话上捕获 RPMB 字节。</para></summary>
    /// <param name="session">A leased MTK session whose active family matches <see cref="MtkRpmbCaptureRequest.Family"/>.
    /// <para>已租用的联发科会话，其活动家族需与 <see cref="MtkRpmbCaptureRequest.Family"/> 一致。</para></param>
    /// <param name="request">The family, shape, range, bounds and destination.
    /// <para>家族、形态、范围、边界与目标文件。</para></param>
    /// <param name="progress">Optional phase progress labelled <c>rpmb</c>. <para>可选的、标签为 <c>rpmb</c> 的阶段进度。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange and the publish.
    /// <para>交换与落盘的取消令牌。</para></param>
    /// <returns>The published byte count, digest and device-reported numbers.
    /// <para>落盘字节数、摘要以及设备上报的数值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="request"/> is null.
    /// <para><paramref name="session"/> 或 <paramref name="request"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The destination path is blank. <para>目标路径为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The request carries the authenticated-write opt-in; no byte is
    /// read and no file is created. <para>请求携带认证写入开关；不读取任何字节，也不创建文件。</para></exception>
    /// <exception cref="NotSupportedException">The family cannot speak the requested shape.
    /// <para>该家族无法表达所请求的形态。</para></exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled; the endpoint is untouched.
    /// <para>令牌已被取消；端点未被接触。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The byte range is empty or negative, or the DA-side bounds
    /// reject the range. <para>字节范围为空或为负，或代理侧边界拒绝该范围。</para></exception>
    internal static async Task<MtkRpmbCaptureResult> CaptureAsync(MtkSession session, MtkRpmbCaptureRequest request,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        RequireReadOnlyRequest(request);
        RequireSupportedShape(request.Family, request.Mode);
        cancellationToken.ThrowIfCancellationRequested();
        var options = ReadOnlyOptions(request.Options);
        if (request.Mode == MtkRpmbCaptureMode.ByteRange)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(request.ByteOffset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ByteLength);
        }

        progress?.Report(new FlashProgress("rpmb", 5,
            $"Reading the RPMB area through the {request.Family} download agent."));
        var capture = await ReadCaptureAsync(session, request, options, cancellationToken).ConfigureAwait(false);
        progress?.Report(new FlashProgress("rpmb", 60,
            $"Publishing {capture.Bytes.LongLength} captured byte(s) atomically."));
        var result = await PublishAsync(request.DestinationPath, capture, request.Family, request.Mode,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new FlashProgress("rpmb", 100, "RPMB capture published."));
        return result;
    }

    /// <summary>Captures RPMB bytes on an enumerated MTK endpoint under the device monitor's lease.
    /// <para>在设备监视器租约下，为已枚举出的联发科端点捕获 RPMB 字节。</para></summary>
    /// <param name="endpoint">A discovered MTK endpoint. <para>已发现的联发科端点。</para></param>
    /// <param name="request">The family, shape, range, bounds and destination.
    /// <para>家族、形态、范围、边界与目标文件。</para></param>
    /// <param name="progress">Optional phase progress. <para>可选的阶段进度。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole capture. <para>整个捕获的取消令牌。</para></param>
    /// <returns>The published byte count, digest and device-reported numbers.
    /// <para>落盘字节数、摘要以及设备上报的数值。</para></returns>
    /// <exception cref="InvalidOperationException">The request carries the authenticated-write opt-in.
    /// <para>请求携带认证写入开关。</para></exception>
    /// <exception cref="NotSupportedException">The family cannot speak the requested shape.
    /// <para>该家族无法表达所请求的形态。</para></exception>
    internal static async Task<MtkRpmbCaptureResult> CaptureOnEndpointAsync(DetectedDevice endpoint,
        MtkRpmbCaptureRequest request, IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        RequireReadOnlyRequest(request);
        RequireSupportedShape(request.Family, request.Mode);
        var result = await App.DeviceMonitor.RunMtkAsync(endpoint,
            session => CaptureAsync(session, request, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Queries the RPMB capacity read-only under the device monitor's lease and requests the XFlash storage
    /// status, keeping the reply undecoded except for the capacity field of the discovered geometry.
    /// <para>在设备监视器租约下只读查询 RPMB 容量并请求 XFlash 存储状态，除已发现几何信息中的容量字段外
    /// 应答保持未解码。</para>
    /// </summary>
    /// <param name="endpoint">A discovered MTK endpoint. <para>已发现的联发科端点。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole query. <para>整个查询的取消令牌。</para></param>
    /// <returns>The device-reported capacity and the undecoded status reply.
    /// <para>设备上报的容量与未解码的状态应答。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// The session never reached the XFlash storage-ready state, or the storage family has no RPMB area.
    /// <para>会话未进入 XFlash 存储就绪状态，或该存储族没有 RPMB 区域。</para>
    /// </exception>
    /// <exception cref="MtkProtocolException">A control frame was malformed or rejected.
    /// <para>控制帧格式错误或被拒绝。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired.
    /// <para>交换截止时间到期。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled at a cooperative point.
    /// <para>操作在任一协作点被取消。</para></exception>
    internal static Task<MtkRpmbStatusResult> QueryCapacityOnEndpointAsync(DetectedDevice endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return App.DeviceMonitor.RunMtkAsync(endpoint,
            session => session.QueryXFlashRpmbStatusAsync(null, cancellationToken), cancellationToken);
    }

    /// <summary>Explains which captures a family cannot perform, without contacting the device.
    /// <para>说明某家族无法执行哪类捕获，且不会联系设备。</para></summary>
    /// <param name="family">The download-agent family under evaluation.
    /// <para>待判定的下载代理家族。</para></param>
    /// <param name="mode">The addressing shape under evaluation.
    /// <para>待判定的寻址形态。</para></param>
    /// <returns>The refusal reason, or <see langword="null"/> when the family supports the shape.
    /// <para>拒绝原因；该家族支持此形态时为 <see langword="null"/>。</para></returns>
    internal static string? DescribeShapeRefusal(MtkDaProtocol family, MtkRpmbCaptureMode mode) => (family, mode) switch
    {
        (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.ByteRange) =>
            "The XFlash download agent addresses RPMB by frame index only, so a byte range cannot be read on this family.",
        (MtkDaProtocol.Xml, MtkRpmbCaptureMode.ByteRange) =>
            "The XML download agent addresses RPMB by frame index only, so a byte range cannot be read on this family.",
        (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.Frames) =>
            "The Legacy download agent reads RPMB as raw partition bytes and exposes no frame indexing or per-frame authentication status.",
        (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.FullArea) =>
            "The Legacy download agent reports no RPMB area size before a read, so a whole-area dump cannot be bounded on this family.",
        (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.Frames) or (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.FullArea)
            or (MtkDaProtocol.Xml, MtkRpmbCaptureMode.Frames) or (MtkDaProtocol.Xml, MtkRpmbCaptureMode.FullArea)
            or (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.ByteRange) => null,
        _ => $"The download-agent family {family} or the capture shape {mode} is not a supported combination."
    };

    /// <summary>Refuses any request that asks for RPMB mutation.
    /// <para>拒绝任何要求修改 RPMB 的请求。</para></summary>
    /// <param name="request">The request to validate. <para>待校验的请求。</para></param>
    /// <exception cref="InvalidOperationException">The authenticated-write opt-in is set.
    /// <para>认证写入开关已被设置。</para></exception>
    private static void RequireReadOnlyRequest(MtkRpmbCaptureRequest request)
    {
        if (request.Options is { AllowAuthenticatedWrites: true })
        {
            throw new InvalidOperationException(
                "This host path publishes read-only RPMB captures; frame writes, counter resets and authentication-key provisioning are not reachable and are refused.");
        }
    }

    /// <summary>Refuses a family and shape pair the download agent cannot speak.
    /// <para>拒绝下载代理无法表达的家族与形态组合。</para></summary>
    /// <param name="family">The selected family. <para>所选家族。</para></param>
    /// <param name="mode">The selected shape. <para>所选形态。</para></param>
    /// <exception cref="NotSupportedException">The pair is unsupported. <para>该组合不受支持。</para></exception>
    private static void RequireSupportedShape(MtkDaProtocol family, MtkRpmbCaptureMode mode)
    {
        if (DescribeShapeRefusal(family, mode) is { } refusal)
        {
            throw new NotSupportedException(refusal);
        }
    }

    /// <summary>Rebuilds the capture options so the write permission can never reach the communication layer.
    /// <para>重建捕获选项，使写入许可绝不可能抵达通信层。</para></summary>
    /// <param name="options">The requested ceilings and deadline, or <see langword="null"/>.
    /// <para>待归一化的上限与截止时间，可为 <see langword="null"/>。</para></param>
    /// <returns>The read-only options actually used. <para>实际使用的只读选项。</para></returns>
    private static MtkRpmbOptions ReadOnlyOptions(MtkRpmbOptions? options) =>
        (options ?? new MtkRpmbOptions()) with { AllowAuthenticatedWrites = false };

    /// <summary>Runs the family-specific read. <para>运行按家族区分的读取。</para></summary>
    /// <param name="session">The leased session. <para>已租用的会话。</para></param>
    /// <param name="request">The capture request. <para>捕获请求。</para></param>
    /// <param name="options">The read-only options in force. <para>生效的只读选项。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>交换的取消令牌。</para></param>
    /// <returns>The delivered bytes plus the device's own counters.
    /// <para>交付的字节以及设备自身的计数。</para></returns>
    private static async Task<RpmbCapture> ReadCaptureAsync(MtkSession session, MtkRpmbCaptureRequest request,
        MtkRpmbOptions options, CancellationToken cancellationToken) => (request.Family, request.Mode) switch
        {
            (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.Frames) => FromRead(
                await session.ReadXFlashRpmbFramesAsync(request.FrameRange, options, cancellationToken)
                    .ConfigureAwait(false)),
            (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.FullArea) => FromBlob(
                await session.ReadXFlashRpmbDeviceBlobAsync(options, cancellationToken).ConfigureAwait(false)),
            (MtkDaProtocol.Xml, MtkRpmbCaptureMode.Frames) => FromRead(
                await session.ReadXmlRpmbFramesAsync(request.FrameRange, options, cancellationToken)
                    .ConfigureAwait(false)),
            (MtkDaProtocol.Xml, MtkRpmbCaptureMode.FullArea) => FromBlob(
                await session.ReadXmlRpmbDeviceBlobAsync(options, cancellationToken).ConfigureAwait(false)),
            _ => await ReadLegacyCaptureAsync(session, request, options, cancellationToken).ConfigureAwait(false),
        };

    /// <summary>Reads the Legacy partition bytes into a bounded in-memory buffer.
    /// <para>把 Legacy 分区字节读取到有界的内存缓冲区。</para></summary>
    /// <param name="session">The leased session. <para>已租用的会话。</para></param>
    /// <param name="request">The capture request. <para>捕获请求。</para></param>
    /// <param name="options">The read-only options in force, whose frame ceiling also bounds the byte count.
    /// <para>生效的只读选项，其帧上限同样约束字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>交换的取消令牌。</para></param>
    /// <returns>The delivered bytes plus the reported capacity. <para>交付的字节以及上报的容量。</para></returns>
    private static async Task<RpmbCapture> ReadLegacyCaptureAsync(MtkSession session,
        MtkRpmbCaptureRequest request, MtkRpmbOptions options, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var read = await session.ReadLegacyRpmbAsync(request.ByteOffset, request.ByteLength, buffer, options,
            null, cancellationToken).ConfigureAwait(false);
        return new RpmbCapture(buffer.ToArray(), 0, 0, null, read.ReportedRpmbCapacityBytes,
            read.ByteLength == request.ByteLength);
    }

    /// <summary>Shapes a frame read into the common capture record.
    /// <para>把帧读取整理为通用捕获记录。</para></summary>
    /// <param name="read">The frame read the agent returned. <para>代理返回的帧读取结果。</para></param>
    /// <returns>The common capture record. <para>通用捕获记录。</para></returns>
    private static RpmbCapture FromRead(MtkRpmbReadResult read) => new(read.RawBytes, read.Frames.Count,
        read.Requested.FrameCount, read.Target, read.ReportedRpmbCapacityBytes, read.IsComplete);

    /// <summary>Shapes a whole-area dump into the common capture record.
    /// <para>把整块区域转储整理为通用捕获记录。</para></summary>
    /// <param name="blob">The dump the agent returned. <para>代理返回的转储。</para></param>
    /// <returns>The common capture record. <para>通用捕获记录。</para></returns>
    private static RpmbCapture FromBlob(MtkRpmbDeviceBlob blob) => new(blob.RawBytes, blob.FrameCount,
        blob.FrameCount, blob.Target, blob.ReportedRpmbCapacityBytes, blob.IsComplete);

    /// <summary>Writes the captured bytes through a staging file and replaces the destination atomically.
    /// <para>把捕获字节写入暂存文件，并原子替换目标文件。</para></summary>
    /// <param name="destinationPath">The destination file requested for the capture.
    /// <para>捕获请求的目标文件。</para></param>
    /// <param name="capture">The bytes and counters the read delivered.
    /// <para>读取交付的字节与计数。</para></param>
    /// <param name="family">The family that answered. <para>应答的家族。</para></param>
    /// <param name="mode">The shape that was read. <para>实际读取的形态。</para></param>
    /// <param name="cancellationToken">Cancellation around the staging write and the replace.
    /// <para>暂存写入与替换周围的取消令牌。</para></param>
    /// <returns>The capture report. <para>捕获报告。</para></returns>
    /// <exception cref="IOException">The staged file length disagrees with the captured byte count.
    /// <para>暂存文件长度与捕获字节数不一致。</para></exception>
    private static async Task<MtkRpmbCaptureResult> PublishAsync(string destinationPath, RpmbCapture capture,
        MtkDaProtocol family, MtkRpmbCaptureMode mode, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        var staging = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(capture.Bytes, cancellationToken).ConfigureAwait(false);
                if (output.Length != capture.Bytes.LongLength)
                {
                    throw new IOException(
                        $"The staged RPMB dump holds {output.Length} byte(s) instead of the captured {capture.Bytes.LongLength}.");
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new MtkRpmbCaptureResult(family, mode, destination, capture.Bytes.LongLength,
            Convert.ToHexString(Sha256.ComputeHash(capture.Bytes)), capture.DeliveredFrames,
            capture.RequestedFrames, capture.Target, capture.ReportedCapacityBytes, capture.IsComplete);
    }

    /// <summary>The bytes one family delivered plus the device's own counters.
    /// <para>某家族交付的字节以及设备自身的计数。</para></summary>
    /// <param name="Bytes">The delivered raw bytes. <para>交付的原始字节。</para></param>
    /// <param name="DeliveredFrames">The frames the device accepted; zero for a byte-range read.
    /// <para>设备接受的帧数；按字节读取时为零。</para></param>
    /// <param name="RequestedFrames">The requested frame count; zero for a byte-range read.
    /// <para>请求的帧数；按字节读取时为零。</para></param>
    /// <param name="Target">The interface carrying RPMB, when the family reports one.
    /// <para>承载 RPMB 的接口，可为 <see langword="null"/>。</para></param>
    /// <param name="ReportedCapacityBytes">The reported RPMB capacity. <para>设备上报的 RPMB 容量。</para></param>
    /// <param name="IsComplete">Whether the requested range was fully delivered. <para>请求范围是否完整交付。</para></param>
    private sealed record RpmbCapture(byte[] Bytes, int DeliveredFrames, int RequestedFrames,
        MtkRpmbTarget? Target, long? ReportedCapacityBytes, bool IsComplete);
}