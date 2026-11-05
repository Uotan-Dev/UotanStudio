using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Cooperative deadline for waiting on the re-enumerated SpacemiT gadget after it left fastboot; the
    /// BootROM-to-U-Boot transition recreates the USB node, which can take several seconds on slow hosts.
    /// <para>进迭时空 gadget 离开 fastboot 后等待重枚举的协作式时限；BootROM 到 U-Boot 的切换会重建 USB 节点，
    /// 慢速主机上可能耗时数秒。</para>
    /// </summary>
    private const int SpacemiTReopenTimeoutMs = 60_000;

    /// <summary>
    /// Number of re-open attempts made when <c>continue</c> left the gadget on the same USB node: the vendor
    /// flasher loops ten times two seconds apart, so this mirrors its cadence within the cooperative deadline.
    /// <para><c>continue</c> 之后 gadget 仍停留在同一 USB 节点时的重开尝试次数：厂商烧录器循环十次、间隔两秒，
    /// 因此在协作式时限内对齐它的节奏。</para>
    /// </summary>
    private const int SpacemiTReopenAttempts = 10;

    /// <summary>
    /// Pause between two re-open attempts, matching the vendor flasher's two-second reconnect cadence.
    /// <para>两次重开尝试之间的暂停，对应厂商烧录器两秒的重连节奏。</para>
    /// </summary>
    private const int SpacemiTReopenDelayMs = 2_000;

    /// <summary>
    /// Runs a SpacemiT flash package against the device while holding one fastboot lease across the whole run:
    /// whenever the flasher reports a re-enumeration (after <c>continue</c> or an accepted super-speed switch),
    /// the stale lease is released, the re-enumerated gadget is waited for and reopened as a fresh fastboot
    /// session, and the run continues on it.
    /// <para>在跨越整个运行的单条 fastboot 租约上执行进迭时空烧录包：每当烧录器报告重新枚举（<c>continue</c>
    /// 之后或被接受的 super speed 切换之后），释放过期租约，等待重枚举出的 gadget 并以全新 fastboot 会话重开，
    /// 使运行在其上继续。</para>
    /// </summary>
    /// <remarks>
    /// The reconnection wait verifies the replacement endpoint's identity and never falls back to VID/PID or
    /// serial matching; a container-policy timeout retries once under the physical-port policy before the run is
    /// failed with the flasher's own re-enumeration error. The lease is released before the wait, as the wait
    /// refuses to run while any lease still holds the endpoint.
    /// <para>重连等待会校验替代端点的身份，绝不回退到 VID/PID 或序列号匹配；容器策略超时后按物理端口策略再试
    /// 一次，然后才让运行以烧录器自身的重枚举错误失败。等待之前先释放租约，因为该等待拒绝在任何租约仍持有
    /// 端点时运行。</para>
    /// </remarks>
    /// <param name="device">The selected SpacemiT fastboot device row. <para>所选进迭时空 fastboot 设备行。</para></param>
    /// <param name="package">The loaded flash package whose script drives the run.
    /// <para>已加载、其脚本驱动本次运行的烧录包。</para></param>
    /// <param name="options">Flash options; the confirmation phrase must already be set by the caller.
    /// <para>烧录选项；确认短语必须已由调用方设置。</para></param>
    /// <param name="progress">Optional progress sink receiving the flasher's phase and byte reports.
    /// <para>接收烧录器阶段与字节报告的可选进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between actions, between transfer chunks and during the
    /// reconnection waits. <para>在动作之间、传输分块之间与重连等待期间取消。</para></param>
    /// <exception cref="IOException">The device does not expose a fastboot session, or a reopened endpoint
    /// reported a different session kind. <para>设备不提供 fastboot 会话，或重开的端点报告了别的会话类型。</para></exception>
    /// <exception cref="FastbootException">The package script failed after all retries.
    /// <para>包脚本在重试耗尽后失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    internal async Task RunSpacemiTFlashAsync(DetectedDevice device, SpacemiTFlashPackage package,
        SpacemiTFlashOptions options, IProgress<SpacemiTFlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        CancelBackgroundEndpointWork(device);
        var currentLease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Fastboot,
            cancellationToken: cancellationToken);
        try
        {
            if (!currentLease.TryGetSession<FastbootSession>(out var session))
            {
                throw new IOException("The selected device does not expose Fastboot.");
            }

            var locationChain = currentLease.Endpoint.UsbInfo is { } info
                ? DeviceController.CaptureUsbLocationChain(info)
                : null;
            var flasher = new SpacemiTFlasher(package, options);
            await Task.Run(
                () => flasher.Execute(session, ReopenSpacemiTGadget, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            FastbootSession? ReopenSpacemiTGadget(FastbootSession stale)
            {
                var previousEndpoint = currentLease.Endpoint;
                CloseLease(currentLease);
                var fresh = WaitForSpacemiTReconnect(previousEndpoint, locationChain, cancellationToken);
                if (fresh is null)
                {
                    return null;
                }

                if (!fresh.TryGetSession<FastbootSession>(out var freshSession))
                {
                    CloseLease(fresh);
                    throw new IOException("The re-enumerated SpacemiT endpoint does not expose Fastboot.");
                }

                currentLease = fresh;
                return freshSession;
            }
        }
        finally
        {
            CloseLease(currentLease);
        }
    }

    /// <summary>
    /// Waits for the re-enumerated SpacemiT gadget and returns its lease, or <see langword="null"/> when every
    /// identity policy ran out of time or lacks the evidence it needs.
    /// <para>等待重枚举出的进迭时空 gadget 并返回其租约；所有身份策略都超时或缺少所需证据时返回
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <remarks>
    /// The board does not always re-enumerate on <c>continue</c>: the BootROM stage hands over to U-Boot and
    /// comes back as a new USB node (new serial, new container id — only the physical location survives), while
    /// U-Boot's own <c>continue</c> keeps the same node and simply answers the next command. The same-node reopen
    /// is therefore tried first and accepted only on a read-only <c>getvar</c> answer, which is what the vendor
    /// flasher does; the location and container policies cover the re-enumerating stage, and a bounded scan poll
    /// is the last resort.
    /// <para>板卡在 <c>continue</c> 之后并不总会重枚举：BootROM 阶段移交给 U-Boot 时以新的 USB 节点返回
    /// （新序列号、新容器 ID——只有物理位置延续），而 U-Boot 自身的 <c>continue</c> 保持同一节点并直接应答下一条
    /// 命令。因此先尝试就地重开，并只在只读 <c>getvar</c> 得到应答时才接受，这正是厂商烧录器的做法；位置与容器
    /// 策略覆盖会重枚举的阶段，有界的扫描轮询是最后回退。</para>
    /// </remarks>
    /// <param name="previous">The endpoint captured from the released stale lease.
    /// <para>从已释放的过期租约捕获的端点。</para></param>
    /// <param name="locationChain">The previous node's hub chain captured while it was alive.
    /// <para>旧节点存活时捕获的集线器链。</para></param>
    /// <param name="cancellationToken">Cancellation for the waits. <para>等待的取消令牌。</para></param>
    /// <returns>The verified replacement lease, or <see langword="null"/> when the gadget never reappeared.
    /// <para>经校验的替代租约；gadget 未重新出现时为 <see langword="null"/>。</para></returns>
    private DeviceLease? WaitForSpacemiTReconnect(DeviceEndpoint previous, string? locationChain,
        CancellationToken cancellationToken)
    {
        if (TryLeaseSpacemiTFastbootGadget(locationChain, cancellationToken) is { } sameNode)
        {
            return sameNode;
        }

        foreach (var policy in new[]
        {
            UsbReconnectIdentityPolicy.PhysicalLocation,
            UsbReconnectIdentityPolicy.PhysicalPort,
            UsbReconnectIdentityPolicy.Container
        })
        {
            try
            {
                return _controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Fastboot, policy,
                    SpacemiTReopenTimeoutMs, cancellationToken, locationChain).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is NotSupportedException or TimeoutException)
            {
            }
        }

        return PollSpacemiTFastbootGadget(locationChain, cancellationToken);
    }

    /// <summary>
    /// Re-polls the board after every reconnection policy ran out: the current snapshot is scanned for the
    /// SpacemiT fastboot endpoint at the captured physical location, leased fresh, and accepted only when a
    /// read-only fastboot probe answers, which is what proves the handle really drives the gadget.
    /// <para>在所有重连策略都用尽后重新轮询板卡：扫描当前快照中位于已捕获物理位置的进迭时空 fastboot 端点，
    /// 重新租用，并且只在只读 fastboot 探测得到应答时才接受——这才证明句柄真的驱动 gadget。</para>
    /// </summary>
    /// <param name="locationChain">The captured hub chain the replacement must still sit on. <para>替代端点仍须位于的已捕获集线器链。</para></param>
    /// <param name="cancellationToken">Cancels the poll. <para>取消轮询。</para></param>
    /// <returns>The verified lease, or <see langword="null"/> when no poll answered. <para>经校验的租约；没有轮询得到应答时为 <see langword="null"/>。</para></returns>
    private DeviceLease? PollSpacemiTFastbootGadget(string? locationChain, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SpacemiTReopenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                Thread.Sleep(SpacemiTReopenDelayMs);
            }

            if (TryLeaseSpacemiTFastbootGadget(locationChain, cancellationToken) is { } lease)
            {
                return lease;
            }
        }

        return null;
    }

    /// <summary>
    /// Leases the single SpacemiT fastboot endpoint of the current snapshot and verifies it with a read-only probe.
    /// <para>租用当前快照中唯一的进迭时空 fastboot 端点，并用只读探测校验它。</para>
    /// </summary>
    /// <remarks>
    /// The released lease's key cannot be reused: it embeds the BootROM stage's serial, which the next stage
    /// replaces. When a captured hub chain exists it is required to still match, so a second board on the same
    /// host is never picked up; without it, only an unambiguous single match is accepted. Any open or probe
    /// failure releases the lease and reports <see langword="null"/> instead of a half-usable session.
    /// <para>已释放租约的键不能复用：它内嵌 BootROM 阶段的序列号，而下一阶段会替换它。存在已捕获的集线器链时
    /// 要求它仍然匹配，因此同一主机上的第二块板卡绝不会被选中；没有链时只接受无歧义的唯一匹配。任何打开或探测
    /// 失败都会释放租约并报告 <see langword="null"/>，而不是返回一个半可用的会话。</para>
    /// </remarks>
    /// <param name="locationChain">The hub chain the endpoint must sit on, or <see langword="null"/> to accept a unique match.
    /// <para>端点必须位于的集线器链；为 <see langword="null"/> 时接受唯一匹配。</para></param>
    /// <param name="cancellationToken">Cancels the refresh, the open and the probe. <para>取消刷新、打开与探测。</para></param>
    /// <returns>The verified lease, or <see langword="null"/> when none matched or the probe failed.
    /// <para>经校验的租约；没有匹配或探测失败时为 <see langword="null"/>。</para></returns>
    private DeviceLease? TryLeaseSpacemiTFastbootGadget(string? locationChain, CancellationToken cancellationToken)
    {
        DeviceLease? lease = null;
        try
        {
            var snapshot = _controller.RefreshAsync(cancellationToken).GetAwaiter().GetResult();
            var candidates = snapshot.Endpoints
                .Where(endpoint => endpoint.Source == UsbDiscoverySource.SourceName &&
                    endpoint.Route == "direct-usb" &&
                    endpoint.Protocol == "fastboot" &&
                    endpoint.Usb is { VendorId: SpacemiTConstants.UsbVendorId, ProductId: SpacemiTConstants.UsbProductId } &&
                    (locationChain is null || string.Equals(
                        DeviceController.CaptureUsbLocationChain(endpoint.UsbInfo!), locationChain, StringComparison.Ordinal)))
                .Take(2)
                .ToArray();
            if (candidates.Length != 1)
            {
                return null;
            }

            lease = _controller.OpenAsync(snapshot, candidates[0].Id, DeviceSessionKind.Fastboot, cancellationToken)
                .GetAwaiter().GetResult();
            if (!lease.TryGetSession<FastbootSession>(out var session))
            {
                CloseLease(lease);
                return null;
            }

            if (session.GetVarWithResult(SpacemiTConstants.VarBromVersion, useCache: false).Success
                || session.GetVarWithResult("version", useCache: false).Success
                || session.GetVarWithResult("product", useCache: false).Success)
            {
                return lease;
            }

            CloseLease(lease);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or FastbootException or KeyNotFoundException or NotSupportedException or TimeoutException)
        {
            if (lease is not null)
            {
                CloseLease(lease);
            }

            return null;
        }
    }
}