using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Validates that the device may be opened over direct USB and returns the stable key to open it with,
    /// or <c>null</c> when it is not directly openable.
    /// <para>校验设备能否经直连 USB 打开，返回用于打开的稳定键；不可直连时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="device">The device to resolve.<para>要解析的设备。</para></param>
    /// <returns>The stable key for a direct USB open, or <c>null</c>.<para>可直连 USB 打开时的稳定键，否则为 <c>null</c>。</para></returns>
    public string? ResolveDirectUsbOpenKey(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc &&
            DebugBridgeRoute.Mode == Comm.Routing.DebugBridgeRouteMode.ServerOnly)
        {
            throw new IOException(
                "The ServerOnly route mode forbids direct debug access; open the server target instead.");
        }

        if (device.Protocol == DeviceProtocolKind.Adb &&
            (device.Key.StartsWith("adb:", StringComparison.Ordinal) ||
                DebugBridgeSessionOpener.AdbServerOwns(device.Serial)))
            throw new IOException("ADB server owns this target; direct USB access is prohibited.");
        if (device.Protocol == DeviceProtocolKind.Hdc && DebugBridgeRoute.UseHdcServer())
            throw new IOException("HDC server owns this protocol; direct USB access is prohibited.");
        var direct = ResolveDirectUsbDevice(device);
        return direct.Transport == DeviceTransportKind.Usb ? direct.Key : null;
    }

    internal HdcSession OpenNetworkHdcSession(string host, int port)
        => _controller.OpenNetworkHdcSession(host, port);

    internal DeviceLease AdoptHdcSession(DeviceEndpoint endpoint, HdcSession session)
    {
        try
        {
            return _controller.AdoptSession(endpoint, session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Opens a lease for an exact stable physical key, or returns <c>null</c> when that key cannot open.<para>按精确的稳定物理键打开租约，该键无法打开时返回 <c>null</c>。</para></summary>
    /// <param name="deviceKey">The exact stable physical key of the endpoint.<para>端点的精确稳定物理键。</para></param>
    /// <param name="kind">The session kind the lease must provide.<para>租约需要提供的会话类型。</para></param>
    /// <returns>The lease for that key, or <c>null</c>.<para>该键对应的租约，或为 <c>null</c>。</para></returns>
    internal DeviceLease? OpenLeaseByKey(string deviceKey, DeviceSessionKind kind)
        => _controller.OpenLeaseByKeyAsync(deviceKey, kind).GetAwaiter().GetResult();

    internal DetectedDevice ResolveDirectUsbDevice(DetectedDevice device)
    {
        if (!device.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            !device.Key.StartsWith("hdc:", StringComparison.Ordinal))
        {
            return device;
        }
        var matches = ListUsbDevices()
            .Where(info => Classify(info) == device.Protocol &&
                string.Equals(info.SerialNumber, device.Serial, StringComparison.Ordinal)).ToArray();
        var match = RequireUniqueUsbIdentity(matches);
        return new DetectedDevice
        {
            Key = match.DeviceKey,
            Serial = device.Serial,
            Protocol = device.Protocol,
            Transport = DeviceTransportKind.Usb,
            State = "device",
            VendorId = match.VendorId,
            ProductId = match.ProductId
        };
    }

    internal static UsbDeviceInfo RequireUniqueUsbIdentity(IReadOnlyList<UsbDeviceInfo> matches)
    {
        if (matches.Count != 1)
        {
            throw new IOException("The server target cannot be uniquely matched to a physical USB device. Refresh the device list and select the USB target.");
        }
        return matches[0];
    }

    /// <summary>
    /// Opens an ADB session lease for the device: server-managed targets route through the server, direct
    /// targets through their exact USB key. Returns <c>null</c> when no lease can be opened.
    /// <para>为设备打开 ADB 会话租约：server 管理的目标经 server 路由，直连目标按其精确 USB 键；无法打开租约时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="device">The device whose ADB lease is opened.<para>要打开 ADB 租约的设备。</para></param>
    /// <returns>The lease and ADB session to dispose, or <c>null</c> when none is available.<para>需释放的租约与 ADB 会话，不可用时为 <c>null</c>。</para></returns>
    public (DeviceLease Lease, IAdbDeviceSession Session)? OpenAdbLease(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb) return null;
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal))
        {
            if (!DebugBridgeRoute.UseAdbServer())
                throw new IOException("The selected ADB server target is unavailable. Refresh and select the USB target for direct access.");
            return OpenServerAdbLease(device.Serial);
        }

        if (DebugBridgeRoute.Mode == Comm.Routing.DebugBridgeRouteMode.ServerOnly)
        {
            if (!DebugBridgeRoute.UseAdbServer())
                throw new IOException("The ServerOnly route mode requires a listening ADB server.");
            return OpenServerAdbLease(device.Serial);
        }

        if (device.Key.StartsWith("adb-tcp:", StringComparison.Ordinal))
        {
            return OpenDirectNetworkAdbLease(device.Serial);
        }
        if (device.Transport == DeviceTransportKind.Tcp && !DebugBridgeRoute.UseAdbServer())
            throw new IOException("A network ADB target requires the ADB server.");
        if (DebugBridgeSessionOpener.AdbServerOwns(device.Serial))
        {
            return OpenServerAdbLease(device.Serial);
        }

        var key = ResolveDirectUsbOpenKey(device);
        if (key is null) return null;
        var lease = _controller.OpenLeaseByKeyAsync(key, DeviceSessionKind.Adb).GetAwaiter().GetResult();
        if (lease is null) return null;
        if (!lease.TryGetSession<IAdbDeviceSession>(out var session))
        {
            CloseLease(lease);
            throw new IOException("The selected USB endpoint does not expose an ADB session.");
        }
        return (lease, session);
    }

    private (DeviceLease Lease, IAdbDeviceSession Session) OpenServerAdbLease(string serial)
    {
        var session = _controller.OpenServerAdbSession(serial,
            DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort);
        var endpoint = new DeviceEndpoint
        {
            Source = $"adb-server@{DeviceCommunicationConfiguration.ServerHost}:{DeviceCommunicationConfiguration.AdbServerPort}",
            Transport = "adb-server",
            Address = serial,
            DisplayName = serial,
            Serial = serial,
            Route = "server",
            RouteEpoch = DebugBridgeRoute.Current.Epoch,
            Protocol = "adb"
        };
        return (AdoptOwnedSession(endpoint, session), session);
    }

    /// <summary>
    /// Adopts an already-opened session into a controller lease and disposes that session when the
    /// adoption is rejected (for example because another route already holds the physical device).
    /// <para>把已打开的会话纳入控制器租约；若纳入被拒绝（例如另一条路由已持有该物理设备）则释放该会话。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint whose identity carries the reservation.
    /// <para>承载占用登记、提供身份的端点。</para></param>
    /// <param name="session">The opened session to adopt. <para>要纳入的已打开会话。</para></param>
    /// <returns>The adopted lease. <para>已纳入的租约。</para></returns>
    private DeviceLease AdoptOwnedSession(DeviceEndpoint endpoint, object session)
    {
        try
        {
            return _controller.AdoptSession(endpoint, session);
        }
        catch
        {
            if (session is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a direct TCP ADB lease for a manually registered network target while no server listens. A
    /// failed secure handshake re-browses the port by IP once before detaching the target.
    /// <para>为无 server 期间手动登记的网络目标打开直连 TCP ADB 租约；安全握手失败时先按 IP 重新浏览一次端口，仍失败才注销目标。</para>
    /// </summary>
    /// <param name="serial">A remembered <c>host:port</c> of a manual target.<para>已记住的手动目标 <c>host:port</c>。</para></param>
    /// <returns>The lease and the connected ADB session.<para>租约与已连接的 ADB 会话。</para></returns>
    private (DeviceLease Lease, IAdbDeviceSession Session) OpenDirectNetworkAdbLease(string serial)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(serial, out var host, out var port))
        {
            throw new IOException("A manually registered network target needs a host:port address.");
        }

        bool isSecure;
        lock (_networkTargetsLock)
        {
            isSecure = _manualNetworkTargets.TryGetValue(serial, out var requiresTls) && requiresTls;
        }
        var address = serial;
        IAdbDeviceSession session;
        try
        {
            session = OpenConnectedNetworkSession(host, port, isSecure);
        }
        catch (Exception) when (!isSecure)
        {
            TryDetachManualNetworkTarget(serial);
            throw;
        }
        catch (Exception) when (isSecure)
        {
            var resolved = _controller.ResolveSecureNetworkPortAsync(host, RediscoverSecurePortTimeout)
                .GetAwaiter().GetResult();
            if (resolved is null || resolved == port)
            {
                TryDetachManualNetworkTarget(serial);
                throw;
            }

            address = DebugBridgeNetworkAddress.FormatEndpoint(host, resolved.Value);
            lock (_networkTargetsLock)
            {
                _manualNetworkTargets.Remove(serial);
                _manualNetworkTargets[address] = true;
            }
            SavePairedNetworkTargets();
            try
            {
                session = OpenConnectedNetworkSession(host, resolved.Value, isSecure: true);
            }
            catch
            {
                TryDetachManualNetworkTarget(address);
                throw;
            }
        }

        var endpoint = new DeviceEndpoint
        {
            Source = "adb-tcp",
            Transport = "adb-tcp",
            Address = address,
            DisplayName = address,
            Serial = address,
            Route = "direct",
            RouteEpoch = DebugBridgeRoute.Current.Epoch,
            Protocol = "adb"
        };
        return (AdoptOwnedSession(endpoint, session), session);
    }

    /// <summary>
    /// Opens a network ADB session and completes its handshake, releasing the session and rethrowing
    /// the underlying reason on failure.
    /// <para>打开并完成一次网络 ADB 握手；失败时释放会话后向上抛出底层原因。</para>
    /// </summary>
    /// <param name="host">The target IP address or name.<para>目标的 IP 地址或名称。</para></param>
    /// <param name="port">The target TCP port.<para>目标的 TCP 端口。</para></param>
    /// <param name="isSecure">When true, the session connects over TLS.<para>为 true 时会话经 TLS 连接。</para></param>
    /// <returns>The session after a completed handshake.<para>完成握手后的会话。</para></returns>
    private IAdbDeviceSession OpenConnectedNetworkSession(string host, int port, bool isSecure)
    {
        var session = isSecure
            ? _controller.OpenSecureNetworkAdbSession(host, port)
            : _controller.OpenNetworkAdbSession(host, port);
        try
        {
            session.Connect();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decides whether an HDC target must go through the local hdc server. USB and explicit TCP
    /// targets can use the direct session path when no server is present.
    /// <para>判断 HDC 目标是否应经本机 hdc server 交互。没有 server 时，USB 与显式 TCP 目标都可走直连会话。</para>
    /// </summary>
    /// <param name="device">The HDC target under consideration.<para>待判断的 HDC 目标。</para></param>
    /// <param name="host">The server address to probe.<para>要探测的 server 地址。</para></param>
    /// <param name="port">Optional server port override.<para>可选的 server 端口覆盖。</para></param>
    /// <returns><see langword="true"/> when the target must use the HDC server.<para>目标必须经 HDC server 交互时为 <see langword="true"/>。</para></returns>
    internal static bool UseHdcServerFor(DetectedDevice device, string host = DeviceCommunicationConfiguration.ServerHost, int? port = null)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc || string.IsNullOrWhiteSpace(device.Serial))
            throw new ArgumentException("An HDC target with a connect key is required.", nameof(device));
        if (DebugBridgeRoute.Mode == Comm.Routing.DebugBridgeRouteMode.ServerOnly)
        {
            if (!DebugBridgeRoute.UseHdcServer(host, port))
            {
                throw new IOException("The ServerOnly route mode requires a listening HDC server.");
            }

            return true;
        }

        if (device.Transport is not (DeviceTransportKind.Usb or DeviceTransportKind.Tcp))
            throw new IOException("An HDC target requires a USB or TCP transport.");

        // A manually registered HDC TCP target is direct by definition, matching ADB's adb-tcp targets.
        if (device.Key.StartsWith("hdc-tcp:", StringComparison.Ordinal))
        {
            return false;
        }

        if (!DebugBridgeRoute.UseHdcServer(host, port))
        {
            return false;
        }

        // A live server owns only the connect keys it actually lists; an unlisted device falls back
        // to direct USB or TCP exactly like ADB's per-serial ownership check.
        return DebugBridgeSessionOpener.HdcServerOwns(device.Serial, host, port);
    }

    /// <summary>
    /// Opens an HDC session lease for the device: through the server when it owns the target, otherwise
    /// through the exact direct USB or TCP endpoint.
    /// <para>为设备打开 HDC 会话租约：server 拥有该目标时经 server，否则走精确的直连 USB 或 TCP 端点。</para>
    /// </summary>
    /// <param name="device">The device whose HDC lease is opened.<para>要打开 HDC 租约的设备。</para></param>
    /// <returns>The lease and HDC session to dispose, or <c>null</c> for non-HDC devices.<para>需释放的租约与 HDC 会话，非 HDC 设备时为 <c>null</c>。</para></returns>
    public (DeviceLease Lease, HdcSession Session)? OpenHdcLease(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc) return null;
        if (UseHdcServerFor(device))
        {
            var port = HdcServerClient.DefaultServerPort;
            using var server = new HdcServerClient(DeviceCommunicationConfiguration.ServerHost, port);
            var session = server.CreateSession(device.Serial);
            var endpoint = new DeviceEndpoint
            {
                Source = $"hdc-server@{DeviceCommunicationConfiguration.ServerHost}:{port}",
                Transport = "hdc-server",
                Address = device.Serial,
                DisplayName = device.Serial,
                Serial = device.Serial,
                Route = "server",
                RouteEpoch = DebugBridgeRoute.Current.Epoch,
                Protocol = "hdc"
            };
            return (AdoptOwnedSession(endpoint, session), session);
        }

        return HdcDirectConnection.Open(this, device);
    }

    /// <summary>Executes a shell command against the exact selected ADB server target; the caller must have verified the target with <see cref="RequireAdbServerDevice"/> so a property batch does not re-list targets per command.<para>针对精确选中的 ADB server 目标执行一条 shell 命令；调用方须先用 <see cref="RequireAdbServerDevice"/> 校验目标，使一次属性批量不必每条命令都重新列出目标。</para></summary>
    /// <param name="device">The ADB server target to run against.<para>要执行命令的 ADB server 目标。</para></param>
    /// <param name="command">The shell command text.<para>shell 命令文本。</para></param>
    /// <returns>The output of the command.<para>命令输出文本。</para></returns>
    public string ExecuteAdbServerShell(DetectedDevice device, string command)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!DebugBridgeRoute.UseAdbServer())
            throw new IOException("The ADB server is unavailable.");
        return new AdbServerServices(device.Serial,
            DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort)
            .RunShellAsync(command).GetAwaiter().GetResult();
    }

    /// <summary>Checks whether an official adb server is running and reachable.<para>检查官方 adb server 是否正在运行且可达。</para></summary>
    /// <value><see langword="true"/> when an adb server responds.<para>有 adb server 应答时为 <see langword="true"/>。</para></value>
    public bool HasAdbServer => DebugBridgeRoute.UseAdbServer();

    /// <summary>Requires the exact server-owned ADB target without opening a direct USB session.<para>要求目标确由 server 持有，不打开直连 USB 会话。</para></summary>
    /// <param name="device">The ADB target that the server must own.<para>必须由 server 持有的 ADB 目标。</para></param>
    public void RequireAdbServerDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        if (!DebugBridgeRoute.UseAdbServer())
            throw new IOException("The ADB server is unavailable.");
        EnsureAdbServerTarget(device.Serial);
    }

    /// <summary>Probes the selected ADB target through the controller lease; server errors never grant direct USB access.<para>经控制器租约探测所选 ADB 目标；server 错误不会转为直连 USB 访问。</para></summary>
    /// <param name="device">The ADB target to probe.<para>要探测的 ADB 目标。</para></param>
    /// <returns><see langword="true"/> when the target is connected.<para>目标已连接时为 <see langword="true"/>。</para></returns>
    public bool ProbeAdb(DetectedDevice device)
    {
        try { return RunAdb(device, session => session.IsConnected); }
        catch { return false; }
    }

    /// <summary>Probes the selected Fastboot target and reads its product variable.<para>探测所选 Fastboot 目标并读取其 product 变量。</para></summary>
    /// <param name="device">The Fastboot target to probe.<para>要探测的 Fastboot 目标。</para></param>
    /// <returns>Whether the target answered and the product variable it reports.<para>目标是否应答，以及其报告的 product 变量。</para></returns>
    public (bool Ready, string? Product) ProbeFastboot(DetectedDevice device)
    {
        try { return (true, RunFastboot(device, session => session.GetVariable("product", useCache: false))); }
        catch { return (false, null); }
    }

    /// <summary>Checks whether the selected RKDL target is responsive.<para>检查所选 RKDL 目标是否可以响应。</para></summary>
    /// <param name="device">The RKDL target to check.<para>要检查的 RKDL 目标。</para></param>
    /// <returns><see langword="true"/> when the target answers the readiness check.<para>目标响应就绪检查时为 <see langword="true"/>。</para></returns>
    public bool ProbeRkdl(DetectedDevice device)
    {
        try
        {
            return RunRkdlAsync(device, session =>
                Task.FromResult(session.IsRockchipDevice() && session.TestDeviceReady())).GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    /// <summary>Lists the targets managed by the running adb server, or an empty list when it is absent.<para>列出正在运行的 adb server 所管理的目标；server 不在时为空列表。</para></summary>
    /// <returns>The server target list, empty when the server is absent.<para>server 的目标列表；server 不在时为空列表。</para></returns>
    internal static IReadOnlyList<AdbTargetInfo> ListAdbServerTargets() =>
        new AdbServerServices(string.Empty, DeviceCommunicationConfiguration.ServerHost,
            DeviceCommunicationConfiguration.AdbServerPort).ListTargets();

    private static void EnsureAdbServerTarget(string serial)
    {
        if (!ListAdbServerTargets().Any(target => string.Equals(target.Serial, serial, StringComparison.Ordinal)))
            throw new IOException("The ADB server does not contain the selected target. Direct USB fallback is prohibited while the server is running.");
    }

    /// <summary>Reports whether the running server manages the device, for routing server versus direct-USB work.<para>报告正在运行的 server 是否管理该设备，用于在 server 与直连 USB 之间路由。</para></summary>
    /// <param name="device">The device to look for in the server list.<para>要在 server 列表中查找的设备。</para></param>
    /// <returns><see langword="true"/> when the running server manages that device.<para>正在运行的 server 管理该设备时为 <see langword="true"/>。</para></returns>
    private static bool AdbServerManages(DetectedDevice device)
    {
        if (!DebugBridgeRoute.UseAdbServer())
        {
            return false;
        }

        var present = ListAdbServerTargets().Any(target =>
            string.Equals(target.Serial, device.Serial, StringComparison.Ordinal));
        if (!present && device.Key.StartsWith("adb:", StringComparison.Ordinal))
            throw new IOException("The selected ADB server target is no longer present.");
        return present;
    }

    /// <summary>Checks the current HDC server and propagates handshake errors.<para>检查当前的 HDC server，并传递握手错误。</para></summary>
    /// <returns>A connected HDC server client, or <c>null</c>.<para>已连接的 HDC server 客户端，或为 <c>null</c>。</para></returns>
    private HdcServerClient? ProbeHdcServer()
    {
        if (!DebugBridgeRoute.UseHdcServer()) return null;
        var client = new HdcServerClient();
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

}