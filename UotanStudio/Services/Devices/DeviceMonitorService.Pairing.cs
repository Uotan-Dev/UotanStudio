using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Wireless pairing progress stages.
/// <para>无线配对进度阶段。</para>
/// </summary>
public enum WirelessPairingStage
{
    /// <summary>Waiting for the phone to scan the QR code; pairing-code pairing skips this stage.
    /// <para>等待手机扫描二维码；配对码配对不会经过该阶段。</para></summary>
    WaitingForScan,
    /// <summary>Pairing service found, pairing in progress.<para>已发现配对服务，正在配对。</para></summary>
    Pairing,
    /// <summary>Pairing done, establishing the connection.<para>配对完成，正在建立连接。</para></summary>
    Connecting,
}

/// <summary>
/// Wireless pairing progress; Detail carries the stage-specific address text.
/// <para>无线配对进度。Detail 为阶段相关的地址文本。</para>
/// </summary>
/// <param name="Stage">The current pairing stage.<para>当前配对阶段。</para></param>
/// <param name="Detail">Stage-specific address text, when any.<para>阶段相关的地址文本，可为空。</para></param>
public sealed record WirelessPairingProgress(WirelessPairingStage Stage, string? Detail = null);

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Runs the QR wireless pairing flow, through the ADB server when it listens and through a direct
    /// SPAKE2 handshake otherwise. A successful direct pairing registers the secure network target.
    /// <para>执行扫码无线配对流程：有 ADB server 在监听时经 server 配对，否则走直连 SPAKE2 握手；成功的直连配对会登记安全网络目标。</para>
    /// </summary>
    /// <param name="credentials">The one-time QR credentials displayed in the QR code.
    /// <para>二维码中展示的一次性凭据。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度回调。</para></param>
    /// <param name="cancellationToken">Cancels discovery, pairing and connecting.
    /// <para>取消发现、配对与连接。</para></param>
    /// <param name="discoveryTimeout">Deadline for the phone to appear via mDNS; the default applies
    /// when <c>null</c>. <para>手机经 mDNS 出现的期限；为 <c>null</c> 时使用默认值。</para></param>
    /// <returns>The pairing outcome (route and connected address).
    /// <para>配对结果（路径与已连接地址）。</para></returns>
    public async Task<AdbQrPairingOutcome> RunQrPairingAsync(AdbQrPairingCredentials credentials,
        IProgress<WirelessPairingProgress>? progress, CancellationToken cancellationToken = default,
        TimeSpan? discoveryTimeout = null)
    {
        var reporter = MapPairingProgress(progress);

        AdbQrPairingOutcome outcome;
        try
        {
            outcome = await AdbQrPairingService.PairAsync(credentials, reporter, discoveryTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Warn, "DevicePairing",
                $"QR pairing failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        if (outcome.Route == AdbQrPairingRoute.Direct)
        {
            var verified = await AttachSecureDirectNetworkTargetAsync(outcome.Address, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(verified, outcome.Address, StringComparison.Ordinal))
            {
                outcome = outcome with { Address = verified };
            }
        }
        LogService.Instance.Write(LogLevel.Info, "DevicePairing",
            $"QR pairing connected {outcome.Address} through the {outcome.Route} route.");
        await ForceRefreshAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Pairs with the IP address and one-time code shown by the phone's wireless-debugging dialog, then
    /// connects and registers the resulting secure endpoint when the toolbox paired directly.
    /// <para>使用手机无线调试对话框显示的 IP 地址与一次性配对码完成配对，随后连接；由工具箱直连配对时登记
    /// 得到的安全端点。</para>
    /// </summary>
    /// <param name="pairingAddress">The pairing <c>ip:port</c> shown by the phone; the port is required.
    /// <para>手机显示的配对 <c>ip:port</c>；端口必填。</para></param>
    /// <param name="pairingCode">The one-time pairing code. <para>一次性配对码。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度回调。</para></param>
    /// <param name="cancellationToken">Cancels pairing and connecting. <para>取消配对与连接。</para></param>
    /// <returns>The connected address and the pairing route. <para>已连接地址与配对路径。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="pairingAddress"/> is not an explicit
    /// <c>host:port</c>, or <paramref name="pairingCode"/> is blank.
    /// <para><paramref name="pairingAddress"/> 不是显式 <c>host:port</c>，或 <paramref name="pairingCode"/> 为空。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public async Task<AdbQrPairingOutcome> PairWithCodeAsync(string pairingAddress, string pairingCode,
        IProgress<WirelessPairingProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePairingEndpoint(pairingAddress, out var host, out var pairingPort))
        {
            throw new ArgumentException("A pairing address requires an explicit host:port.",
                nameof(pairingAddress));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingCode);

        AdbQrPairingOutcome outcome;
        try
        {
            outcome = await AdbQrPairingService.PairWithCodeAsync(host, pairingPort, pairingCode,
                MapPairingProgress(progress), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Write(LogLevel.Warn, "DevicePairing",
                $"Pairing-code pairing failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        if (outcome.Route == AdbQrPairingRoute.Direct)
        {
            try
            {
                var verified = await AttachSecureDirectNetworkTargetAsync(outcome.Address, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(verified, outcome.Address, StringComparison.Ordinal))
                {
                    outcome = outcome with { Address = verified };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var suggestion = DebugBridgeNetworkAddress.TrySplitEndpoint(outcome.Address, out _, out var port)
                    ? port
                    : 0;
                throw new AdbQrPairingConnectPortException(AdbQrPairingRoute.Direct, host,
                    $"Pairing succeeded, but the secure connect endpoint {outcome.Address} could not be verified: " +
                    ex.Message, suggestion);
            }
        }
        LogService.Instance.Write(LogLevel.Info, "DevicePairing",
            $"Pairing code connected {outcome.Address} through the {outcome.Route} route.");
        await ForceRefreshAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Normalizes an explicit ADB pairing endpoint; the port is mandatory because the phone allocates its
    /// pairing port dynamically.
    /// <para>规范化显式 ADB 配对端点；端口必填，因为手机动态分配配对端口。</para>
    /// </summary>
    /// <param name="input">The <c>host:port</c> text shown by the phone. <para>手机显示的 <c>host:port</c> 文本。</para></param>
    /// <param name="host">The bare host on success. <para>成功时的裸主机。</para></param>
    /// <param name="port">The pairing port on success. <para>成功时的配对端口。</para></param>
    /// <returns>Whether the text is one explicit endpoint. <para>文本是否为单个显式端点。</para></returns>
    internal static bool TryNormalizePairingEndpoint(string? input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        return DebugBridgeNetworkAddress.TryNormalize(input, out var address) &&
               DebugBridgeNetworkAddress.TrySplitEndpoint(address, out host, out port);
    }

    private static IProgress<AdbQrPairingProgress>? MapPairingProgress(
        IProgress<WirelessPairingProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<AdbQrPairingProgress>(p => progress.Report(new WirelessPairingProgress(
            p.Stage switch
            {
                AdbQrPairingStage.DeviceFound or AdbQrPairingStage.Pairing => WirelessPairingStage.Pairing,
                AdbQrPairingStage.Connecting => WirelessPairingStage.Connecting,
                _ => WirelessPairingStage.WaitingForScan,
            },
            p.Detail)));
    }

    /// <summary>
    /// Connects a paired device that never advertised its secure connect service, using the port the
    /// phone displays. The direct route validates the connection with one TLS handshake.
    /// <para>连接已配对但从未广播安全连接服务的设备，使用手机显示的端口；直连路径以一次 TLS 握手验证连接。</para>
    /// </summary>
    /// <param name="route">The route the pairing completed through.
    /// <para>配对完成的路径。</para></param>
    /// <param name="host">The device IP address discovered during pairing.
    /// <para>配对阶段发现的设备 IP 地址。</para></param>
    /// <param name="port">The connect port shown on the phone. <para>手机上显示的连接端口。</para></param>
    /// <param name="cancellationToken">Cancels the connection. <para>取消连接。</para></param>
    /// <returns>The connected <c>host:port</c> address. <para>已连接的 <c>host:port</c> 地址。</para></returns>
    public async Task<string> CompleteQrPairingWithPortAsync(AdbQrPairingRoute route, string host, int port,
        CancellationToken cancellationToken = default)
    {
        var address = await AdbQrPairingService.ConnectPairedAsync(route, host, port, cancellationToken)
            .ConfigureAwait(false);
        if (route == AdbQrPairingRoute.Direct)
        {
            address = await AttachSecureDirectNetworkTargetAsync(address, cancellationToken).ConfigureAwait(false);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
        return address;
    }

    /// <summary>
    /// After a successful direct pairing, validates the secure network target with one asynchronous TLS
    /// handshake and registers it as a manual network endpoint requiring TLS. When the handshake fails on an
    /// advertised port that the device has already replaced, the current port is browsed once and the target is
    /// registered under that address instead; the original failure is rethrown when no replacement answers.
    /// <para>直连配对成功后，对安全网络目标完成一次异步 TLS 握手验证，成功后登记为需要 TLS 的手动网络端点。
    /// 广播端口已被设备替换导致握手失败时，按 IP 重新浏览一次当前端口并以该地址登记；没有可用的替代端口时重新抛出
    /// 原始失败。</para>
    /// </summary>
    /// <param name="target">A secure <c>host:port</c> endpoint to validate and register.
    /// <para>要验证并登记的 <c>host:port</c> 安全端点。</para></param>
    /// <param name="cancellationToken">Cancels the connect and the peer handshake wait; registration is
    /// skipped after cancellation.
    /// <para>取消连接与对端握手等待；取消后跳过登记。</para></param>
    /// <returns>The endpoint that answered, which differs from <paramref name="target"/> when the advertised
    /// port was stale. <para>实际应答的端点；广播端口过期时与 <paramref name="target"/> 不同。</para></returns>
    /// <exception cref="IOException">The target is not a <c>host:port</c> endpoint or the handshake failed.
    /// <para>目标不是 <c>host:port</c> 端点或握手失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    private async Task<string> AttachSecureDirectNetworkTargetAsync(string target, CancellationToken cancellationToken)
    {
        var verified = await VerifySecureNetworkTargetAsync(target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_networkTargetsLock)
        {
            _manualNetworkTargets[verified] = true;
        }
        MarkManualNetworkTargetVerified(verified);
        if (DebugBridgeNetworkAddress.TrySplitEndpoint(verified, out var host, out _))
        {
            DropOtherManualNetworkTargetsForHost(host, verified);
        }

        SavePairedNetworkTargets();
        return verified;
    }

    /// <summary>
    /// Completes one TLS handshake with a secure network target and returns the address that answered, which
    /// differs from <paramref name="target"/> when the advertised port was stale and a fresh browse resolved the
    /// current one.
    /// <para>与安全网络目标完成一次 TLS 握手并返回实际应答的地址；广播端口过期、重新浏览解析出当前端口时，
    /// 该地址与 <paramref name="target"/> 不同。</para>
    /// </summary>
    /// <param name="target">The advertised <c>host:port</c> endpoint.
    /// <para>广播出的 <c>host:port</c> 端点。</para></param>
    /// <param name="cancellationToken">Cancels the handshake and the port browse.
    /// <para>取消握手与端口浏览。</para></param>
    /// <returns>The address that completed the handshake. <para>完成握手的地址。</para></returns>
    /// <exception cref="IOException">Neither the advertised nor the browsed port completed a handshake.
    /// <para>广播端口与重新浏览到的端口都未完成握手。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    private async Task<string> VerifySecureNetworkTargetAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectSecureNetworkSessionAsync(target, cancellationToken).ConfigureAwait(false);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out var port))
            {
                throw new IOException("A paired secure target needs a host:port address.", ex);
            }

            var resolved = await ResolveCurrentSecurePortAsync(host, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } candidate || candidate == port)
            {
                throw;
            }

            var retried = DebugBridgeNetworkAddress.FormatEndpoint(host, candidate);
            await ConnectSecureNetworkSessionAsync(retried, cancellationToken).ConfigureAwait(false);
            return retried;
        }
    }

    /// <summary>
    /// Browses the current secure connect port for a device address and reports no replacement when the browse
    /// itself fails, so the caller keeps the original handshake failure.
    /// <para>为设备地址浏览当前安全连接端口；浏览本身失败时报告没有替代端口，使调用方保留原始握手失败。</para>
    /// </summary>
    /// <param name="host">The device address literal. <para>设备地址字面量。</para></param>
    /// <param name="cancellationToken">Cancels the browse. <para>取消浏览。</para></param>
    /// <returns>The browsed port, or <c>null</c> when the device did not answer.
    /// <para>浏览到的端口；设备未应答时为 <c>null</c>。</para></returns>
    private async Task<int?> ResolveCurrentSecurePortAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await _controller.ResolveSecureNetworkPortAsync(host, RediscoverSecurePortTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task ConnectSecureNetworkSessionAsync(string target, CancellationToken cancellationToken)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out var port))
        {
            throw new IOException("A paired secure target needs a host:port address.");
        }

        var session = _controller.OpenSecureNetworkAdbSession(host, port);
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            session.Dispose();
            throw;
        }

        session.Dispose();
    }
}