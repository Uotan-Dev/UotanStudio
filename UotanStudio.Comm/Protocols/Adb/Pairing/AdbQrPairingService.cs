using System.Net.Sockets;
using UotanStudio.Comm.Routing;
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
/// <para>While an adb server listens, the address and port of both services are read from that
/// server's own mDNS table — the same source the <c>adb pair</c> and <c>adb connect</c> command line
/// uses — and from the local multicast browser at the same time; whichever resolves the expected
/// instance first is used. Without a server only the local browser runs.</para>
/// <para>只要 adb server 在监听，两个服务的地址与端口就同时来自该 server 自身的 mDNS 表（也就是
/// <c>adb pair</c> 与 <c>adb connect</c> 命令行使用的同一来源）和本地组播浏览器，先解析出期望实例的
/// 一方被采用。没有 server 时只运行本地浏览器。</para>
/// </remarks>
public static class AdbQrPairingService
{
    private static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ConnectDiscoveryTimeout = TimeSpan.FromSeconds(10);

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

        var pairing = await DiscoverEndpointAsync(useServer,
                entry => AdbServerMdns.IsType(entry, AdbMdnsDiscovery.AdbTlsPairingService) &&
                         AdbMdnsDiscovery.MatchesInstanceLabel(entry.Instance, credentials.ServiceName),
                AdbMdnsDiscovery.AdbTlsPairingService,
                device => AdbMdnsDiscovery.MatchesInstanceLabel(device.Instance, credentials.ServiceName),
                timeout, cancellationToken).ConfigureAwait(false)
            ?? throw new TimeoutException(
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
            var address = await PairThroughServerAsync(credentials, host, pairing.Port, progress, cancellationToken)
                .ConfigureAwait(false);
            return new AdbQrPairingOutcome(AdbQrPairingRoute.AdbServer, address);
        }

        var deviceGuid = await AdbPairingConnection.PairAsync(
                host, pairing.Port, credentials.Password, cancellationToken).ConfigureAwait(false);

        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Connecting, foundAt));
        var connectPort = await ResolveConnectPortAsync(AdbQrPairingRoute.Direct, false, host, cancellationToken)
            .ConfigureAwait(false);
        return new AdbQrPairingOutcome(AdbQrPairingRoute.Direct,
            DebugBridgeNetworkAddress.FormatEndpoint(host, connectPort));
    }

    /// <summary>
    /// Discovers one service from both sources at once: the adb server's mDNS table, which is
    /// authoritative while a server listens and already holds resolved endpoints, and the local
    /// multicast browser, which works without a server but has to redo the PTR, SRV and address
    /// resolution itself. Whichever source sees the advertisement first wins, so a phone that only
    /// advertises while its pairing dialog is open is caught by the faster source, while a server
    /// process that cannot receive multicast traffic — a firewall blocking UDP 5353 for it — is still
    /// covered by the local browser inside the same window.
    /// <para>同时从两个来源发现同一个服务：adb server 的 mDNS 表，以及本地组播浏览器。只要有 server 在监听，
    /// 该表就是权威来源，且其中的端点已完成解析；本地浏览器不需要 server 也能工作，但必须自己重做 PTR、SRV
    /// 与地址解析。谁先看到广播谁先返回，因此只在配对对话框打开期间广播的手机会被更快的来源抓到；而当 server
    /// 进程收不到组播流量（防火墙为它拦截 UDP 5353）时，同一窗口内本地浏览器仍能兜底。</para>
    /// </summary>
    /// <remarks>Once one source resolves the expected instance, the other browse is cancelled and observed,
    /// so a server hit returns without waiting for the local browser's full window.
    /// <para>一旦某个来源解析出期望实例，另一个浏览会被取消并观察，使 server 命中无需等待本地浏览器跑满窗口。</para></remarks>
    /// <param name="useServer">Whether the server table should be polled alongside the local browser.
    /// <para>是否在本地浏览的同时轮询 server 的表。</para></param>
    /// <param name="serverMatch">Test against a row of the server table.
    /// <para>用于匹配 server 表中的行。</para></param>
    /// <param name="serviceType">The service type browsed locally. <para>本地浏览的服务类型。</para></param>
    /// <param name="browserMatch">Test against a locally browsed device.
    /// <para>用于匹配本地浏览到的设备。</para></param>
    /// <param name="timeout">Window given to both sources. <para>两个来源共用的窗口。</para></param>
    /// <param name="cancellationToken">Cancels both sources. <para>取消两个来源。</para></param>
    /// <returns>The resolved device, or <c>null</c> when neither source advertised it in
    /// time. <para>解析出的设备；两个来源都未在期限内广播时为 <c>null</c>。</para></returns>
    private static async Task<MdnsDevice?> DiscoverEndpointAsync(bool useServer,
        Func<AdbServerMdnsEntry, bool> serverMatch, string serviceType, Func<MdnsDevice, bool> browserMatch,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var raced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var browserTask = AdbMdnsDiscovery.WaitForServiceAsync(serviceType, browserMatch, timeout, raced.Token);
        if (!useServer)
        {
            return await browserTask.ConfigureAwait(false);
        }

        var serverTask = WaitForServerEntryAsync(serverMatch, timeout, raced.Token);
        try
        {
            var finished = await Task.WhenAny(browserTask, serverTask).ConfigureAwait(false);
            var resolved = await finished.ConfigureAwait(false);
            if (resolved is not null)
            {
                raced.Cancel();
                return resolved;
            }

            return await (ReferenceEquals(finished, browserTask) ? serverTask : browserTask).ConfigureAwait(false);
        }
        finally
        {
            raced.Cancel();
            await IgnoreCompletionAsync(browserTask).ConfigureAwait(false);
            await IgnoreCompletionAsync(serverTask).ConfigureAwait(false);
        }
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

    private static async Task<MdnsDevice?> WaitForServerEntryAsync(Func<AdbServerMdnsEntry, bool> match,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lookup = await AdbServerMdns.WaitForAsync(match, timeout, cancellationToken).ConfigureAwait(false);
        return lookup.Entry is null ? null : AdbServerMdns.ToDevice(lookup.Entry);
    }

    private static async Task<string> PairThroughServerAsync(AdbQrPairingCredentials credentials,
        string host, int pairingPort, IProgress<AdbQrPairingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serverHost = DeviceCommunicationConfiguration.ServerHost;
        var serverPort = DeviceCommunicationConfiguration.AdbServerPort;
        await AdbServerServices.PairAsync(host, pairingPort, credentials.Password, serverHost, serverPort,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new AdbQrPairingProgress(AdbQrPairingStage.Connecting,
            DebugBridgeNetworkAddress.FormatHost(host)));
        var existing = TryFindServerEndpoint(host);
        if (existing is not null)
        {
            return existing;
        }

        var connectPort = await ResolveConnectPortAsync(AdbQrPairingRoute.AdbServer, true, host, cancellationToken)
            .ConfigureAwait(false);
        await AdbServerServices.ConnectAsync(DebugBridgeNetworkAddress.FormatEndpoint(host, connectPort),
            serverHost, serverPort, cancellationToken).ConfigureAwait(false);
        return DebugBridgeNetworkAddress.FormatEndpoint(host, connectPort);
    }

    private static string? TryFindServerEndpoint(string host)
    {
        try
        {
            var services = new AdbServerServices(string.Empty, DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort);
            var prefix = host + ":";
            return services.ListTargets()
                .FirstOrDefault(target => target.Serial.StartsWith(prefix, StringComparison.Ordinal) &&
                    target.State != "offline")?.Serial;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the secure connect port for an already paired device, again preferring the server table
    /// when that table answered during pairing and falling back to the local browser.
    /// <para>为已配对设备查找安全连接端口：配对阶段 server 表有应答时继续优先使用它，否则回退到本地浏览器。</para>
    /// </summary>
    /// <param name="route">The route the pairing completed through, carried into the manual-port
    /// fallback. <para>配对完成的路径，会带入手动端口回退。</para></param>
    /// <param name="useServer">Whether the adb server's mDNS table should be polled alongside the
    /// local browser. <para>是否在本地浏览的同时轮询 adb server 的 mDNS 表。</para></param>
    /// <param name="host">The device address discovered during pairing.
    /// <para>配对阶段发现的设备地址。</para></param>
    /// <param name="cancellationToken">Cancels discovery. <para>取消发现。</para></param>
    /// <returns>The advertised secure connect port. <para>广播出的安全连接端口。</para></returns>
    /// <exception cref="AdbQrPairingConnectPortException">No source advertised the secure connect
    /// service. <para>没有任何来源广播安全连接服务。</para></exception>
    private static async Task<int> ResolveConnectPortAsync(AdbQrPairingRoute route, bool useServer, string host,
        CancellationToken cancellationToken)
    {
        var connect = await DiscoverEndpointAsync(useServer,
                entry => AdbServerMdns.IsType(entry, AdbMdnsDiscovery.AdbTlsService) &&
                         IsSameHost(entry.Host, host),
                AdbMdnsDiscovery.AdbTlsService,
                device => device.Addresses.Any(candidate => IsSameHost(candidate, host)),
                ConnectDiscoveryTimeout, cancellationToken).ConfigureAwait(false)
            ?? throw new AdbQrPairingConnectPortException(route, host,
                $"The device did not advertise its secure connect service after pairing (no " +
                $"'{AdbMdnsDiscovery.AdbTlsService}' entry for {host}).");
        return connect.Port;
    }

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
    public AdbQrPairingConnectPortException(AdbQrPairingRoute route, string host, string message)
        : base(message)
    {
        Route = route;
        Host = host;
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
}