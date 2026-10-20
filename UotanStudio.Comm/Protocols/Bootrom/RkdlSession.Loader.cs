namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Downloads a complete RKBOOT loader image in two stages and waits for the device to
    /// re-enumerate.
    /// <para>分两阶段下载完整的 RKBOOT loader 镜像，并等待设备重新枚举。</para>
    /// </summary>
    /// <remarks>
    /// <para>The entry tables are parsed first, and the image is rejected before any vendor request is
    /// sent when any entry declares a <c>dwDataDelay</c> above
    /// <see cref="RkdlConstants.MaxLoaderDataDelayMs"/>. Entries whose <c>DataSize</c> is zero are
    /// skipped: they are neither sent nor delayed, matching upstream <c>RKBoot.cpp</c>. Every 471
    /// (first-stage) entry is then sent before every 472 (second-stage) entry through the 0x0471/0x0472
    /// vendor requests, honouring each entry's bounded settle delay, and the method finally waits
    /// <see cref="RkdlConstants.LoaderReenumerationSettleMs"/> milliseconds for re-enumeration.</para>
    /// <para>先解析条目表；当任一条目声明的 <c>dwDataDelay</c> 超过
    /// <see cref="RkdlConstants.MaxLoaderDataDelayMs"/> 时，在任何 vendor 请求发出之前拒绝该镜像。
    /// <c>DataSize</c> 为零的条目会被跳过：既不发送也不延时，与上游 <c>RKBoot.cpp</c> 一致。
    /// 随后先通过 0x0471 vendor 请求发送全部 471（第一阶段）条目，再通过 0x0472 请求发送全部
    /// 472（第二阶段）条目，并遵循每个条目有界的稳定延时；最后等待
    /// <see cref="RkdlConstants.LoaderReenumerationSettleMs"/> 毫秒让设备重新枚举。</para>
    /// <para>The <c>ucRc4Flag</c> is informational: upstream <c>RKBoot.cpp</c> reads a non-zero value as
    /// "RC4 disabled" and boot_merger writes 1 by default, so the flag is not a rejection condition and
    /// entry payloads are sent verbatim either way.</para>
    /// <para><c>ucRc4Flag</c> 仅作信息：上游 <c>RKBoot.cpp</c> 把非 0 值解释为“禁用 RC4”，
    /// boot_merger 默认写入 1，因此该标志不是拒绝条件，条目负载始终原样发送。</para>
    /// <para>Completion of that settle window is not evidence that the device reached loader mode:
    /// <see cref="IsRockusbMode"/> derives from a device-info snapshot taken when this session was
    /// constructed and can never observe the transition, and the loader download switches the USB
    /// product ID (a maskrom PID versus
    /// <see cref="RkdlConstants.KnownLoaderProductIds"/>). Callers must re-observe the device through
    /// the device layer, using <see cref="CreateLoaderTransitionOffer"/>; whether a PID-switching
    /// bootrom transition passes the controller's USB reconnection validation needs real-device
    /// confirmation and has not been verified in this repository. A positive byte-count return
    /// therefore only means every entry was transmitted, not that the loader took over.</para>
    /// <para>该稳定窗口的结束并不能证明设备已进入 loader 模式：<see cref="IsRockusbMode"/> 派生自本会话
    /// 构造时拍摄的设备信息快照，在会话内永远观测不到该模式变化，而且 loader 下载会切换 USB 产品 ID
    /// （maskrom PID 与 <see cref="RkdlConstants.KnownLoaderProductIds"/> 之列的 loader ID）。
    /// 调用方必须借助 <see cref="CreateLoaderTransitionOffer"/> 通过设备层重新观测设备；切换 PID 的
    /// bootrom 迁移能否通过控制器的 USB 重连校验需真机确认，本仓库未在硬件上验证。因此正的字节数
    /// 返回值只代表每个条目已发出，不代表 loader 已经接管。</para>
    /// </remarks>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <returns>The total number of bytes transferred; not a proof that the device entered loader
    /// mode. <para>实际传输的字节总数，并非设备已进入 loader 模式的证明。</para></returns>
    /// <exception cref="RkdlException">The device is not in maskrom mode, the image fails to parse,
    /// any entry declares a <c>dwDataDelay</c> above
    /// <see cref="RkdlConstants.MaxLoaderDataDelayMs"/> (the raw value from the untrusted loader
    /// file is quoted in the message and no vendor request is sent), or a vendor request was short.
    /// <para>设备不处于 maskrom 模式、镜像解析失败、任一条目声明的
    /// <c>dwDataDelay</c> 超过 <see cref="RkdlConstants.MaxLoaderDataDelayMs"/>（消息中会引用来自不受信
    /// loader 文件的原始数值，且不会发出任何 vendor 请求），或 vendor 请求写入过短。</para></exception>
    public int DownloadBootLoader(byte[] loaderImage)
    {
        var image = PrepareLoaderImage(loaderImage);

        var total = 0;

        foreach (var entry in image.Entries471)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            total += DeviceRequest(RkdlConstants.RkdlWIndex471, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep(checked((int)entry.DataDelay));
            }
        }

        foreach (var entry in image.Entries472)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            total += DeviceRequest(RkdlConstants.RkdlWIndex472, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep(checked((int)entry.DataDelay));
            }
        }

        Thread.Sleep(RkdlConstants.LoaderReenumerationSettleMs);
        return total;
    }

    /// <summary>Asynchronously downloads a complete RKBOOT loader image; see <see cref="DownloadBootLoader(byte[])"/> for the protocol flow, the delay bound, and the settle-window caveats. Every entry delay and the final re-enumeration settle window are cooperative waits on <paramref name="cancellationToken"/>.
    /// <para>异步下载完整的 RKBOOT loader 镜像；协议流程、延时上界与稳定窗口的注意事项见 <see cref="DownloadBootLoader(byte[])"/>。每个条目延时与最终的重枚举稳定窗口都会协作式地 等待 <paramref name="cancellationToken"/>。</para></summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin).
    /// <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <param name="cancellationToken">A cancellation token observed by every delay in the download.
    /// <para>下载过程中每段等待都会观测的取消令牌。</para></param>
    /// <returns>A task that resolves to the total number of bytes transferred; completion is not proof that the device entered loader mode.
    /// <para>返回实际传输字节总数的任务；完成并不代表 设备已进入 loader 模式。</para></returns>
    /// <exception cref="RkdlException">Propagated from <see cref="DownloadBootLoader(byte[])"/> for the same guards.
    /// <para>与 <see cref="DownloadBootLoader(byte[])"/> 相同的守卫抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during any delay of the download.
    /// <para>令牌在下载开始前或任一段等待期间被取消。</para></exception>
    public async Task<int> DownloadBootLoaderAsync(byte[] loaderImage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = PrepareLoaderImage(loaderImage);

        var total = 0;

        foreach (var entry in image.Entries471)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            total += DeviceRequest(RkdlConstants.RkdlWIndex471, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                await Task.Delay(checked((int)entry.DataDelay), cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var entry in image.Entries472)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            total += DeviceRequest(RkdlConstants.RkdlWIndex472, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                await Task.Delay(checked((int)entry.DataDelay), cancellationToken).ConfigureAwait(false);
            }
        }

        await Task.Delay(RkdlConstants.LoaderReenumerationSettleMs, cancellationToken).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// Publishes the re-enumeration evidence for this session: the physical identity captured when
    /// the session was opened and the default relink budget.
    /// <para>发布本会话的重枚举证据：会话打开时捕获的物理身份与默认重连预算。</para>
    /// </summary>
    /// <remarks>
    /// <para>The RKDL session is not leased through the device layer, so
    /// <see cref="RkdlLoaderTransitionOffer.PreviousEndpoint"/> and
    /// <see cref="RkdlLoaderTransitionOffer.ObservedGeneration"/> are <see langword="null"/> here and
    /// the caller completes them with <c>with</c> expressions. This offer never reconnects and never
    /// claims the device reached loader mode; see <see cref="RkdlLoaderTransitionOffer"/> for why
    /// in-session observation of the transition is impossible and why the PID switch still needs
    /// real-device confirmation against the controller's VID/PID equality check.</para>
    /// <para>RKDL 会话并非经由设备层租用，因此这里的
    /// <see cref="RkdlLoaderTransitionOffer.PreviousEndpoint"/> 与
    /// <see cref="RkdlLoaderTransitionOffer.ObservedGeneration"/> 为 <see langword="null"/>，需由调用方
    /// 用 <c>with</c> 表达式补全。该提议绝不重连，也绝不声称设备已进入 loader 模式；为何无法在会话内
    /// 观测该迁移，以及 PID 切换为何仍需针对控制器 VID/PID 相等校验做真机确认，见
    /// <see cref="RkdlLoaderTransitionOffer"/>。</para>
    /// </remarks>
    /// <returns>The offer carrying the captured identity and the default timeout.
    /// <para>携带已捕获身份与默认超时的提议。</para></returns>
    public RkdlLoaderTransitionOffer CreateLoaderTransitionOffer() => new()
    {
        PreviousEndpoint = null,
        Identity = _physicalIdentity,
        ObservedGeneration = null,
    };

    /// <summary>
    /// Runs every guard that must reject a loader image before the first vendor request: maskrom
    /// mode, parse success, and the <c>dwDataDelay</c> bound of both entry tables. The
    /// <c>ucRc4Flag</c> is not a guard: upstream reads a non-zero value as "RC4 disabled", and
    /// zero-length entries are skipped because they are never sent or delayed.
    /// <para>执行所有必须在首个 vendor 请求之前拒绝 loader 镜像的守卫：maskrom 模式、解析成功、
    /// 以及两张条目表的 <c>dwDataDelay</c> 上界。<c>ucRc4Flag</c> 不是守卫：上游把非 0 值解释为
    /// “禁用 RC4”；零长度条目会被跳过，因为它们既不会被发送也不会延时。</para>
    /// </summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <returns>The validated parsed image. <para>已通过校验的解析结果。</para></returns>
    private RkBootImage PrepareLoaderImage(byte[] loaderImage)
    {
        ArgumentNullException.ThrowIfNull(loaderImage);
        if (!IsMaskromMode)
        {
            throw new RkdlException(RkdlError.NotSupported, "Loader download requires a maskrom-mode device (0x0471/0x0472 vendor requests).");
        }

        var image = RkBootImage.Parse(loaderImage);

        foreach (var entry in image.Entries471)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            RequireDataDelayWithinBound(entry);
        }

        foreach (var entry in image.Entries472)
        {
            if (entry.DataSize == 0)
            {
                continue;
            }

            RequireDataDelayWithinBound(entry);
        }

        return image;
    }

    /// <summary>
    /// Rejects a loader entry whose <c>dwDataDelay</c>, read from an untrusted loader file, exceeds
    /// <see cref="RkdlConstants.MaxLoaderDataDelayMs"/> before it can be converted to a wait, so a
    /// crafted file can neither pin the download path nor wrap negative through an
    /// <see cref="int"/> conversion.
    /// <para>在下载条目来自不受信 loader 文件的 <c>dwDataDelay</c> 超过
    /// <see cref="RkdlConstants.MaxLoaderDataDelayMs"/> 时、在把它转换为等待时长之前将其拒绝，
    /// 使伪造文件既无法挂住下载路径，也无法经 <see cref="int"/> 转换回绕为负值。</para>
    /// </summary>
    /// <param name="entry">The parsed loader entry. <para>已解析的 loader 条目。</para></param>
    private static void RequireDataDelayWithinBound(RkBootEntry entry)
    {
        if (entry.DataDelay > (uint)RkdlConstants.MaxLoaderDataDelayMs)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Loader entry '{entry.Name}' declares dwDataDelay {entry.DataDelay} ms in an untrusted loader file, above the {RkdlConstants.MaxLoaderDataDelayMs} ms settle bound.");
        }
    }

    /// <summary>
    /// Extracts the data slice of a loader entry. <para>提取 loader 条目的数据切片。</para>
    /// </summary>
    private static byte[] SliceEntry(byte[] loaderImage, RkBootEntry entry)
    {
        var end = (long)entry.DataOffset + entry.DataSize;
        if (entry.DataOffset >= loaderImage.Length || end > loaderImage.Length)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader entry '{entry.Name}' data out of range.");
        }

        var data = new byte[entry.DataSize];
        Array.Copy(loaderImage, entry.DataOffset, data, 0, entry.DataSize);
        return data;
    }

    /// <summary>
    /// Downloads the first-stage loader via the 0x0471 vendor request.
    /// <para>通过 0x0471 vendor 请求下载第一阶段 loader。</para>
    /// Data is sent in 4096-byte chunks with a trailing CRC-16/CCITT, including the 4094/4095 padding
    /// rules. The request carries BmRequestType = 0x40, BRequest = 0x0C and wIndex = 0x0471.
    /// <para>数据按 4096 字节分块发送，尾部追加 CRC-16/CCITT，并遵循 4094/4095 填充规则。该请求使用
    /// BmRequestType = 0x40、BRequest = 0x0C 与 wIndex = 0x0471。</para>
    /// </summary>
    /// <remarks>The caller-supplied bytes are sent verbatim, exactly like the entries of
    /// <see cref="DownloadBootLoader(byte[])"/>; no <c>ucRc4Flag</c> check is applied.
    /// <para>调用方给定的字节原样发送，与 <see cref="DownloadBootLoader(byte[])"/> 的条目一致；
    /// 此处不检查 <c>ucRc4Flag</c>。</para></remarks>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader471(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex471, loaderData);

    /// <summary>
    /// Downloads the second-stage loader via the 0x0472 vendor request
    /// (BmRequestType = 0x40, BRequest = 0x0C, wIndex = 0x0472). See
    /// <see cref="DownloadBootLoader471"/> for the framing rules.
    /// <para>通过 0x0472 vendor 请求下载第二阶段 loader
    /// （BmRequestType = 0x40，BRequest = 0x0C，wIndex = 0x0472）。
    /// 帧规则见 <see cref="DownloadBootLoader471"/>。</para>
    /// </summary>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader472(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex472, loaderData);

}