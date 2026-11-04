using UotanStudio.Comm.Contracts;
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
    /// <param name="previous">The endpoint captured from the released stale lease.
    /// <para>从已释放的过期租约捕获的端点。</para></param>
    /// <param name="locationChain">The previous node's hub chain captured while it was alive, reused by the
    /// physical-port policy. <para>旧节点存活时捕获的集线器链，供物理端口策略复用。</para></param>
    /// <param name="cancellationToken">Cancellation for the waits. <para>等待的取消令牌。</para></param>
    /// <returns>The verified replacement lease, or <see langword="null"/> when the gadget never reappeared.
    /// <para>经校验的替代租约；gadget 未重新出现时为 <see langword="null"/>。</para></returns>
    private DeviceLease? WaitForSpacemiTReconnect(DeviceEndpoint previous, string? locationChain,
        CancellationToken cancellationToken)
    {
        try
        {
            return _controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Fastboot,
                SpacemiTReopenTimeoutMs, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is NotSupportedException or TimeoutException)
        {
            try
            {
                return _controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Fastboot,
                    UsbReconnectIdentityPolicy.PhysicalPort, SpacemiTReopenTimeoutMs, cancellationToken,
                    locationChain).GetAwaiter().GetResult();
            }
            catch (Exception fallback) when (fallback is NotSupportedException or TimeoutException)
            {
                return null;
            }
        }
    }
}