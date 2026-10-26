using System.Net.Sockets;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Comm.Transports.Udp;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// The route a QR pairing completed through.
/// <para>扫码配对路径。</para>
/// </summary>
public enum AdbQrPairingRoute
{
    /// <summary>
    /// Paired and connected through the local adb server (credentials stored server-side).
    /// <para>经本机 adb server 配对并连接（凭据保存在 server 侧）。</para>
    /// </summary>
    AdbServer,
    /// <summary>
    /// Paired and connected by the toolbox itself: pairing registers this host's ADB public key on the
    /// device and the remembered endpoint stays in toolbox settings, with no stored certificate.
    /// <para>由工具箱直接配对并连接：配对把本主机的 ADB 公钥登记到设备，记住的地址仍存放在工具箱设置中，
    /// 不保存任何证书。</para>
    /// </summary>
    Direct,
}

/// <summary>
/// QR pairing progress stage; Detail carries the stage-specific <c>ip:port</c> text.
/// <para>扫码配对进度阶段。Detail 携带阶段相关的 <c>ip:port</c> 文本。</para>
/// </summary>
public enum AdbQrPairingStage
{
    /// <summary>
    /// The pairing service advertised by the phone was found.
    /// <para>已发现手机广播的配对服务。</para>
    /// </summary>
    DeviceFound,
    /// <summary>
    /// The SPAKE2 pairing is in progress.
    /// <para>正在执行 SPAKE2 配对。</para>
    /// </summary>
    Pairing,
    /// <summary>
    /// Pairing finished, establishing the connection.
    /// <para>配对完成，正在建立连接。</para>
    /// </summary>
    Connecting,
}

/// <summary>
/// QR pairing progress report.
/// <para>扫码配对进度。</para>
/// </summary>
/// <param name="Stage">The reached stage. <para>已达成的阶段。</para></param>
/// <param name="Detail">Stage-specific address text, if any.
/// <para>阶段相关的地址文本，可能为空。</para></param>
public sealed record AdbQrPairingProgress(AdbQrPairingStage Stage, string? Detail = null);

/// <summary>
/// QR pairing outcome.
/// <para>扫码配对结果。</para>
/// </summary>
/// <param name="Route">The route the pairing completed through.
/// <para>配对完成的路径。</para></param>
/// <param name="Address">The connected device address (<c>ip:connectPort</c>).
/// <para>已连接的设备地址（<c>ip:connectPort</c>）。</para></param>
public sealed record AdbQrPairingOutcome(AdbQrPairingRoute Route, string Address);

/// <summary>
/// Orchestrates wireless-debugging QR pairing: waits for the pairing service the phone
/// advertises after scanning the QR code, pairs with the one-time password (through the
/// local adb server when one listens, otherwise the toolbox pairs directly), then waits
/// for the device's secure connect service and connects.
/// <para>编排无线调试扫码配对：等待手机扫描二维码后广播的配对服务，用一次性密码完成配对
/// （本机 adb server 在监听时经 server 配对，否则由工具箱直接配对），随后等待设备的安全
/// 连接服务并建立连接。</para>
/// </summary>
/// <remarks>
/// <para>While an adb server listens, the pairing endpoint is read from that server's own mDNS table —
/// the same source the <c>adb pair</c> command line uses — and from the local multicast browser at the
/// same time; whichever resolves the expected instance first is used. The post-pairing connection then
/// combines every source instead of trusting one: the server's target list under either its
/// <c>ip:port</c> or DNS-SD instance identity, the server's mDNS table, the connect instances collected
/// during pairing and a live local browse, each candidate port being dialled explicitly. Without a server
/// only the local browser runs and its port is probed before it is handed back.</para>
/// <para>只要 adb server 在监听，配对端点就同时来自该 server 自身的 mDNS 表（也就是 <c>adb pair</c>
/// 命令行使用的同一来源）和本地组播浏览器，先解析出期望实例的一方被采用。配对后的连接随后综合所有来源，
/// 而不是只信任一个：server 目标列表中的 <c>ip:port</c> 身份与 DNS-SD 实例身份、server 的 mDNS 表、
/// 配对期间收集到的连接实例，以及一次实时本地浏览；每个候选端口都会被显式拨号。没有 server 时只运行本地
/// 浏览器，并在交回端口前先做一次探测。</para>
/// </remarks>
public static class AdbQrPairingService
{
    private static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ConnectDiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServerConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServerPollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ServerTableRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PairingRefreshTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace after pairing before every advertised secure connect instance is tried, not only the ones that
    /// identify the paired device, so a dual-stack responder that publishes the service under another address
    /// family or host name still yields a dialable port.
    /// <para>配对后先只试能标识出已配对设备的实例，超过该宽限期后连其余广播实例也一并尝试，使以另一地址族或主机名
    /// 发布该服务的双栈应答方仍能给出可拨号端口。</para>
    /// </summary>
    private static readonly TimeSpan ForeignPortFallbackDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Window of one post-pairing connect browse. The daemon re-advertises its TLS service on a new port after
    /// pairing, so the browse is repeated until the deadline instead of trusting the pre-pairing candidates.
    /// <para>配对后一次连接浏览的窗口。守护进程会在配对后以新端口重新广播其 TLS 服务，因此浏览会在期限内重复，
    /// 而不是只信任配对前的候选。</para>
    /// </summary>
    private static readonly TimeSpan ConnectBrowseWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Window of the unicast DNS-SD query sent to the device itself when multicast delivered no secure connect
    /// instance, covering networks where the phone's multicast answers are filtered.
    /// <para>组播没有交出任何安全连接实例时向设备自身发送的单播 DNS-SD 问询窗口，覆盖手机会被过滤组播应答的网络。</para>
    /// </summary>
    private static readonly TimeSpan UnicastQueryTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// Window of the browse performed only to pre-fill the manual port when no advertised port answered.
    /// <para>仅为预填手动端口而执行的一次浏览窗口，用于没有任何广播端口应答时。</para>
    /// </summary>
    private static readonly TimeSpan SuggestionBrowseWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a successful <c>host:connect:</c> reply is verified against the server's target list. The reply
    /// only proves that some socket accepted the connection, so a transport that never leaves <c>offline</c>
    /// must not be mistaken for the device's secure connect service.
    /// <para><c>host:connect:</c> 的成功应答要在 server 的目标列表里复核的时长。该应答只证明某个套接字接受了
    /// 连接，因此始终停留在 <c>offline</c> 的传输不能被当成设备的安全连接服务。</para>
    /// </summary>
    private static readonly TimeSpan ConnectVerifyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Spacing between target-list polls while a fresh explicit connection is verified.
    /// <para>复核新建显式连接时两次目标列表轮询之间的间隔。</para>
    /// </summary>
    private static readonly TimeSpan ConnectVerifyInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Spacing between TCP probes of an advertised secure connect port that does not answer yet, while the
    /// daemon finishes restarting its TLS service after pairing.
    /// <para>广播出的安全连接端口尚未应答时，两次 TCP 探测之间的间隔；此时守护进程可能仍在重启其 TLS 服务。</para>
    /// </summary>
    private static readonly TimeSpan DirectPortRetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// First port of the range probed when the device advertises no secure connect service: adbd allocates its TLS
    /// listener from the Linux ephemeral range, whose Android default starts here.
    /// <para>设备没有广播安全连接服务时探测范围的起始端口：adbd 从 Linux 临时端口范围分配其 TLS 监听端口，Android 的
    /// 默认范围从此处开始。</para>
    /// </summary>
    private const int SecurePortScanFirst = 32768;

    /// <summary>
    /// Last port of the probed range, i.e. the Android default upper bound of the ephemeral range.
    /// <para>探测范围的末个端口，即 Android 临时端口范围的默认上界。</para>
    /// </summary>
    private const int SecurePortScanLast = 60999;

    /// <summary>
    /// Probes in flight while the range is scanned; the daemon's own listener answers in one round trip, so the
    /// remaining budget is what the parallelism is spent on.
    /// <para>扫描范围时在途的探测数；守护进程自身的监听器在一个往返内应答，并发度实际上花在剩余预算上。</para>
    /// </summary>
    private const int SecurePortScanConcurrency = 768;

    /// <summary>
    /// Deadline of one plaintext probe; a port that neither accepts nor refuses by then counts as closed.
    /// <para>单次明文探测的期限；届时既未接受也未拒绝的端口按关闭处理。</para>
    /// </summary>
    private static readonly TimeSpan SecurePortProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Deadline of the whole range scan, so a device that answers nothing cannot stall the pairing flow.
    /// <para>整个范围扫描的期限，使完全不应答的设备无法拖住配对流程。</para>
    /// </summary>
    private static readonly TimeSpan SecurePortScanBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Deadline of the ADB handshake that confirms a scanned port really is the daemon's TLS service.
    /// <para>确认被扫描端口确实是守护进程 TLS 服务的 ADB 握手期限。</para>
    /// </summary>
    private static readonly TimeSpan SecurePortHandshakeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Plaintext connect deadline of one scanned port, shorter than the handshake deadline because the range scan
    /// already proved the port accepts a connection.
    /// <para>单个被扫描端口的明文连接期限；范围扫描已证明该端口接受连接，因此该期限短于握手期限。</para>
    /// </summary>
    private const int SecurePortConnectTimeoutMs = 1500;

    /// <summary>
    /// Runs the pairing flow for the supplied one-time credentials. The phone must scan
    /// the QR code built from these credentials before the discovery timeout elapses.
    /// <para>为给定一次性凭据执行配对流程。手机必须在发现超时之前扫描由这些凭据构建的二维码。</para>
    /// </summary>
    /// <param name="credentials">The one-time QR credentials. <para>一次性二维码凭据。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度回调。</para></param>
    /// <param name="discoveryTimeout">Deadline for the phone to appear; defaults to
    /// 3 minutes. <para>手机出现的期限；默认 3 分钟。</para></param>
    /// <param name="cancellationToken">Cancels discovery, pairing and connecting.
    /// <para>取消发现、配对与连接。</para></param>
    /// <returns>The pairing outcome with the connected address.
    /// <para>携带已连接地址的配对结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> is <c>null</c>.
    /// <para><paramref name="credentials"/> 为 <c>null</c>。</para></exception>
    /// <exception cref="ArgumentException">A credential value is empty or contains a QR token delimiter,
    /// a control character, or a dot in the service name.
    /// <para>凭据值为空，或含二维码分隔符、控制字符，或服务名含点号。</para></exception>
    /// <exception cref="TimeoutException">The phone did not advertise the expected pairing
    /// service in time. <para>手机未在期限内广播期望的配对服务。</para></exception>
    /// <exception cref="AdbQrPairingConnectPortException">Pairing succeeded but the device never
    /// advertised its secure connect service; the caller can still complete the connection with
    /// the port shown on the phone. <para>配对成功但设备始终未广播安全连接服务；调用方仍可用手机上
    /// 显示的端口完成连接。</para></exception>
    /// <exception cref="IOException">The pairing protocol or the server command failed, or
    /// <see cref="DebugBridgeRouteMode.ServerOnly"/> is fixed and no server is listening.
    /// <para>配对协议或 server 命令失败，或已固定 <see cref="DebugBridgeRouteMode.ServerOnly"/> 但没有 server 在监听。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    /// <remarks>
    /// <para>When the server reports that it could not even start its pairing client, the endpoint is
    /// resolved once more from the live browser and the pairing is completed in-process with this host's
    /// key, which is the same user identity the server offers; the server then makes the connection.
    /// <see cref="DebugBridgeRouteMode.ServerOnly"/> keeps the server's failure instead, because that mode
    /// forbids pairing outside the server.</para>
    /// <para>server 报告它甚至无法启动配对客户端时，会再从实时浏览重新解析端点，并用本主机的密钥在进程内完成
    /// 配对——这正是 server 提供的同一用户身份——随后仍由 server 建立连接。固定
    /// <see cref="DebugBridgeRouteMode.ServerOnly"/> 时保留 server 的失败，因为该模式禁止绕过 server 配对。</para>
    /// </remarks>
    public static async Task<AdbQrPairingOutcome> PairAsync(AdbQrPairingCredentials credentials,
        IProgress<AdbQrPairingProgress>? progress = null, TimeSpan? discoveryTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _ = AdbQrPairing.BuildPayload(credentials);
        var timeout = discoveryTimeout ?? DefaultDiscoveryTimeout;
        var useServer = DebugBridgeRoute.UseAdbServer();
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.ServerOnly && !useServer)
        {
            throw new IOException("The ServerOnly route mode requires a listening ADB server.");
        }

        var snapshot = await DiscoverPairingAsync(useServer, credentials.ServiceName, timeout, cancellationToken)
            .ConfigureAwait(false);
        var pairing = snapshot.Pairing ?? throw new TimeoutException(
                $"The device did not advertise the pairing service '{credentials.ServiceName}'. Make sure " +
                "Wireless debugging is enabled and the QR code was scanned.");
        var host = pairing.Addresses.Count > 0
            ? pairing.Addresses[0]
            : throw new IOException(
                $"The pairing service '{credentials.ServiceName}' was advertised without a resolvable address.");

        var foundAt = DebugBridgeNetworkAddress.FormatEndpoint(host, pairing.Port);
        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.DeviceFound, foundAt));
        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Pairing, foundAt));

        if (useServer)
        {
            try
            {
                var address = await PairThroughServerAsync(credentials,
                    new AdbPairingEndpoint(host, pairing.HostName), pairing.Port, snapshot.ConnectCandidates,
                    progress, cancellationToken).ConfigureAwait(false);
                return new AdbQrPairingOutcome(AdbQrPairingRoute.AdbServer, address);
            }
            catch (AdbServerPairingClientException) when (AllowsInProcessPairing(DebugBridgeRoute.Mode))
            {
                pairing = await RefreshPairingEndpointAsync(credentials.ServiceName, pairing, cancellationToken)
                    .ConfigureAwait(false);
                host = pairing.Addresses[0];
                var retryAt = DebugBridgeNetworkAddress.FormatEndpoint(host, pairing.Port);
                progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Pairing, retryAt));
                _ = await AdbPairingConnection.PairAsync(
                        host, pairing.Port, credentials.Password, cancellationToken).ConfigureAwait(false);
                var address = await ConnectThroughServerAsync(new AdbPairingEndpoint(host, pairing.HostName),
                    snapshot.ConnectCandidates, progress, cancellationToken).ConfigureAwait(false);
                return new AdbQrPairingOutcome(AdbQrPairingRoute.AdbServer, address);
            }
        }

        string deviceGuid;
        try
        {
            deviceGuid = await AdbPairingConnection.PairAsync(
                    host, pairing.Port, credentials.Password, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsConnectFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            var refreshed = await RefreshPairingEndpointAsync(credentials.ServiceName, pairing, cancellationToken)
                .ConfigureAwait(false);
            var refreshedHost = refreshed.Addresses.Count > 0 ? refreshed.Addresses[0] : host;
            if (refreshedHost == host && refreshed.Port == pairing.Port)
            {
                throw;
            }

            host = refreshedHost;
            pairing = refreshed;
            progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Pairing,
                DebugBridgeNetworkAddress.FormatEndpoint(host, pairing.Port)));
            deviceGuid = await AdbPairingConnection.PairAsync(
                    host, pairing.Port, credentials.Password, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Connecting,
            DebugBridgeNetworkAddress.FormatHost(host)));
        var connectPort = await ResolveDirectConnectPortAsync(
            new AdbPairingEndpoint(host, pairing.HostName, deviceGuid), snapshot.ConnectCandidates,
            cancellationToken).ConfigureAwait(false);
        return new AdbQrPairingOutcome(AdbQrPairingRoute.Direct,
            DebugBridgeNetworkAddress.FormatEndpoint(host, connectPort));
    }

    /// <summary>
    /// Reports whether a pairing failure happened while opening the TCP connection, which a repeated scan that
    /// moved the pairing service to a new port produces; a TLS or SPAKE2 failure is not retried this way.
    /// <para>判断配对失败是否发生在建立 TCP 连接时——重复扫码使配对服务换到新端口时会出现该失败；TLS 或 SPAKE2
    /// 失败不会以此方式重试。</para>
    /// </summary>
    /// <param name="exception">The failure raised by the direct pairing client.
    /// <para>直连配对客户端抛出的失败。</para></param>
    /// <returns><see langword="true"/> when the socket connect itself failed.
    /// <para>套接字连接本身失败时为 <see langword="true"/>。</para></returns>
    private static bool IsConnectFailure(IOException exception) =>
        exception.InnerException is SocketException;

    /// <summary>
    /// Reports whether the fixed route mode permits completing a pairing in-process after the adb server's
    /// pairing client failed; <see cref="DebugBridgeRouteMode.ServerOnly"/> forbids it.
    /// <para>报告固定路由模式是否允许在 adb server 的配对客户端失败后改为进程内配对；
    /// <see cref="DebugBridgeRouteMode.ServerOnly"/> 禁止这样做。</para>
    /// </summary>
    /// <param name="mode">The fixed route mode. <para>固定的路由模式。</para></param>
    /// <returns><see langword="true"/> for every mode except <see cref="DebugBridgeRouteMode.ServerOnly"/>.
    /// <para>除 <see cref="DebugBridgeRouteMode.ServerOnly"/> 以外的模式均返回 <see langword="true"/>。</para></returns>
    internal static bool AllowsInProcessPairing(DebugBridgeRouteMode mode) =>
        mode != DebugBridgeRouteMode.ServerOnly;

    /// <summary>
    /// Resolves the pairing endpoint once more from the live local browser, which queries the network instead
    /// of reading the server's cached table, and falls back to <paramref name="current"/> when the device does
    /// not answer within <see cref="PairingRefreshTimeout"/> or advertises no address.
    /// <para>再从实时本地浏览器解析一次配对端点（它查询网络而不是读取 server 缓存表）；设备在
    /// <see cref="PairingRefreshTimeout"/> 内未应答或未广播地址时回退到 <paramref name="current"/>。</para>
    /// </summary>
    /// <param name="serviceName">The expected pairing instance label from the QR payload.
    /// <para>二维码负载中期望的配对实例标签。</para></param>
    /// <param name="current">The endpoint resolved by the first discovery pass.
    /// <para>第一轮发现解析出的端点。</para></param>
    /// <param name="cancellationToken">Cancels the browse. <para>取消浏览。</para></param>
    /// <returns>The refreshed endpoint, or <paramref name="current"/> when nothing better resolved.
    /// <para>刷新后的端点；没有更好的结果时为 <paramref name="current"/>。</para></returns>
    private static async Task<MdnsDevice> RefreshPairingEndpointAsync(string serviceName, MdnsDevice current,
        CancellationToken cancellationToken)
    {
        var refreshed = await AdbMdnsDiscovery.WaitForPairingServiceAsync(
                serviceName, PairingRefreshTimeout, cancellationToken).ConfigureAwait(false);
        return refreshed is { Addresses.Count: > 0 } ? refreshed : current;
    }

    /// <summary>
    /// Finds the pairing advertisement through both sources at once: the adb server's mDNS table, which
    /// is authoritative while a server listens and already holds resolved endpoints, and the local
    /// multicast browser, which works without a server. The browser also collects the device's secure
    /// connect instances in the same session, so the post-pairing connect port is usually already known.
    /// <para>同时从两个来源查找配对广播：adb server 的 mDNS 表（有 server 在监听时权威，且已解析端点）与本地
    /// 组播浏览器（无 server 也能工作）。浏览器还会在同一会话中一并收集该设备的安全连接实例，使配对后的连接端口
    /// 通常已经就绪。</para>
    /// </summary>
    /// <remarks>Once one source resolves the expected instance, the other browse is cancelled and observed,
    /// so a server hit returns without waiting for the local browser's full window.
    /// <para>一旦某个来源解析出期望实例，另一个浏览会被取消并观察，使 server 命中无需等待本地浏览器跑满窗口。</para></remarks>
    /// <param name="useServer">Whether the server table should be polled alongside the local browser.
    /// <para>是否在本地浏览的同时轮询 server 的表。</para></param>
    /// <param name="serviceName">The expected pairing instance label from the QR payload.
    /// <para>二维码负载中期望的配对实例标签。</para></param>
    /// <param name="timeout">Window given to both sources. <para>两个来源共用的窗口。</para></param>
    /// <param name="cancellationToken">Cancels both sources. <para>取消两个来源。</para></param>
    /// <returns>The pairing instance and the connect candidates collected with it.
    /// <para>配对实例以及随之收集到的连接候选。</para></returns>
    private static async Task<AdbPairingSnapshot> DiscoverPairingAsync(bool useServer, string serviceName,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var raced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var browserTask = AdbMdnsDiscovery.WaitForPairingSnapshotAsync(serviceName, timeout, raced.Token);
        if (!useServer)
        {
            return await browserTask.ConfigureAwait(false);
        }

        var serverTask = WaitForServerPairingAsync(serviceName, timeout, raced.Token);
        try
        {
            var finished = await Task.WhenAny(browserTask, serverTask).ConfigureAwait(false);
            var resolved = await finished.ConfigureAwait(false);
            if (resolved.Pairing is not null)
            {
                raced.Cancel();
                return resolved;
            }

            var other = await (ReferenceEquals(finished, browserTask) ? serverTask : browserTask)
                .ConfigureAwait(false);
            return other.Pairing is not null ? other : resolved;
        }
        finally
        {
            raced.Cancel();
            await IgnoreCompletionAsync(browserTask).ConfigureAwait(false);
            await IgnoreCompletionAsync(serverTask).ConfigureAwait(false);
        }
    }

    private static async Task<AdbPairingSnapshot> WaitForServerPairingAsync(string serviceName, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lookup = await AdbServerMdns.WaitForAsync(
                entry => AdbServerMdns.IsType(entry, AdbMdnsDiscovery.AdbTlsPairingService) &&
                         AdbMdnsDiscovery.MatchesInstanceLabel(entry.Instance, serviceName),
                timeout, cancellationToken).ConfigureAwait(false);
        return new AdbPairingSnapshot(lookup.Entry is null ? null : AdbServerMdns.ToDevice(lookup.Entry), []);
    }

    /// <summary>
    /// Observes a losing discovery task so its cancellation or fault cannot escape the race.
    /// <para>观察竞速中落败的发现任务，避免其取消或故障逸出竞速。</para>
    /// </summary>
    /// <param name="task">The task to observe. <para>待观察的任务。</para></param>
    /// <returns>A task that completes after the observed task settles. <para>被观察任务结束后完成的任务。</para></returns>
    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<string> PairThroughServerAsync(AdbQrPairingCredentials credentials,
        AdbPairingEndpoint endpoint, int pairingPort, IReadOnlyList<MdnsDevice> connectCandidates,
        IProgress<AdbQrPairingProgress>? progress, CancellationToken cancellationToken)
    {
        await AdbServerServices.PairAsync(endpoint.Host, pairingPort, credentials.Password,
            DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort,
            cancellationToken).ConfigureAwait(false);
        return await ConnectThroughServerAsync(endpoint, connectCandidates, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects the already paired device through the adb server: an existing target is reused whatever
    /// identity the server keyed it under, and otherwise the server's own auto-connect races an explicit
    /// connect whose first source is the server's freshly re-read mDNS table.
    /// <para>通过 adb server 连接已配对设备：已有目标无论以何种身份被 server 键控都直接复用，否则让 server
    /// 自身的自动连接与显式连接竞速，后者的首选来源是重新读取的 server mDNS 表。</para>
    /// </summary>
    /// <remarks>
    /// <para>A server that auto-connects an mDNS device keys its transport by the DNS-SD instance name
    /// (<c>adb-…._adb-tls-connect._tcp</c>), not by <c>ip:port</c>, so a target is recognized through the
    /// server's mDNS table instead of a serial prefix. The table is also what supplies the explicit connect
    /// port, which keeps working when the server did not auto-connect.</para>
    /// <para>自动连接 mDNS 设备的 server 以 DNS-SD 实例名（<c>adb-…._adb-tls-connect._tcp</c>）而不是
    /// <c>ip:port</c> 作为传输键，因此目标通过 server 的 mDNS 表识别，而不是按串号前缀。该表同时提供显式连接
    /// 所需的端口，server 未自动连接时仍可完成连接。</para>
    /// <para>The post-pairing steps follow the upstream command line: read <c>host:mdns:services</c> again after
    /// pairing and connect the device's <c>_adb-tls-connect</c> row, instead of reusing a port collected before
    /// pairing.</para>
    /// <para>配对后的步骤与上游命令行一致：配对后重新读取 <c>host:mdns:services</c> 并连接设备的
    /// <c>_adb-tls-connect</c> 行，而不是复用配对前收集到的端口。</para>
    /// </remarks>
    /// <param name="endpoint">The paired device identity resolved during pairing.
    /// <para>配对阶段解析出的设备身份。</para></param>
    /// <param name="connectCandidates">Connect instances collected while waiting for the pairing instance; used
    /// only after the server table and a live browse produced nothing.
    /// <para>等待配对实例期间收集到的连接实例；仅在 server 表与实时浏览都没有结果后使用。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度回调。</para></param>
    /// <param name="cancellationToken">Cancels the connect attempts. <para>取消连接尝试。</para></param>
    /// <returns>The server's <c>host:port</c> target for the device.
    /// <para>设备在 server 侧的 <c>host:port</c> 目标。</para></returns>
    /// <exception cref="AdbQrPairingConnectPortException">No source advertised the secure connect service and
    /// the server never connected the device. <para>没有任何来源广播安全连接服务且 server 始终未连接该设备。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    private static async Task<string> ConnectThroughServerAsync(AdbPairingEndpoint endpoint,
        IReadOnlyList<MdnsDevice> connectCandidates, IProgress<AdbQrPairingProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Connecting,
            DebugBridgeNetworkAddress.FormatHost(endpoint.Host)));
        var existing = await FindServerConnectionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Address;
        }

        using var raced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var autoConnected = WaitForServerConnectionAsync(endpoint, ServerConnectTimeout, raced.Token);
        var explicitConnect = ConnectThroughAdvertisedPortsAsync(endpoint, connectCandidates, raced.Token);
        try
        {
            var finished = await Task.WhenAny(autoConnected, explicitConnect).ConfigureAwait(false);
            var address = await finished.ConfigureAwait(false);
            if (address is not null)
            {
                raced.Cancel();
                return address;
            }

            var other = await (ReferenceEquals(finished, autoConnected) ? explicitConnect : autoConnected)
                .ConfigureAwait(false);
            if (other is not null)
            {
                return other;
            }
        }
        finally
        {
            raced.Cancel();
            await IgnoreCompletionAsync(autoConnected).ConfigureAwait(false);
            await IgnoreCompletionAsync(explicitConnect).ConfigureAwait(false);
        }

        var suggestion = await SuggestConnectPortAsync(endpoint, connectCandidates, cancellationToken)
            .ConfigureAwait(false);
        throw new AdbQrPairingConnectPortException(AdbQrPairingRoute.AdbServer, endpoint.Host,
            $"The device did not become a connected ADB target and did not advertise its secure connect " +
            $"service (no '{AdbMdnsDiscovery.AdbTlsService}' entry for {endpoint.Host}).", suggestion);
    }

    /// <summary>
    /// Re-reads the server's mDNS table and connects the device's current <c>_adb-tls-connect</c> rows, the two
    /// steps upstream performs with <c>adb mdns services</c> followed by <c>adb connect</c>.
    /// <para>重新读取 server 的 mDNS 表并连接设备当前的 <c>_adb-tls-connect</c> 行，即上游使用
    /// <c>adb mdns services</c> 再加 <c>adb connect</c> 的两步。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="attempted">Ports already dialled in this flow. <para>本流程中已经拨过的端口。</para></param>
    /// <param name="cancellationToken">Cancels the query and the connect attempts.
    /// <para>取消查询与连接尝试。</para></param>
    /// <returns>The connected endpoint, or <c>null</c> when the table listed no usable row.
    /// <para>已连接的端点；表中没有可用行时为 <c>null</c>。</para></returns>
    private static async Task<string?> TryConnectServerTableAsync(AdbPairingEndpoint endpoint,
        HashSet<int> attempted, CancellationToken cancellationToken)
    {
        var table = await QueryConnectTableAsync(cancellationToken).ConfigureAwait(false);
        return await TryConnectPortsAsync(endpoint, SelectTablePorts(table, endpoint.Host), attempted,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls the server until it lists the paired device under any identity or the deadline passes.
    /// <para>轮询 server，直到它以任一身份列出已配对设备或期限到期。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="timeout">Overall poll deadline. <para>轮询期限。</para></param>
    /// <param name="cancellationToken">Cancels the loop and each query.
    /// <para>取消循环与每次查询。</para></param>
    /// <returns>The dialable address, or <c>null</c> when the server never listed the device.
    /// <para>可拨号地址；server 始终未列出该设备时为 <c>null</c>。</para></returns>
    private static async Task<string?> WaitForServerConnectionAsync(AdbPairingEndpoint endpoint, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var table = await QueryConnectTableAsync(cancellationToken).ConfigureAwait(false);
        var nextTableRefresh = DateTime.UtcNow + ServerTableRefreshInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = await ListServerTargetsAsync(cancellationToken).ConfigureAwait(false);
            var connection = targets is null ? null : MatchServerConnection(targets, table, endpoint.Host);
            if (connection is not null)
            {
                return connection.Address;
            }

            var now = DateTime.UtcNow;
            var remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            if (now >= nextTableRefresh)
            {
                table = await QueryConnectTableAsync(cancellationToken).ConfigureAwait(false);
                nextTableRefresh = now + ServerTableRefreshInterval;
            }

            await Task.Delay(remaining > ServerPollInterval ? ServerPollInterval : remaining, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<AdbServerConnection?> FindServerConnectionAsync(AdbPairingEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var targets = await ListServerTargetsAsync(cancellationToken).ConfigureAwait(false);
        if (targets is null)
        {
            return null;
        }

        var table = await QueryConnectTableAsync(cancellationToken).ConfigureAwait(false);
        return MatchServerConnection(targets, table, endpoint.Host);
    }

    private static async Task<IReadOnlyList<AdbTargetInfo>?> ListServerTargetsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var services = new AdbServerServices(string.Empty, DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort);
            return await Task.Run(services.ListTargets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the server target representing the paired device. A target keyed by a dialable <c>host:port</c>
    /// for that host wins, then a target keyed by a DNS-SD connect instance whose server mDNS row resolves to
    /// that host; <c>device</c> targets are preferred over any other non-offline state.
    /// <para>选出代表已配对设备的 server 目标：先取以该主机的可拨号 <c>host:port</c> 为键的目标，再取以
    /// DNS-SD 连接实例为键、且其 server mDNS 行解析到该主机的目标；<c>device</c> 状态优先于其它非离线状态。</para>
    /// </summary>
    /// <param name="targets">Rows of the server's current <c>host:devices-l</c> listing.
    /// <para>server 当前 <c>host:devices-l</c> 列表的行。</para></param>
    /// <param name="mdnsEntries">Rows of the server's mDNS table, empty when it exposes none.
    /// <para>server mDNS 表的行；未提供该表时为空。</para></param>
    /// <param name="host">The device address resolved during pairing.
    /// <para>配对阶段解析出的设备地址。</para></param>
    /// <returns>The matched target and its dialable address, or <c>null</c> when the device is absent.
    /// <para>匹配到的目标及其可拨号地址；设备不在列表时为 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.
    /// <para>某个参数为 <c>null</c>。</para></exception>
    internal static AdbServerConnection? MatchServerConnection(IReadOnlyList<AdbTargetInfo> targets,
        IReadOnlyList<AdbServerMdnsEntry> mdnsEntries, string host)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(mdnsEntries);
        var aliases = AdbServerMdns.BuildConnectAliases(mdnsEntries,
            [AdbMdnsDiscovery.AdbTlsConnectTypeName]);
        return Match(requireDevice: true) ?? Match(requireDevice: false);

        AdbServerConnection? Match(bool requireDevice)
        {
            AdbServerConnection? instanceMatch = null;
            foreach (var target in targets)
            {
                if (string.Equals(target.State, "offline", StringComparison.OrdinalIgnoreCase) ||
                    target.State.Length == 0 ||
                    (requireDevice && !string.Equals(target.State, "device", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (DebugBridgeNetworkAddress.TrySplitEndpoint(target.Serial, out var serialHost, out _) &&
                    IsSameHost(serialHost, host))
                {
                    return new AdbServerConnection(target.Serial, target.Serial);
                }

                var alias = AdbServerMdns.ResolveConnectAlias(aliases, target.Serial,
                    [AdbMdnsDiscovery.AdbTlsConnectTypeName]);
                if (alias is not null &&
                    DebugBridgeNetworkAddress.TrySplitEndpoint(alias, out var aliasHost, out _) &&
                    IsSameHost(aliasHost, host))
                {
                    instanceMatch ??= new AdbServerConnection(target.Serial, alias);
                }
            }

            return instanceMatch;
        }
    }

    /// <summary>
    /// Connects the secure port the device currently advertises, combining the sources in the order upstream
    /// uses them: the server's freshly re-read mDNS table first, then repeated local browses, and only then the
    /// connect instances collected before pairing. The table and the live browse are re-read every pass because
    /// the daemon re-advertises its TLS service on a new port after pairing.
    /// <para>连接设备当前广播的安全端口，来源顺序与上游一致：先重新读取 server 的 mDNS 表，其次反复进行本地浏览，
    /// 最后才用配对前收集到的连接实例。表与实时浏览每一轮都会重读，因为守护进程会在配对后以新端口重新广播 TLS 服务。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="connectCandidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="cancellationToken">Cancels probing, connecting and browsing.
    /// <para>取消探测、连接与浏览。</para></param>
    /// <returns>The connected endpoint, or <c>null</c> when no advertised port accepted the connection.
    /// <para>已连接的端点；没有广播端口接受连接时为 <c>null</c>。</para></returns>
    private static async Task<string?> ConnectThroughAdvertisedPortsAsync(AdbPairingEndpoint endpoint,
        IReadOnlyList<MdnsDevice> connectCandidates, CancellationToken cancellationToken)
    {
        var attempted = new HashSet<int>();
        var deadline = DateTime.UtcNow + ConnectDiscoveryTimeout;
        var pairingCandidatesTried = false;
        var nextTableQuery = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= nextTableQuery)
            {
                if (await TryConnectServerTableAsync(endpoint, attempted, cancellationToken).ConfigureAwait(false)
                    is { } tableAddress)
                {
                    return tableAddress;
                }

                nextTableQuery = now + ServerTableRefreshInterval;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var discovered = await AdbMdnsDiscovery.CollectConnectServicesAsync(
                remaining > ConnectBrowseWindow ? ConnectBrowseWindow : remaining,
                device => IsSameDevice(device, endpoint.Host, endpoint.HostName), cancellationToken)
                .ConfigureAwait(false);
            var ports = SelectConnectPorts(discovered, endpoint.Host, endpoint.HostName);
            if (await TryConnectPortsAsync(endpoint, ports, attempted, cancellationToken).ConfigureAwait(false)
                is { } discoveredAddress)
            {
                return discoveredAddress;
            }

            if (!pairingCandidatesTried)
            {
                pairingCandidatesTried = true;
                var candidates = SelectConnectPorts(connectCandidates, endpoint.Host, endpoint.HostName);
                if (await TryConnectPortsAsync(endpoint, candidates, attempted, cancellationToken)
                    .ConfigureAwait(false) is { } candidateAddress)
                {
                    return candidateAddress;
                }
            }

            await Task.Delay(remaining > DirectPortRetryInterval ? DirectPortRetryInterval : remaining,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> TryConnectPortsAsync(AdbPairingEndpoint endpoint,
        IReadOnlyList<int> ports, HashSet<int> attempted, CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            if (!attempted.Add(port))
            {
                continue;
            }

            var probe = await DebugBridgeRoute.TryProbeAsync(endpoint.Host, port, cancellationToken)
                .ConfigureAwait(false);
            if (probe == DebugBridgeProbeOutcome.Refused)
            {
                CommTrace.Debug("adb-qr-pairing",
                    $"advertised connect port {endpoint.Host}:{port} refused the probe; trying the next source.");
                continue;
            }

            if (await ConnectServerEndpointAsync(endpoint.Host, port, cancellationToken).ConfigureAwait(false)
                is { } address)
            {
                return address;
            }
        }

        return null;
    }

    private static async Task<string?> ConnectServerEndpointAsync(string host, int port,
        CancellationToken cancellationToken)
    {
        var endpoint = DebugBridgeNetworkAddress.FormatEndpoint(host, port);
        try
        {
            await AdbServerServices.ConnectAsync(endpoint, DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            CommTrace.Debug("adb-qr-pairing", $"explicit connect to {endpoint} failed: {ex.Message}");
            return null;
        }

        if (await WaitForExplicitTargetAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            return endpoint;
        }

        CommTrace.Debug("adb-qr-pairing",
            $"explicit connect to {endpoint} was accepted but the server never listed it as a usable target.");
        return null;
    }

    /// <summary>
    /// Polls the server's target list until the freshly dialled <c>host:port</c> leaves <c>offline</c>. The
    /// connect reply only proves that a socket accepted the connection, so a port that answers with a
    /// non-ADB service must not be reported as the device's secure connect endpoint.
    /// <para>轮询 server 目标列表，直到刚拨号的 <c>host:port</c> 脱离 <c>offline</c>。连接应答只证明某个套接字
    /// 接受了连接，因此以非 ADB 服务应答的端口不能被报告为设备的安全连接端点。</para>
    /// </summary>
    /// <param name="endpoint">The dialled <c>host:port</c> identity. <para>已拨号的 <c>host:port</c> 标识。</para></param>
    /// <param name="cancellationToken">Cancels the polling. <para>取消轮询。</para></param>
    /// <returns><see langword="true"/> when the server listed the endpoint as a non-offline target.
    /// <para>server 已把该端点列为非离线目标时为 <see langword="true"/>。</para></returns>
    private static async Task<bool> WaitForExplicitTargetAsync(string endpoint, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ConnectVerifyTimeout;
        while (true)
        {
            var targets = await ListServerTargetsAsync(cancellationToken).ConfigureAwait(false);
            if (targets is not null && targets.Any(target =>
                    string.Equals(target.Serial, endpoint, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target.State, "offline", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining > ConnectVerifyInterval ? ConnectVerifyInterval : remaining,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<AdbServerMdnsEntry>> QueryConnectTableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await AdbServerMdns.QueryAsync(DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort, cancellationToken).ConfigureAwait(false);
            return query.Status == AdbServerMdnsStatus.Answered ? query.Entries : [];
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            return [];
        }
    }

    private static IReadOnlyList<int> SelectTablePorts(IReadOnlyList<AdbServerMdnsEntry> entries, string host)
        => [.. entries
            .Where(entry => AdbServerMdns.IsType(entry, AdbMdnsDiscovery.AdbTlsConnectTypeName) &&
                            IsSameHost(entry.Host, host))
            .Select(entry => entry.Port)
            .Where(port => port is >= 1 and <= 65535)
            .Distinct()];

    /// <summary>
    /// Pre-fills the manual port with the freshest advertisement the device currently publishes, falling back
    /// to the pre-pairing candidates and then to the server's table. The snapshot alone is not used first: the
    /// daemon re-advertises on a new port after pairing, so the pre-pairing value is the stale one.
    /// <para>用设备当前广播的最新端口预填手动端口，其次回退到配对前的候选，最后回退到 server 的表。不先使用
    /// 配对前的快照：守护进程会在配对后以新端口重新广播，配对前的值才是过期的。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="connectCandidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="cancellationToken">Cancels the browse. <para>取消浏览。</para></param>
    /// <returns>The suggested port, or zero when nothing is known. <para>建议端口；没有任何信息时为 0。</para></returns>
    private static async Task<int> SuggestConnectPortAsync(AdbPairingEndpoint endpoint,
        IReadOnlyList<MdnsDevice> connectCandidates, CancellationToken cancellationToken)
    {
        var discovered = await AdbMdnsDiscovery.CollectConnectServicesAsync(SuggestionBrowseWindow,
                device => IsSameDevice(device, endpoint.Host, endpoint.HostName), cancellationToken)
            .ConfigureAwait(false);
        var fresh = SelectConnectPort(discovered, endpoint.Host, endpoint.HostName);
        if (fresh != 0)
        {
            return fresh;
        }

        var snapshotPort = SelectConnectPort(connectCandidates, endpoint.Host, endpoint.HostName);
        if (snapshotPort != 0)
        {
            return snapshotPort;
        }

        var table = await QueryConnectTableAsync(cancellationToken).ConfigureAwait(false);
        return SelectTablePorts(table, endpoint.Host).FirstOrDefault();
    }

    /// <summary>
    /// Resolves the secure connect port for a device paired without an adb server, where the tool must dial the daemon
    /// itself. The device identity is the instance name adbd registered (<c>persist.adb.wifi.guid</c>, returned by the
    /// pairing exchange), so that instance's exact SRV record is queried on every pass instead of waiting for a PTR
    /// announcement; a port advertised by an instance that does not carry the label is still tried against the paired
    /// address after a short grace, because a dual-stack responder can answer under its own name.
    /// <para>为无 adb server 时配对的设备解析安全连接端口，此时由工具箱自己拨号。设备身份是 adbd 注册的实例名
    /// （<c>persist.adb.wifi.guid</c>，由配对交换返回），因此每轮直接查询该实例的精确 SRV 记录，而不是等待 PTR 广播；
    /// 未携带该标签的实例所广播的端口会在短暂宽限后仍按配对地址试探，因为双栈应答方可能以自身名称作答。</para>
    /// <para>The advertised lookup and a bounded scan of the device's ephemeral range run at the same time, so a daemon
    /// whose mDNS registration never happened still yields a port within the same wait instead of after it; the scan
    /// accepts a port only once it completes an ADB handshake.</para>
    /// <para>广播查找与设备临时端口范围的有界扫描同时进行，使 mDNS 注册从未成功的守护进程也在同一段等待内给出端口，
    /// 而不是在其之后；扫描只在端口完成 ADB 握手后才接受它。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="connectCandidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="cancellationToken">Cancels probing, browsing and scanning.
    /// <para>取消探测、浏览与扫描。</para></param>
    /// <returns>The listening secure connect port. <para>正在监听的安全连接端口。</para></returns>
    /// <exception cref="AdbQrPairingConnectPortException">Neither the advertisements nor the scan produced a port; the
    /// message names the instances, ports and scan result that were tried.
    /// <para>广播与扫描都没有给出端口；消息会列出尝试过的实例、端口与扫描结果。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    private static async Task<int> ResolveDirectConnectPortAsync(AdbPairingEndpoint endpoint,
        IReadOnlyList<MdnsDevice> connectCandidates, CancellationToken cancellationToken)
    {
        var known = new List<int>(SelectConnectPorts(connectCandidates, endpoint.Guid, endpoint.Host,
            endpoint.HostName));
        var observed = new List<string>();
        foreach (var port in known)
        {
            if (await IsListeningAsync(endpoint.Host, port, cancellationToken).ConfigureAwait(false))
            {
                return port;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var advertised = ResolveAdvertisedPortAsync(endpoint, known, observed, stop.Token);
        var scanned = ScanSecurePortsAsync(endpoint.Host, stop.Token);
        var pending = new List<Task> { advertised, scanned };
        var scan = default(SecurePortScan);
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            if (finished == advertised)
            {
                var port = await advertised.ConfigureAwait(false);
                if (port == 0)
                {
                    continue;
                }

                await stop.CancelAsync().ConfigureAwait(false);
                await scanned.ConfigureAwait(false);
                return port;
            }

            scan = await scanned.ConfigureAwait(false);
            if (scan.Port == 0)
            {
                continue;
            }

            await stop.CancelAsync().ConfigureAwait(false);
            await advertised.ConfigureAwait(false);
            return scan.Port;
        }

        cancellationToken.ThrowIfCancellationRequested();
        CommTrace.Debug("adb-qr-pairing",
            $"no secure connect port answered for {endpoint.Host}; known ports [{string.Join(",", known)}], " +
            $"advertised [{string.Join("; ", observed)}], scanned listening [{scan.Listening}].");
        throw new AdbQrPairingConnectPortException(AdbQrPairingRoute.Direct, endpoint.Host,
            $"The device did not answer on any advertised secure connect port for {endpoint.Host} " +
            $"(tried ports [{string.Join(",", known)}]; connect instances " +
            $"[{(observed.Count == 0 ? "none" : string.Join("; ", observed))}]; scanned ports " +
            $"{SecurePortScanFirst}-{SecurePortScanLast}: {scan.Listening} accepted a connection and none " +
            "completed the ADB handshake).",
            known.Count > 0 ? known[0] : scan.FirstListening);
    }

    /// <summary>
    /// Polls the device's advertisements until the secure connect service answers: the exact instance name adbd
    /// registered is queried on every pass, the local multicast browser and a unicast DNS-SD question to the device are
    /// both used, and every candidate port is probed before it is handed back.
    /// <para>轮询设备广播直到安全连接服务应答：每轮都查询 adbd 注册的精确实例名，同时使用本机组播浏览器与向设备发送的
    /// 单播 DNS-SD 问询，并在交回前探测每个候选端口。</para>
    /// </summary>
    /// <param name="endpoint">The paired device identity. <para>已配对设备的身份。</para></param>
    /// <param name="known">Candidate ports, extended with every newly advertised port.
    /// <para>候选端口，会随新广播的端口扩充。</para></param>
    /// <param name="observed">Descriptions of the instances seen, for the failure message.
    /// <para>见到的实例描述，用于失败消息。</para></param>
    /// <param name="cancellationToken">Cancels browsing and probing. <para>取消浏览与探测。</para></param>
    /// <returns>The answering port, or zero when the deadline passes or the caller cancels.
    /// <para>应答的端口；超过期限或调用方取消时为 0。</para></returns>
    private static async Task<int> ResolveAdvertisedPortAsync(AdbPairingEndpoint endpoint, List<int> known,
        List<string> observed, CancellationToken cancellationToken)
    {
        try
        {
            var startedAt = DateTime.UtcNow;
            var deadline = startedAt + ConnectDiscoveryTimeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var discovered = await AdbMdnsDiscovery.CollectConnectServicesAsync(
                        remaining > ConnectBrowseWindow ? ConnectBrowseWindow : remaining,
                        device => AdbMdnsDiscovery.DescribesDevice(device, endpoint.Guid, endpoint.Host),
                        cancellationToken, requireAddress: false, endpoint.Guid).ConfigureAwait(false);
                if (!discovered.Any(static device => AdbMdnsDiscovery.IsType(device, AdbMdnsDiscovery.AdbTlsService)))
                {
                    // Multicast delivered no instance at all: ask the device itself over unicast, which still works
                    // when Wi-Fi power save or AP isolation filters the phone's multicast answers.
                    var direct = await AdbMdnsDiscovery.QueryUnicastAsync(endpoint.Host, UnicastQueryTimeout,
                        cancellationToken, endpoint.Guid).ConfigureAwait(false);
                    if (direct.Count > 0)
                    {
                        discovered = [.. discovered, .. direct];
                    }
                }

                foreach (var device in discovered)
                {
                    var description = device.ToString();
                    if (!observed.Contains(description))
                    {
                        observed.Add(description);
                    }
                }

                var ports = SelectConnectPorts(discovered, endpoint.Guid, endpoint.Host, endpoint.HostName);
                if (DateTime.UtcNow - startedAt >= ForeignPortFallbackDelay)
                {
                    ports = [.. ports, .. SelectAdvertisedConnectPorts(discovered)];
                }

                foreach (var port in ports)
                {
                    if (!known.Contains(port))
                    {
                        known.Insert(0, port);
                    }
                }

                foreach (var port in known)
                {
                    if (await IsListeningAsync(endpoint.Host, port, cancellationToken).ConfigureAwait(false))
                    {
                        return port;
                    }
                }

                remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining > DirectPortRetryInterval ? DirectPortRetryInterval : remaining,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        return 0;
    }

    /// <summary>
    /// Finds the secure connect port by probing the device's ephemeral range, for a daemon that never advertised its
    /// TLS service. A port is accepted only after it completes an ADB handshake, so an unrelated listener on the same
    /// device is never mistaken for the daemon; the range scan stops at the first accepted port.
    /// <para>为始终未广播 TLS 服务的守护进程探测设备的临时端口范围以找出安全连接端口。只有完成 ADB 握手的端口才会
    /// 被接受，因此同一设备上的无关监听器不会被误认为守护进程；范围扫描在第一个被接受的端口处结束。</para>
    /// </summary>
    /// <param name="host">The paired device address. <para>已配对设备的地址。</para></param>
    /// <param name="cancellationToken">Cancels the scan and the handshakes. <para>取消扫描与握手。</para></param>
    /// <returns>The accepted port, how many ports accepted a connection, and the lowest of those; a default result
    /// when the budget expired or the caller canceled. <para>被接受的端口、接受过连接的端口数，以及其中最小的端口；预算
    /// 到期或调用方取消时返回默认结果。</para></returns>
    private static async Task<SecurePortScan> ScanSecurePortsAsync(string host,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SecurePortScanBudget);
        TcpPortScanResult result;
        try
        {
            result = await TcpPortScanner.ScanAsync(host, SecurePortScanFirst, SecurePortScanLast,
                    SecurePortScanConcurrency, SecurePortProbeTimeout,
                    (port, token) => IsSecureAdbServiceAsync(host, port, token), budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the scan budget expired or the caller stopped waiting for it; the advertised lookup reports the
            // outcome, and the caller re-checks its own token.
            CommTrace.Debug("adb-qr-pairing", $"secure connect port scan for {host} stopped early.");
            return default;
        }

        return new SecurePortScan(result.AcceptedPort, result.OpenPorts.Count,
            result.OpenPorts.Count > 0 ? result.OpenPorts[0] : 0);
    }

    /// <summary>
    /// Reports whether a port is adbd's secure service by completing one ADB connection handshake over it: the
    /// daemon answers the client's CNXN with A_STLS, upgrades to TLS and then accepts this host's key, which pairing
    /// registered. Any other listener fails the handshake.
    /// <para>在该端口上完成一次 ADB 连接握手以判断它是否为 adbd 的安全服务：守护进程以 A_STLS 应答客户端的 CNXN，
    /// 升级到 TLS 后接受本主机的密钥——即配对阶段登记的那一个。其他监听器都会握手失败。</para>
    /// </summary>
    /// <param name="host">The paired device address. <para>已配对设备的地址。</para></param>
    /// <param name="port">The port to identify. <para>待识别的端口。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消握手。</para></param>
    /// <returns><see langword="true"/> when the port completed the handshake.
    /// <para>该端口完成握手时为 <see langword="true"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    private static async Task<bool> IsSecureAdbServiceAsync(string host, int port,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SecurePortHandshakeTimeout);
        try
        {
            using var session = AdbSecureSessionFactory.Open(host, port, SecurePortConnectTimeoutMs);
            await session.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// What one range scan found: the port that completed an ADB handshake, how many ports accepted a connection,
    /// and the lowest of them for pre-filling a manual port.
    /// <para>一次范围扫描的结果：完成 ADB 握手的端口、接受过连接的端口数，以及其中最小的端口（用于预填手动端口）。</para>
    /// </summary>
    /// <param name="Port">The accepted port, or zero when no port completed a handshake.
    /// <para>被接受的端口；没有端口完成握手时为 0。</para></param>
    /// <param name="Listening">How many ports accepted a connection. <para>接受过连接的端口数。</para></param>
    /// <param name="FirstListening">The lowest listening port, or zero. <para>最小的监听端口；没有时为 0。</para></param>
    private readonly record struct SecurePortScan(int Port, int Listening, int FirstListening);

    /// <summary>
    /// Returns every secure connect port any instance advertised, newest first, regardless of which device it
    /// belongs to. Used only after the same-device match had its grace, so a dual-stack responder that publishes
    /// the paired device under another address family or host name still yields a dialable port.
    /// <para>按最新优先给出任意实例广播的安全连接端口，不区分归属设备。仅在按同一设备匹配的宽限期结束后使用，使以另一
    /// 地址族或主机名发布已配对设备的双栈应答方仍能给出可拨号端口。</para>
    /// </summary>
    /// <param name="devices">The instances one browse resolved. <para>一次浏览解析出的实例。</para></param>
    /// <returns>The advertised ports, deduplicated. <para>去重后的广播端口。</para></returns>
    private static IReadOnlyList<int> SelectAdvertisedConnectPorts(IReadOnlyList<MdnsDevice> devices)
    {
        var ports = new List<int>();
        foreach (var device in devices.OrderByDescending(static device => device.LastSeenUtc))
        {
            if (device.Port is >= 1 and <= 65535 &&
                AdbMdnsDiscovery.IsType(device, AdbMdnsDiscovery.AdbTlsService) &&
                !ports.Contains(device.Port))
            {
                ports.Add(device.Port);
            }
        }

        return ports;
    }

    private static async Task<bool> IsListeningAsync(string host, int port, CancellationToken cancellationToken)
        => await DebugBridgeRoute.TryProbeAsync(host, port, cancellationToken).ConfigureAwait(false)
            == DebugBridgeProbeOutcome.Listening;

    /// <summary>
    /// Orders the secure connect ports known for one device: the most recently advertised instance first, then
    /// entries whose address is the pairing address, then entries that only share its SRV host name,
    /// deduplicated. The daemon moves its TLS service to a new port after pairing, so a stale instance that the
    /// responder still lists must never outrank the fresh one.
    /// <para>按顺序给出某台设备已知的安全连接端口：最近广播的实例优先，其次是地址等于配对地址的条目，最后是
    /// 只共享 SRV 主机名的条目，并去重。守护进程会在配对后把 TLS 服务换到新端口，因此应答方仍列出的旧实例绝不能
    /// 排在最新实例之前。</para>
    /// </summary>
    /// <param name="candidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="host">The device address the pairing advertisement resolved to.
    /// <para>配对广播解析出的设备地址。</para></param>
    /// <param name="pairingHostName">The pairing service's SRV host name, when known.
    /// <para>配对服务的 SRV 主机名（已知时）。</para></param>
    /// <returns>Candidate ports in preference order; empty when nothing refers to that device.
    /// <para>按优先级排序的候选端口；没有条目指向该设备时为空。</para></returns>
    internal static IReadOnlyList<int> SelectConnectPorts(IReadOnlyList<MdnsDevice> candidates, string host,
        string? pairingHostName) => SelectConnectPorts(candidates, null, host, pairingHostName);

    /// <summary>
    /// Orders the secure connect ports known for one device, preferring instances that carry the instance name adbd
    /// registered (its GUID) over ones identified only by the paired address or SRV host name, then the most recently
    /// advertised instance.
    /// <para>按顺序给出某台设备已知的安全连接端口：先取携带 adbd 注册实例名（其 GUID）的条目，其次才是只靠配对地址或
    /// SRV 主机名识别的条目，同一档内最近广播的实例优先。</para>
    /// </summary>
    /// <param name="candidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="instanceName">The instance label adbd registered, when known.
    /// <para>已知时 adbd 注册的实例标签。</para></param>
    /// <param name="host">The device address the pairing advertisement resolved to.
    /// <para>配对广播解析出的设备地址。</para></param>
    /// <param name="pairingHostName">The pairing service's SRV host name, when known.
    /// <para>配对服务的 SRV 主机名（已知时）。</para></param>
    /// <returns>Candidate ports in preference order; empty when nothing refers to that device.
    /// <para>按优先级排序的候选端口；没有条目指向该设备时为空。</para></returns>
    internal static IReadOnlyList<int> SelectConnectPorts(IReadOnlyList<MdnsDevice> candidates,
        string? instanceName, string host, string? pairingHostName)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ports = new List<int>();
        foreach (var device in candidates
                     .Where(static candidate => candidate.Port is >= 1 and <= 65535 &&
                                                AdbMdnsDiscovery.IsType(candidate,
                                                    AdbMdnsDiscovery.AdbTlsService))
                     .OrderByDescending(candidate => InstanceMatches(candidate, instanceName))
                     .ThenByDescending(static candidate => candidate.LastSeenUtc)
                     .ThenByDescending(candidate => AddressMatches(candidate, host))
                     .ThenByDescending(candidate => HostNameMatches(candidate.HostName, pairingHostName)))
        {
            if ((InstanceMatches(device, instanceName) || AddressMatches(device, host) ||
                 HostNameMatches(device.HostName, pairingHostName)) && !ports.Contains(device.Port))
            {
                ports.Add(device.Port);
            }
        }

        return ports;
    }

    /// <summary>
    /// Returns the preferred secure connect port for one device, or zero when no candidate refers to it.
    /// <para>返回某台设备最优先的安全连接端口；没有候选指向该设备时返回 0。</para>
    /// </summary>
    /// <param name="candidates">Connect instances collected while waiting for the pairing instance.
    /// <para>等待配对实例期间收集到的连接实例。</para></param>
    /// <param name="host">The device address the pairing advertisement resolved to.
    /// <para>配对广播解析出的设备地址。</para></param>
    /// <param name="pairingHostName">The pairing service's SRV host name, when known.
    /// <para>配对服务的 SRV 主机名（已知时）。</para></param>
    /// <returns>The preferred port, or zero. <para>最优先的端口；可能为 0。</para></returns>
    internal static int SelectConnectPort(IReadOnlyList<MdnsDevice> candidates, string host,
        string? pairingHostName) => SelectConnectPorts(candidates, host, pairingHostName).FirstOrDefault();

    /// <summary>
    /// Reports whether a connect instance describes the device the pairing service belongs to, by SRV host
    /// name or by address, so a dual-stack phone that answers different address families per service is
    /// still recognized.
    /// <para>判断某个连接实例是否描述配对服务所属的设备：按 SRV 主机名或按地址，使双栈手机对不同服务返回不同地址族时
    /// 仍能被识别。</para>
    /// </summary>
    /// <param name="device">The connect instance. <para>连接实例。</para></param>
    /// <param name="host">The device address the pairing advertisement resolved to.
    /// <para>配对广播解析出的设备地址。</para></param>
    /// <param name="pairingHostName">The pairing service's SRV host name, when known.
    /// <para>配对服务的 SRV 主机名（已知时）。</para></param>
    /// <returns>Whether both describe the same device. <para>两者描述同一设备时为 <see langword="true"/>。</para></returns>
    internal static bool IsSameDevice(MdnsDevice device, string host, string? pairingHostName) =>
        HostNameMatches(device.HostName, pairingHostName) || AddressMatches(device, host);

    private static bool InstanceMatches(MdnsDevice device, string? instanceName) =>
        instanceName is not null && AdbMdnsDiscovery.MatchesInstanceLabel(device.Instance, instanceName);

    private static bool HostNameMatches(string? candidateHostName, string? pairingHostName) =>
        candidateHostName is not null && pairingHostName is not null &&
        string.Equals(candidateHostName.TrimEnd('.'), pairingHostName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool AddressMatches(MdnsDevice device, string host) =>
        device.Addresses.Any(address => IsSameHost(address, host));

    /// <summary>
    /// Compares two discovered host strings as addresses: equal text matches, and so do the IPv4
    /// and IPv6 forms of the same address, so a phone that advertises the secure connect service
    /// over a different address family than the pairing service is still recognized.
    /// <para>把两个发现到的主机字符串按地址比较：文本相同视为一致，同一地址的 IPv4 与 IPv6
    /// 写法也视为一致，这样手机以与配对服务不同的地址族广播安全连接服务时仍能被识别。</para>
    /// </summary>
    private static bool IsSameHost(string candidate, string host) =>
        AdbMdnsDiscovery.SameAddress(candidate, host);

    /// <summary>Completes the connection half of QR pairing with an explicit port, for the case where the device never advertised its secure connect service via mDNS. The ADB-server route connects through the server; the direct route validates the arguments and returns the endpoint.
    /// <para>以显式端口完成 QR 配对的连接后半段，用于设备未通过 mDNS 通告安全连接服务的场景。ADB server 路由经 server 连接；直连路由仅校验参数并返回端点。</para></summary>
    /// <param name="route">The route the pairing completed through.<para>配对完成的路径。</para></param>
    /// <param name="host">The device IP address discovered during pairing.
    /// <para>配对阶段发现的设备 IP 地址。</para></param>
    /// <param name="port">The connect port shown on the phone.<para>手机上显示的连接端口。</para></param>
    /// <param name="cancellationToken">Cancels the server command.<para>取消 server 命令。</para></param>
    /// <returns>The connected <c>host:port</c> endpoint.
    /// <para>已连接的 <c>host:port</c> 端点。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank or <paramref name="port"/> is out of range.
    /// <para><paramref name="host"/> 为空或 <paramref name="port"/> 超出范围。</para></exception>
    /// <exception cref="IOException">The server route could not reach the device port, found no listening ADB server, or the server rejected the connection.
    /// <para>server 路由无法到达设备端口、未找到在监听的 adb server，或 server 拒绝连接。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<string> ConnectPairedAsync(AdbQrPairingRoute route, string host, int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var endpoint = DebugBridgeNetworkAddress.FormatEndpoint(host, port);
        if (route == AdbQrPairingRoute.Direct)
        {
            return endpoint;
        }

        if (await DebugBridgeRoute.TryProbeAsync(host, port, cancellationToken).ConfigureAwait(false)
            != DebugBridgeProbeOutcome.Listening)
        {
            throw new IOException(
                $"The device port {endpoint} is not reachable. Keep the phone on the Wireless debugging " +
                "page and enter the port it currently shows.");
        }

        if (!DebugBridgeRoute.UseAdbServer())
        {
            throw new IOException("The ADB server that holds the pairing credentials is no longer listening.");
        }

        await AdbServerServices.ConnectAsync(endpoint, DeviceCommunicationConfiguration.ServerHost,
            DeviceCommunicationConfiguration.AdbServerPort, cancellationToken).ConfigureAwait(false);
        return endpoint;
    }
}

/// <summary>
/// Identity of the phone a QR pairing targets: the address its pairing advertisement resolved to and the
/// SRV host name that advertisement carried.
/// <para>扫码配对目标手机的标识：其配对广播解析出的地址与该广播携带的 SRV 主机名。</para>
/// </summary>
/// <param name="Host">The resolved device address. <para>解析出的设备地址。</para></param>
/// <param name="HostName">The SRV target host name, when the source reported one.
/// <para>来源报告时的 SRV 目标主机名。</para></param>
/// <param name="Guid">The instance name adbd registered for its TLS service, i.e. <c>persist.adb.wifi.guid</c>,
/// which the pairing exchange returns as the device GUID. <para>adbd 为其 TLS 服务注册的实例名，即
/// <c>persist.adb.wifi.guid</c>；配对交换以设备 GUID 返回该值。</para></param>
internal sealed record AdbPairingEndpoint(string Host, string? HostName, string? Guid = null);

/// <summary>
/// One device row of the adb server's listing together with the dialable address that row resolves to.
/// <para>adb server 列表中的一条设备行，以及该行解析出的可拨号地址。</para>
/// </summary>
/// <param name="Serial">The server serial that authoritatively identifies the transport.
/// <para>权威标识该传输的 server 串号。</para></param>
/// <param name="Address">The dialable <c>host:port</c> address for that transport.
/// <para>该传输的可拨号 <c>host:port</c> 地址。</para></param>
internal sealed record AdbServerConnection(string Serial, string Address);

/// <summary>
/// Raised when QR pairing succeeded but the device never advertised its secure connect service, so the connect port could not be discovered via mDNS. The caller can complete the connection with the port shown on the phone's Wireless debugging page.
/// <para>QR 配对成功但设备从未通告安全连接服务、因而无法通过 mDNS 发现连接端口时抛出。调用方可使用手机“无线调试”页面上显示的端口完成连接。</para>
/// </summary>
public sealed class AdbQrPairingConnectPortException : TimeoutException
{
    /// <summary>
    /// Initializes the exception with the pairing route and device address needed for the
    /// manual-port fallback.
    /// <para>以手动端口回退所需的配对路由与设备地址初始化异常。</para>
    /// </summary>
    /// <param name="route">The route the pairing completed through.
    /// <para>配对完成的路径。</para></param>
    /// <param name="host">The device IP address discovered during pairing.
    /// <para>配对阶段发现的设备 IP 地址。</para></param>
    /// <param name="message">The discovery failure text. <para>发现失败的文本。</para></param>
    /// <param name="suggestedPort">A port the device advertised but did not answer on, or zero when no
    /// advertisement was seen; the manual fallback pre-fills it.
    /// <para>设备广播过但未能连通的端口；未见到任何广播时为 0；手动回退会预填该值。</para></param>
    public AdbQrPairingConnectPortException(AdbQrPairingRoute route, string host, string message,
        int suggestedPort = 0)
        : base(message)
    {
        Route = route;
        Host = host;
        SuggestedPort = suggestedPort is >= 1 and <= 65535 ? suggestedPort : 0;
    }

    /// <summary>
    /// Gets the route the pairing completed through.
    /// <para>获取配对完成的路径。</para>
    /// </summary>
    /// <value>The pairing route. <para>配对完成的路径。</para></value>
    public AdbQrPairingRoute Route { get; }

    /// <summary>
    /// Gets the device IP address discovered during pairing.
    /// <para>获取配对阶段发现的设备 IP 地址。</para>
    /// </summary>
    /// <value>The device IP address. <para>设备 IP 地址。</para></value>
    public string Host { get; }

    /// <summary>
    /// Gets a port the device advertised but did not answer on, or zero when no advertisement was seen.
    /// <para>获取设备广播过但未能连通的端口；未见到任何广播时为 0。</para>
    /// </summary>
    /// <value>The last advertised port, or zero. <para>最后看到的广播端口；可能为 0。</para></value>
    public int SuggestedPort { get; }
}