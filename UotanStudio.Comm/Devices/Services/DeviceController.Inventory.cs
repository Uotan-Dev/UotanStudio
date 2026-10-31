using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Comm.Transports.Udp;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// L4 raw-USB surface: exposes enumeration, hotplug monitoring and reopen-by-key so hosts need not
/// name <c>CommKit</c>. Each method is a behavior-identical forward to the same communication kit the
/// controller already uses; it changes no routing or physical-association rule.
/// <para>L4 原始 USB 承载：向服务层暴露枚举、热插拔监视与按键重开，使宿主无需直接命名
/// <c>CommKit</c>。这些方法只是对控制器所用同一通信套件的行为等价转发，不改变任何路由或
/// 物理关联规则。</para>
/// </summary>
public sealed partial class DeviceController
{
    private readonly Lock _watchGate = new();
    private IDisposable? _removalWatch;

    /// <summary>Enumerates the raw USB inventory behind the controller's endpoints.
    /// <para>枚举控制器端点背后的原始 USB 清单。</para></summary>
    /// <param name="apiKind">USB backend to query; <c>Auto</c> selects the available backend.
    /// <para>要查询的 USB 后端；<c>Auto</c> 自动选择可用后端。</para></param>
    /// <param name="filter">Optional enumeration filter; null enumerates every device.
    /// <para>可选的枚举过滤器；null 枚举全部设备。</para></param>
    /// <returns>The matching device nodes.<para>匹配的设备节点。</para></returns>
    /// <exception cref="NotSupportedException">The controller was composed without a communication kit.
    /// <para>该控制器组装时未提供通信套件。</para></exception>
    public IReadOnlyList<UsbDeviceInfo> EnumerateUsb(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null)
        => RequiredKit.EnumerateUsbDevices(apiKind, filter);

    /// <summary>Monitors raw USB additions and removals.<para>监视原始 USB 新增与移除。</para></summary>
    /// <param name="onChanged">Callback receiving each batch of device changes.
    /// <para>接收每批设备变化的回调。</para></param>
    /// <param name="apiKind">USB backend to monitor; <c>Auto</c> selects the available backend.
    /// <para>要监视的 USB 后端；<c>Auto</c> 自动选择可用后端。</para></param>
    /// <param name="filter">Optional monitor filter; null watches every device.
    /// <para>可选的监视过滤器；null 监视全部设备。</para></param>
    /// <param name="pollInterval">Polling cadence for backends without native hotplug; null uses the kit default.
    /// <para>无原生热插拔后端的轮询间隔；null 使用套件默认值。</para></param>
    /// <param name="fireInitialSnapshot">Whether the first callback reports the current inventory as additions.
    /// <para>首次回调是否把当前清单作为新增设备上报。</para></param>
    /// <param name="onError">Optional callback for monitor failures; without it, errors are swallowed.
    /// <para>监视失败的可选回调；未提供时错误被吞掉。</para></param>
    /// <returns>A handle that stops monitoring when disposed.<para>释放即停止监视的句柄。</para></returns>
    /// <exception cref="NotSupportedException">The controller was composed without a communication kit.
    /// <para>该控制器组装时未提供通信套件。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed; a monitor started afterwards would never be released.
    /// <para>控制器已被释放；此时启动的监视将永远不会被归还。</para></exception>
    public IDisposable WatchUsb(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null)
    {
        ThrowIfDisposed();
        return RequiredKit.MonitorUsbDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);
    }

    /// <summary>
    /// Reopens the physical device session matching the stable key.
    /// <para>按稳定设备键重开物理设备会话。</para>
    /// </summary>
    internal IUsbDeviceSession? OpenUsbSessionByKey(string deviceKey) => RequiredKit.OpenUsbDeviceSessionByKey(deviceKey);

    /// <summary>Creates an ADB session façade routed through the local adb server.
    /// <para>创建一条经本机 adb server 交互的 ADB 会话门面。</para></summary>
    /// <param name="serial">Exact server target serial to serve.
    /// <para>要服务的精确 server 目标序列号。</para></param>
    /// <param name="host">adb server host.<para>adb server 主机。</para></param>
    /// <param name="port">adb server port.<para>adb server 端口。</para></param>
    /// <returns>A session façade that talks to the adb server over a socket.
    /// <para>经套接字与 adb server 交互的会话门面。</para></returns>
    public IAdbDeviceSession OpenServerAdbSession(string serial, string host, int port)
        => AdbSession.ForServer(serial, host, port);

    /// <summary>
    /// Opens a direct TCP session to the daemon at <c>host:port</c> authenticated with the host identity
    /// chain (the user key followed by <c>ADB_VENDOR_KEYS</c>), bypassing any server. The caller must
    /// <c>Connect()</c> the session and dispose it on failure.
    /// <para>打开到 <c>host:port</c> 上 adbd 的直接 TCP 会话，使用主机身份链（用户密钥后接
    /// <c>ADB_VENDOR_KEYS</c>）完成认证；不经过任何 server。调用方需自行 <c>Connect()</c> 并在失败时释放。</para>
    /// </summary>
    /// <param name="host">Device hostname or IP literal. <para>设备主机名或 IP 字面量。</para></param>
    /// <param name="port">Daemon port, conventionally 5555. <para>adbd 监听端口，通常是 5555。</para></param>
    /// <param name="authentication">
    /// Single authentication identity used by the handshake; when null the runtime host identity chain
    /// (user key plus <c>ADB_VENDOR_KEYS</c>) is loaded and owned by the session.
    /// <para>握手使用的单一认证身份；为 null 时加载运行时主机身份链（用户密钥加 <c>ADB_VENDOR_KEYS</c>），
    /// 并由会话拥有。</para>
    /// </param>
    /// <returns>An unconnected direct session. <para>尚未连接的直接会话。</para></returns>
    public IAdbDeviceSession OpenNetworkAdbSession(string host, int port, AdbAuthentication? authentication = null)
    {
        if (authentication is not null)
        {
            ICommTransport? explicitTransport = null;
            try
            {
                explicitTransport = new AdbTcpTransport(host, port);
                return new AdbSession(explicitTransport, authentication);
            }
            catch
            {
                explicitTransport?.Dispose();
                throw;
            }
        }

        var identities = AdbAuthentication.LoadIdentityChain();
        ICommTransport? transport = null;
        try
        {
            transport = new AdbTcpTransport(host, port);
            return new AdbSession(transport, identities);
        }
        catch
        {
            transport?.Dispose();
            foreach (var identity in identities)
            {
                identity.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a direct session to adbd's secure service (Android 11+ TLS) at host:port. The client presents the
    /// certificate derived from its host identity key for this handshake, the same identity an adb host derives
    /// from <c>~/.android/adbkey</c>, and the device's self-signed server certificate is accepted without
    /// validation because adbd rebuilds it on every handshake; adbd authenticates this host's key instead. The
    /// caller must connect and dispose on failure.
    /// <para>在 host:port 上直接打开到 adbd 安全服务（Android 11+ TLS）的会话。客户端出示本次握手由主机身份
    /// 密钥派生的证书，与上游 adb 每次用 <c>~/.android/adbkey</c> 重新生成一致；设备的自签服务端证书因 adbd
    /// 每次握手都会重建而直接接受不校验，改由 adbd 校验本机密钥。调用方须自行连接，并在失败时释放。</para>
    /// </summary>
    /// <param name="host">Device hostname or IP literal. <para>设备主机名或 IP 字面量。</para></param>
    /// <param name="port">Secure daemon port. <para>adbd 安全服务监听端口。</para></param>
    /// <param name="authentication">
    /// Single authentication identity used by the handshake and the TLS certificate; when null the runtime
    /// host identity chain (user key plus <c>ADB_VENDOR_KEYS</c>) is loaded, the certificate is derived from
    /// its user key, and the session owns every identity.
    /// <para>握手与 TLS 证书使用的单一认证身份；为 null 时加载运行时主机身份链（用户密钥加
    /// <c>ADB_VENDOR_KEYS</c>），证书由其用户密钥派生，并由会话拥有全部身份。</para>
    /// </param>
    /// <returns>An unconnected direct session. <para>尚未连接的直接会话。</para></returns>
    public IAdbDeviceSession OpenSecureNetworkAdbSession(string host, int port, AdbAuthentication? authentication = null)
    {
        if (authentication is not null)
        {
            var explicitCertificate = AdbHostCertificate.Create(authentication.RsaKey);
            ICommTransport? explicitTransport = null;
            try
            {
                explicitTransport = new AdbTlsTransport(host, port, explicitCertificate);
                return new AdbSession(explicitTransport, authentication);
            }
            catch
            {
                explicitTransport?.Dispose();
                explicitCertificate.Dispose();
                throw;
            }
        }

        return AdbSecureSessionFactory.Open(host, port);
    }

    /// <summary>
    /// Opens a direct HDC TCP session to the daemon at <c>host:port</c>, bypassing any hdc server.
    /// The caller must <c>Connect()</c> the session and dispose it on failure.
    /// <para>打开到 <c>host:port</c> 上 HDC 守护进程的直连 TCP 会话，不经过任何 hdc server。
    /// 调用方需自行 <c>Connect()</c> 并在失败时释放。</para>
    /// </summary>
    /// <param name="host">Daemon hostname or IP literal. <para>守护进程主机名或 IP 字面量。</para></param>
    /// <param name="port">Daemon TCP port, for example the OpenHarmony emulator's <c>5557</c>.
    /// <para>守护进程 TCP 端口，例如 OpenHarmony 模拟器的 <c>5557</c>。</para></param>
    /// <returns>An unconnected direct session. <para>尚未连接的直接会话。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside 1..65535.
    /// <para><paramref name="port"/> 不在 1..65535 范围内。</para></exception>
    public HdcSession OpenNetworkHdcSession(string host, int port)
        => new(new HdcTcpTransport(host, port));

    /// <summary>
    /// Opens a direct fastboot session to a fastboot-over-TCP target at <c>host:port</c>. Unlike the ADB and
    /// HDC variants the returned session is already connected: the TCP Fastboot <c>FB01</c> handshake runs
    /// while the transport is created, and a refused or silent peer throws before a session exists. The caller
    /// must dispose the session on failure of any later step.
    /// <para>打开到 <c>host:port</c> 上 fastboot-over-TCP 目标的直连会话。与 ADB 与 HDC 变体不同，返回的
    /// 会话已完成连接：TCP Fastboot 的 <c>FB01</c> 握手在创建传输时执行，对端拒绝或无应答会在会话
    /// 存在前抛出。调用方需在后续步骤失败时释放该会话。</para>
    /// </summary>
    /// <param name="host">Target hostname or IP literal. <para>目标主机名或 IP 字面量。</para></param>
    /// <param name="port">Fastboot TCP port, conventionally <c>5554</c>. <para>fastboot TCP 端口，通常是 <c>5554</c>。</para></param>
    /// <returns>A connected direct fastboot session. <para>已连接的直接 fastboot 会话。</para></returns>
    public Protocols.Fastboot.FastbootSession OpenNetworkFastbootSession(string host, int port)
        => new(new Protocols.Fastboot.Transport.FastbootTcpTransport(host, port));

    /// <summary>
    /// Opens a direct fastboot session to a fastboot-over-UDP target at <c>host:port</c>. As with the TCP
    /// variant the returned session is already connected: the Fastboot-over-Network handshake
    /// (device query plus initialization) runs while the transport is created, retrying each packet before
    /// it throws. The transport dials a literal IP address only, so <paramref name="host"/> must not be a
    /// name the resolver would have to expand.
    /// <para>打开到 <c>host:port</c> 上 fastboot-over-UDP 目标的直连会话。与 TCP 变体相同，返回的会话
    /// 已完成连接：Fastboot-over-Network 握手（设备查询加初始化）在创建传输时执行，每包重试后才
    /// 抛出。该传输只拨叫 IP 字面量，因此 <paramref name="host"/> 不能是仍需解析器展开的名称。</para>
    /// </summary>
    /// <param name="host">Target IP literal (IPv4 or IPv6). <para>目标 IP 字面量（IPv4 或 IPv6）。</para></param>
    /// <param name="port">Fastboot UDP port, conventionally <c>5554</c>. <para>fastboot UDP 端口，通常是 <c>5554</c>。</para></param>
    /// <returns>A connected direct fastboot session. <para>已连接的直接 fastboot 会话。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is not an IP literal.
    /// <para><paramref name="host"/> 不是 IP 字面量。</para></exception>
    public Protocols.Fastboot.FastbootSession OpenNetworkFastbootUdpSession(string host, int port)
    {
        if (!System.Net.IPAddress.TryParse(host, out _))
        {
            throw new ArgumentException("A fastboot-over-UDP target requires an IP literal host.", nameof(host));
        }

        return new Protocols.Fastboot.FastbootSession(
            new Protocols.Fastboot.Transport.FastbootUdpTransport(host, port));
    }

    /// <summary>Browses <c>_adb-tls-connect._tcp</c> until an instance resolves to <paramref name="host"/> and returns its port, so a remembered secure endpoint can be re-discovered after the device restarted wireless debugging on a different port. Returns <c>null</c> when the deadline passes with no advertisement.
    /// <para>浏览 <c>_adb-tls-connect._tcp</c> 直到某个实例解析到 <paramref name="host"/> 并返回其端口， 使记住的安全端点能在设备重启无线调试、端口变化后被重新发现；期限内无广播时返回 <c>null</c>。</para></summary>
    /// <param name="host">Device IP address literal of the remembered endpoint.
    /// <para>已记住端点的设备 IP 字面量。</para></param>
    /// <param name="timeout">Overall browse deadline; the Comm default applies when <c>null</c>.
    /// <para>整体浏览期限；为 <c>null</c> 时使用 Comm 默认值。</para></param>
    /// <param name="cancellationToken">Cancels the browse.<para>取消浏览。</para></param>
    /// <returns>The advertised secure connect port, or <c>null</c> when the device did not advertise it.
    /// <para>广播出的安全连接端口；设备未广播时为 <c>null</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.
    /// <para><paramref name="host"/> 为空。</para></exception>
    public async Task<int?> ResolveSecureNetworkPortAsync(string host, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var service = await AdbMdnsDiscovery.WaitForConnectServiceAsync(host, timeout, cancellationToken)
            .ConfigureAwait(false);
        return service?.Port;
    }

    /// <summary>Creates an HDC server-client façade for callers that need server-level operations rather than a single device session; device-level sessions should preferably be obtained through leases.
    /// <para>创建一条经本机 HDC server 交互的 server 客户端门面，供需要 server 级操作 （而非单设备会话）的调用方使用；设备级会话应优先经租约获得。</para></summary>
    /// <param name="host">HDC server host.<para>HDC server 主机。</para></param>
    /// <param name="port">HDC server port.<para>HDC server 端口。</para></param>
    /// <returns>A server client owning its connection until disposed.
    /// <para>持有自身连接直至释放的 server 客户端。</para></returns>
    public HdcServerClient OpenServerHdcClient(string host, int port) => new(host, port);

    /// <summary>
    /// Best-effort Fastboot <c>getvar serialno</c> read for associating a server target with a physical USB
    /// device; the claim/getvar mechanism lives in the discovery layer and the host only consumes the result.
    /// <para>Best-effort 读取 Fastboot <c>getvar serialno</c>，用于把 server 目标关联到物理 USB 设备；
    /// 认领/getvar 机制在 Comm 发现层，宿主只消费结果。</para>
    /// </summary>
    /// <param name="info">
    /// Fastboot-mode USB node to query. <para>待查询的 fastboot 模式 USB 节点。</para>
    /// </param>
    /// <returns>The reported serial number, or <see langword="null"/> when claim or <c>getvar</c> fails.
    /// <para>上报的序列号；认领或 <c>getvar</c> 失败时返回 <see langword="null"/>。</para></returns>
    public string? ReadFastbootSerial(UsbDeviceInfo info)
        => Discovery.FastbootSerialReader.Read(RequiredKit, info);

    /// <summary>
    /// Best-effort display identity read for a Rockchip bootrom/loader device, aligned with RKDevTool:
    /// the chip name via one short READ_CHIP_INFO probe plus the USB location suffix. The
    /// claim/probe mechanism lives in the discovery layer and the host only consumes the result.
    /// <para>Best-effort 读取瑞芯微 bootrom/loader 设备的显示身份，与 RKDevTool 对齐：经一次短暂的
    /// READ_CHIP_INFO 探测读取芯片名并附上 USB 位置后缀。认领/探测机制在 Comm 发现层，
    /// 宿主只消费结果。</para>
    /// </summary>
    /// <param name="info">
    /// Rockchip bootrom/loader USB node to query. <para>待查询的瑞芯微 bootrom/loader USB 节点。</para>
    /// </param>
    /// <returns>The display identity, or <see langword="null"/> when claim, probe or location evidence fails.
    /// <para>显示身份；认领、探测或位置证据失败时返回 <see langword="null"/>。</para></returns>
    public string? ReadRkdlSerial(UsbDeviceInfo info)
        => Discovery.RkdlSerialReader.Read(RequiredKit, info);

    private ICommKit RequiredKit => _kit
        ?? throw new NotSupportedException("This controller was composed without a communication kit and cannot expose the raw USB surface.");

    /// <summary>Starts the OS-driven USB removal watch: on device removal every open lease bound to that physical device is disposed (queued commands cancelled first), its cached properties are dropped and <see cref="DeviceRemoved"/> fires; on a metadata change (mode switch) leases on the old interface are released and <see cref="DeviceChanged"/> fires with the cache retained.
    /// <para>启动操作系统驱动的 USB 移除监视：设备移除时释放绑定该物理设备的所有租约 （先取消排队指令）、丢弃缓存参数并触发 <see cref="DeviceRemoved"/>； 元数据变化（模式切换）时释放旧接口租约、保留缓存并触发 <see cref="DeviceChanged"/>。</para>Does nothing when the controller has no communication kit or the watch already runs.<para>控制器没有通信套件或监视已在运行时不执行任何操作。</para></summary>
    /// <param name="onError">Optional callback for monitor failures.<para>监视失败的可选回调。</para></param>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    /// <remarks>The monitor is constructed outside the controller gate, serialized only by a dedicated watch lock: a backend that synchronously delivers changes during construction can re-enter <see cref="HandleDeviceRemoved"/> without self-locking, and the published handle stays reachable for controller disposal. A watch constructed while disposal wins the race is disposed unpublished.
    /// <para>监视器在控制器锁之外构造，仅由专用监视锁串行化：构造期间同步投递变化的后端可以重入 <see cref="HandleDeviceRemoved"/> 而不会自锁，且已发布的句柄仍可被控制器释放路径取得； 若释放竞态先行发生，则构造出的监视器会被直接释放而不会发布。</para></remarks>
    public void StartRemovalWatch(Action<Exception>? onError = null)
    {
        lock (_watchGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_kit is null || _removalWatch is not null)
                {
                    return;
                }
            }

            var watch = _kit.MonitorUsbDevices(
                changes =>
                {
                    foreach (var change in changes)
                    {
                        if (change.Kind == UsbDeviceChangeKind.Removed)
                        {
                            HandleDeviceRemoved(change.Device, metadataChanged: false);
                        }
                        else if (change.Kind == UsbDeviceChangeKind.Changed)
                        {
                            HandleDeviceRemoved(change.Device, metadataChanged: true);
                        }
                    }

                    RaiseSafe(() => UsbTopologyChanged?.Invoke(changes), "USB topology subscriber failed");
                },
                onError: onError);
            lock (_gate)
            {
                if (Volatile.Read(ref _lifecycle) == 0 && _removalWatch is null)
                {
                    _removalWatch = watch;
                    return;
                }
            }

            watch.Dispose();
        }
    }
}