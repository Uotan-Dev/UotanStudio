using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// QR pairing progress stages.
/// <para>扫码配对进度阶段。</para>
/// </summary>
public enum QrPairingStage
{
    /// <summary>Waiting for the phone to scan the QR code.<para>等待手机扫描二维码。</para></summary>
    WaitingForScan,
    /// <summary>Pairing service found, pairing in progress.<para>已发现配对服务，正在配对。</para></summary>
    Pairing,
    /// <summary>Pairing done, establishing the connection.<para>配对完成，正在建立连接。</para></summary>
    Connecting,
}

/// <summary>
/// QR pairing progress; Detail carries the stage-specific address text.
/// <para>扫码配对进度。Detail 为阶段相关的地址文本。</para>
/// </summary>
/// <param name="Stage">The current pairing stage.<para>当前配对阶段。</para></param>
/// <param name="Detail">Stage-specific address text, when any.<para>阶段相关的地址文本，可为空。</para></param>
public sealed record QrPairingProgress(QrPairingStage Stage, string? Detail = null);

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
        IProgress<QrPairingProgress>? progress, CancellationToken cancellationToken = default,
        TimeSpan? discoveryTimeout = null)
    {
        var reporter = progress is null
            ? null
            : new Progress<AdbQrPairingProgress>(p =>
            {
                var mapped = p.Stage switch
                {
                    AdbQrPairingStage.DeviceFound => QrPairingStage.Pairing,
                    AdbQrPairingStage.Pairing => QrPairingStage.Pairing,
                    AdbQrPairingStage.Connecting => QrPairingStage.Connecting,
                    _ => QrPairingStage.WaitingForScan,
                };
                progress.Report(new QrPairingProgress(mapped, p.Detail));
            });

        var outcome = await AdbQrPairingService.PairAsync(credentials, reporter, discoveryTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Route == AdbQrPairingRoute.Direct)
        {
            await AttachSecureDirectNetworkTargetAsync(outcome.Address, cancellationToken).ConfigureAwait(false);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
        return outcome;
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
            await AttachSecureDirectNetworkTargetAsync(address, cancellationToken).ConfigureAwait(false);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
        return address;
    }

    /// <summary>
    /// After a successful direct QR pairing, validates the secure network target with one asynchronous TLS
    /// handshake and registers it as a manual network endpoint requiring TLS; rethrows the underlying reason
    /// on failure.
    /// <para>扫码直连配对成功后，对安全网络目标完成一次异步 TLS 握手验证，成功后登记为需要 TLS 的手动网络端点；
    /// 失败时向上抛出底层原因。</para>
    /// </summary>
    /// <param name="target">A secure <c>host:port</c> endpoint to validate and register.
    /// <para>要验证并登记的 <c>host:port</c> 安全端点。</para></param>
    /// <param name="cancellationToken">Cancels the connect and the peer handshake wait; registration is
    /// skipped after cancellation.
    /// <para>取消连接与对端握手等待；取消后跳过登记。</para></param>
    /// <exception cref="IOException">The target is not a <c>host:port</c> endpoint or the handshake failed.
    /// <para>目标不是 <c>host:port</c> 端点或握手失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    private async Task AttachSecureDirectNetworkTargetAsync(string target, CancellationToken cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_networkTargetsLock)
        {
            _manualNetworkTargets[target] = true;
        }
        MarkManualNetworkTargetVerified(target);
        SavePairedNetworkTargets();
    }
}