using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>RPMB 捕获的寻址方式：按帧、按字节范围，或转储设备上报的整块区域。</para>
/// The addressing shape of an RPMB capture: whole frames, a byte range, or the entire area the device reported.
/// </summary>
/// <remarks>
/// <para>The three download-agent families do not share one addressing model. XFlash and the XML agent walk
/// fixed-width authenticated frames, while the Legacy agent's partition-selected read header carries a byte
/// address and a byte count, so a capture request must name the shape its family can actually speak.</para>
/// <para>三个下载代理家族并不共用同一种寻址模型。XFlash 与 XML 代理按固定宽度的认证帧前进，而 Legacy 代理的按分区
/// 选择读取头携带的是字节地址与字节数，因此捕获请求必须指明其家族确实能表达的形态。</para>
/// </remarks>
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
/// <para>一次只读 RPMB 捕获的要求：目标协议族、寻址形态、范围、边界与落盘路径。</para>
/// One read-only RPMB capture: the target download-agent family, the addressing shape, the range, the bounds and
/// the destination file.
/// </summary>
/// <remarks>
/// <para>The record never carries payload bytes, only where they should land. Authenticated writes are not part of
/// this contract at all: <see cref="MtkRpmbOptions"/> is accepted purely for its read ceilings and deadline, and a
/// request that tries to switch writes on is refused.</para>
/// <para>本记录从不携带负载字节，只说明字节应落到哪里。认证写入完全不在本契约内：接受
/// <see cref="MtkRpmbOptions"/> 只为使用其读取上限与截止时间，尝试打开写入的请求会被拒绝。</para>
/// </remarks>
internal sealed record MtkRpmbCaptureRequest
{
    /// <summary>Gets the download-agent family the session is running.
    /// <para>获取会话正在运行的下载代理家族。</para></summary>
    /// <value>There is no default; the caller must name the family, and nothing here detects it from the device.
    /// <para>没有默认值；调用方必须指名家族，且此处绝不按设备探测。</para></value>
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

    /// <summary>Gets the frame range used by the frame and full-area shapes.
    /// <para>获取帧与整块区域形态使用的帧范围。</para></summary>
    /// <value>One frame at index zero by default; the session validates it against the reported capacity.
    /// <para>默认读取零号的一帧；会话会依据上报容量校验。</para></value>
    public MtkRpmbFrameRange FrameRange { get; init; } = new(0, 1);

    /// <summary>Gets the byte offset used by the byte-range shape. <para>获取字节范围形态使用的字节偏移。</para></summary>
    /// <value>Zero by default; must be aligned to the family's block width.
    /// <para>默认为零；必须按该家族的块宽度对齐。</para></value>
    public long ByteOffset { get; init; }

    /// <summary>Gets the byte count used by the byte-range shape. <para>获取字节范围形态使用的字节数。</para></summary>
    /// <value>Zero by default, which the capture refuses before touching the endpoint.
    /// <para>默认为零，捕获会在接触端点前拒绝。</para></value>
    public long ByteLength { get; init; }

    /// <summary>Gets the caller's read ceilings and deadline, if any. <para>获取调用方的读取上限与截止时间，可能为
    /// <see langword="null"/>。</para></summary>
    /// <value>
    /// <see langword="null"/> uses the read-only defaults. A record with
    /// <see cref="MtkRpmbOptions.AllowAuthenticatedWrites"/> set is refused, because this host path has no write
    /// entry point at all. <para><see langword="null"/> 使用只读默认值。设置了
    /// <see cref="MtkRpmbOptions.AllowAuthenticatedWrites"/> 的记录会被拒绝，因为本宿主路径完全没有写入入口。</para>
    /// </value>
    public MtkRpmbOptions? Options { get; init; }
}

/// <summary>
/// <para>一次已完成的只读 RPMB 捕获报告：落盘字节数、摘要、帧统计与上报容量。</para>
/// A finished read-only RPMB capture: the published byte count, its digest, the frame statistics and the reported
/// capacity.
/// </summary>
/// <remarks>
/// <para>The captured RPMB frames are opaque authenticated blobs whose meaning the reference client does not
/// decode either, so the report states lengths, a digest and the device's own numbers instead of interpreting the
/// bytes.</para>
/// <para>捕获到的 RPMB 帧是不透明的认证数据块，参考实现同样不解析其含义，因此本报告只给出长度、摘要与设备自身的
/// 数值，而不解释这些字节。</para>
/// </remarks>
/// <param name="Family"><para>实际使用的下载代理家族。</para>The download-agent family that was used.</param>
/// <param name="Mode"><para>实际使用的寻址形态。</para>The addressing shape that was used.</param>
/// <param name="DestinationPath"><para>已原子替换的落盘文件完整路径。</para>
/// The full path of the atomically published file.</param>
/// <param name="ByteLength"><para>落盘字节数。</para>The number of bytes published.</param>
/// <param name="Sha256"><para>落盘字节的 SHA-256 十六进制文本。</para>
/// The hexadecimal SHA-256 of the published bytes.</param>
/// <param name="DeliveredFrames"><para>设备接受并交付的帧数量；按字节读取时为零。</para>
/// The frames the device accepted and delivered; zero for a byte-range read.</param>
/// <param name="RequestedFrames"><para>请求的帧数量；按字节读取时为零。</para>
/// The requested frame count; zero for a byte-range read.</param>
/// <param name="Target"><para>设备侧承载 RPMB 的接口；Legacy 家族的字节读取不上报该值时为
/// <see langword="null"/>。</para>The device interface carrying RPMB, or <see langword="null"/> when the Legacy
/// byte read reports no interface.</param>
/// <param name="ReportedCapacityBytes"><para>设备上报的 RPMB 容量。</para>The RPMB capacity the device reported.</param>
/// <param name="IsComplete"><para>请求范围是否被完整交付。</para>Whether the requested range was fully delivered.</param>
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
/// <para>宿主层的只读 RPMB 捕获入口：按 <see cref="MtkDaProtocol"/> 路由到对应下载代理家族的读取命令，并把字节
/// 以临时文件加原子替换的方式落盘。</para>
/// Host-side read-only RPMB capture entry point: it routes by <see cref="MtkDaProtocol"/> to the matching
/// download-agent read command and publishes the bytes through a staging file with an atomic replace.
/// </summary>
/// <remarks>
/// <para>The three families expose different read surfaces, so the routing table is a capability table rather than
/// a switch over names. XFlash offers the status query, a frame read and a whole-area device dump
/// (<c>MtkSession.QueryXFlashRpmbStatusAsync</c>, <c>MtkSession.ReadXFlashRpmbFramesAsync</c>,
/// <c>MtkSession.ReadXFlashRpmbDeviceBlobAsync</c>); the XML agent offers the frame read and the whole-area dump but
/// no status control code; the Legacy agent only reaches RPMB through its partition-selected byte read
/// (<c>MtkSession.ReadLegacyRpmbAsync</c>), which carries byte counts and reports no per-frame authentication
/// status.</para>
/// <para>三个家族暴露的读取接口并不相同，因此这张路由表是能力表而不是名字分支。XFlash 提供状态查询、帧读取与整块区域
/// 转储（<c>MtkSession.QueryXFlashRpmbStatusAsync</c>、<c>MtkSession.ReadXFlashRpmbFramesAsync</c>、
/// <c>MtkSession.ReadXFlashRpmbDeviceBlobAsync</c>）；XML 代理提供帧读取与整块区域转储但没有状态控制码；Legacy 代理只能
/// 通过其按分区选择的字节读取访问 RPMB（<c>MtkSession.ReadLegacyRpmbAsync</c>），该读取携带字节数且不上报逐帧认证状态。</para>
/// <para>A shape the family cannot speak is refused explicitly with <see cref="NotSupportedException"/> instead of
/// being approximated, because padding a byte range up to frames, or guessing a Legacy frame index from a byte
/// offset, would report bytes the device never delivered in that form. Writes, counter resets and authentication-key
/// provisioning have no host entry point at all and are refused even when the communication layer would accept the
/// opt-in.</para>
/// <para>家族无法表达的形态会以 <see cref="NotSupportedException"/> 明确拒绝，而不是近似实现：把字节范围补到帧、或从字节
/// 偏移猜测 Legacy 帧序号，都会上报设备从未以该形态交付的字节。写入、计数器重置与认证密钥写入在本宿主层完全没有入口，
/// 即使通信层接受该开关也会被拒绝。</para>
/// <para>Published bytes follow the same staging discipline the storage read paths inside the communication layer
/// use: a uniquely named temporary file beside the destination, a length check, then one atomic move. A failure
/// leaves the previous destination content untouched.</para>
/// <para>落盘字节遵循通信层内部存储读取路径使用的同一套暂存纪律：目标旁的唯一命名临时文件、长度校验，然后一次原子
/// 替换。失败时目标的原有内容保持不变。</para>
/// </remarks>
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
    /// <remarks>
    /// The read-only and capability refusals happen before the lease is taken, so an unsupported family never opens
    /// the endpoint. <para>只读与能力拒绝发生在取得租约之前，因此不受支持的家族绝不会打开该端点。</para>
    /// </remarks>
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

    /// <summary>Explains which captures a family cannot perform, without contacting the device.
    /// <para>说明某家族无法执行哪类捕获，且不会联系设备。</para></summary>
    /// <param name="family">The download-agent family the caller selected.
    /// <para>调用方选择的下载代理家族。</para></param>
    /// <param name="mode">The addressing shape the caller selected.
    /// <para>调用方选择的寻址形态。</para></param>
    /// <returns>The refusal reason, or <see langword="null"/> when the family supports the shape.
    /// <para>拒绝原因；该家族支持此形态时为 <see langword="null"/>。</para></returns>
    /// <remarks>
    /// The app layer can call this to disable a control before a session exists; it reads no device state and
    /// authorises nothing by itself. <para>应用层可以在会话建立前调用本方法来禁用控件；它不读取设备状态，本身也不构成授权。</para>
    /// </remarks>
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
    /// <param name="request">The caller's request. <para>调用方的请求。</para></param>
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
    /// <param name="options">The caller's ceilings and deadline, or <see langword="null"/>.
    /// <para>调用方的上限与截止时间，可为 <see langword="null"/>。</para></param>
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
    /// <param name="destinationPath">The caller's destination. <para>调用方的目标文件。</para></param>
    /// <param name="capture">The bytes and counters the read delivered.
    /// <para>读取交付的字节与计数。</para></param>
    /// <param name="family">The family that answered. <para>应答的家族。</para></param>
    /// <param name="mode">The shape that was read. <para>实际读取的形态。</para></param>
    /// <param name="cancellationToken">Cancellation around the staging write and the replace.
    /// <para>暂存写入与替换周围的取消令牌。</para></param>
    /// <returns>The capture report. <para>捕获报告。</para></returns>
    /// <remarks>
    /// The staging name is unique so two captures can never race onto the same temporary file, and the staged
    /// length must equal the delivered byte count before the destination is replaced.
    /// <para>暂存名称是唯一的，因此两次捕获绝不会争用同一个临时文件；只有暂存长度与交付字节数一致时才会替换目标。</para>
    /// </remarks>
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
            Convert.ToHexString(SHA256.HashData(capture.Bytes)), capture.DeliveredFrames,
            capture.RequestedFrames, capture.Target, capture.ReportedCapacityBytes, capture.IsComplete);
    }

    /// <summary>The bytes one family delivered plus the device's own counters.
    /// <para>某家族交付的字节以及设备自身的计数。</para></summary>
    /// <param name="Bytes"><para>交付的原始字节。</para>The delivered raw bytes.</param>
    /// <param name="DeliveredFrames"><para>设备接受的帧数；按字节读取时为零。</para>
    /// The frames the device accepted; zero for a byte-range read.</param>
    /// <param name="RequestedFrames"><para>请求的帧数；按字节读取时为零。</para>
    /// The requested frame count; zero for a byte-range read.</param>
    /// <param name="Target"><para>承载 RPMB 的接口，可为 <see langword="null"/>。</para>
    /// The interface carrying RPMB, when the family reports one.</param>
    /// <param name="ReportedCapacityBytes"><para>设备上报的 RPMB 容量。</para>The reported RPMB capacity.</param>
    /// <param name="IsComplete"><para>请求范围是否完整交付。</para>Whether the requested range was fully delivered.</param>
    private sealed record RpmbCapture(byte[] Bytes, int DeliveredFrames, int RequestedFrames,
        MtkRpmbTarget? Target, long? ReportedCapacityBytes, bool IsComplete);
}