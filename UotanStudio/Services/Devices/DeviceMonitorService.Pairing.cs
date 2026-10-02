using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// <para>扫码配对进度阶段。</para>
/// QR pairing progress stages.
/// </summary>
public enum QrPairingStage
{
    /// <summary>等待手机扫描二维码。Waiting for the phone to scan the QR code.</summary>
    WaitingForScan,
    /// <summary>已发现配对服务，正在配对。Pairing service found, pairing in progress.</summary>
    Pairing,
    /// <summary>配对完成，正在建立连接。Pairing done, establishing the connection.</summary>
    Connecting,
}

/// <summary>
/// <para>扫码配对进度。Detail 为阶段相关的地址文本。</para>
/// QR pairing progress; Detail carries the stage-specific address text.
/// </summary>
public sealed record QrPairingProgress(QrPairingStage Stage, string? Detail = null);

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>执行扫码无线配对全流程：等待手机扫描二维码后广播的配对服务，按 adb server
    /// 是否在监听分流——有 server 时经 server 配对并连接（凭据保存在 server 侧），无 server 时由
    /// Comm 直接完成 SPAKE2 配对并登记为安全的直连网络目标（凭据保存在本工具箱）。成功后触发
    /// 刷新。</para>
    /// Runs the full QR wireless pairing flow: waits for the pairing service the phone
    /// advertises after scanning, then routes by ADB server presence — through the server
    /// when it listens (credentials stored server-side), otherwise Comm performs the SPAKE2
    /// pairing itself and registers the result as a secure direct network target
    /// (credentials stored locally). Forces a refresh on success.
    /// </summary>
    /// <param name="credentials">The one-time QR credentials displayed in the QR code.
    /// <para>二维码中展示的一次性凭据。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度回调。</para></param>
    /// <param name="cancellationToken">Cancels discovery, pairing and connecting.
    /// <para>取消发现、配对与连接。</para></param>
    /// <param name="discoveryTimeout">Deadline for the phone to appear via mDNS; the Comm default
    /// applies when <c>null</c>. <para>手机经 mDNS 出现的期限；为 <c>null</c> 时使用 Comm 默认值。</para></param>
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
            AttachSecureDirectNetworkTarget(outcome.Address);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// <para>配对成功但设备未广播安全连接服务时的回退：用手机上“无线调试 → IP 地址与端口”
    /// 显示的端口完成连接。server 路由经 <c>host:connect:</c> 连接；直连路由做一次 TLS 握手
    /// 验证并登记为安全手动网络端点。成功后触发刷新。</para>
    /// Fallback for a device that paired but never advertised its secure connect service:
    /// completes the connection with the port shown on the phone's Wireless debugging page.
    /// The server route connects through <c>host:connect:</c>; the direct route validates with one
    /// TLS handshake and registers a secure manual network endpoint. Forces a refresh on success.
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
            AttachSecureDirectNetworkTarget(address);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
        return address;
    }

    /// <summary>
    /// <para>扫码直连配对成功后，对安全网络目标完成一次 TLS 握手验证，成功后登记为需要
    /// TLS 的手动网络端点；失败时向上抛出底层原因。</para>
    /// After a successful direct QR pairing, validates the secure network target with one
    /// TLS handshake and registers it as a manual network endpoint requiring TLS; rethrows
    /// the underlying reason on failure.
    /// </summary>
    private void AttachSecureDirectNetworkTarget(string target)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out var port))
        {
            throw new IOException("A paired secure target needs a host:port address.");
        }

        var session = _controller.OpenSecureNetworkAdbSession(host, port);
        try
        {
            session.Connect();
        }
        catch (Exception)
        {
            session.Dispose();
            throw;
        }
        session.Dispose();
        _manualNetworkTargets[target] = true;
        SavePairedNetworkTargets();
    }
}