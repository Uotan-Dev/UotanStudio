using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Mcp.Tools;

/// <summary>Owns device access when MCP runs without the desktop host.
/// <para>在 MCP 不依赖桌面宿主运行时持有设备访问权。</para>
/// The gateway is built from the communication layer's default controller, so discovery, leasing and
/// protocol sessions all stay inside <c>UotanStudio.Comm</c> and need no UI state.
/// <para>网关由通信层的默认控制器构建，因此发现、租用与协议会话全部留在 <c>UotanStudio.Comm</c> 内，无需任何界面状态。</para>
/// </summary>
internal sealed class HeadlessDeviceManagerGateway : IDeviceManagerGateway, IBootProtocolGateway
{
    private readonly DeviceController _controller;
    private readonly Func<bool> _hdcServerAvailable;

    public HeadlessDeviceManagerGateway()
    {
        _controller = DeviceControllerFactory.CreateDefault();
        _hdcServerAvailable = () => DebugBridgeRoute.UseHdcServer();
    }

    internal HeadlessDeviceManagerGateway(DeviceController controller, Func<bool> hdcServerAvailable)
    {
        _controller = controller;
        _hdcServerAvailable = hdcServerAvailable;
    }

    private static T Complete<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
    private static void Close(DeviceLease lease) => lease.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    private DeviceSnapshot Refresh(CancellationToken cancellationToken = default) =>
        Complete(_controller.RefreshAsync(cancellationToken));

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices()
    {
        var snapshot = Refresh();
        return [.. snapshot.Endpoints.Where(endpoint => IsRoutableUsb(snapshot, endpoint))
            .Select(endpoint => endpoint.UsbInfo ??
                throw new InvalidDataException("USB endpoint has no inventory details."))];
    }

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
    {
        var snapshot = Refresh();
        return [.. snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
                (endpoint.Route == "server" || IsRoutableUsb(snapshot, endpoint)))
            .Select(endpoint =>
            {
                var identity = endpoint.Serial ?? endpoint.Address;
                if (endpoint.Route == "server")
                {
                    return (identity, endpoint.State ?? "device");
                }

                try
                {
                    return (identity, RunAdb(identity, _ => "device"));
                }
                catch (Exception)
                {
                    return (identity, "offline");
                }
            })];
    }

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAdb(serial, session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = session.OpenServiceStream(service);
            return operation(stream);
        }, cancellationToken);
    }

    public void PushAdbFile(string serial, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        Complete(RunAdbAsync(serial, async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var sync = session.CreateSyncClient();
            await sync.PushAsync(localPath, remotePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken));
    }

    public void PullAdbFile(string serial, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        Complete(RunAdbAsync(serial, async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var sync = session.CreateSyncClient();
            await sync.PullAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken));
    }

    private async Task<TResult> RunAdbAsync<TResult>(string serial,
        Func<UotanStudio.Comm.Contracts.IAdbDeviceSession, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = Refresh(cancellationToken);
        var endpoint = SelectAdb(snapshot, serial);
        await using var lease = await _controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Adb,
            cancellationToken).ConfigureAwait(false);
        if (!lease.TryGetSession<UotanStudio.Comm.Contracts.IAdbDeviceSession>(out var session))
        {
            throw new InvalidOperationException("ADB endpoint has no supported session.");
        }

        await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!session.IsConnected)
        {
            throw new IOException("ADB authentication did not complete.");
        }

        return await operation(session).ConfigureAwait(false);
    }

    private TResult RunAdb<TResult>(string serial,
        Func<UotanStudio.Comm.Contracts.IAdbDeviceSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var snapshot = Refresh(cancellationToken);
        var endpoint = SelectAdb(snapshot, serial);
        var lease = Complete(_controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Adb, cancellationToken));
        try
        {
            if (!lease.TryGetSession<UotanStudio.Comm.Contracts.IAdbDeviceSession>(out var session))
            {
                throw new InvalidOperationException("ADB endpoint has no supported session.");
            }

            session.Connect();
            if (!session.IsConnected)
            {
                throw new IOException("ADB authentication did not complete.");
            }

            return operation(session);
        }
        finally
        {
            Close(lease);
        }
    }

    public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = Refresh(cancellationToken);
        var endpoints = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "fastboot" &&
            IsRoutableUsb(snapshot, endpoint));
        DeviceEndpoint selected;
        if (string.IsNullOrWhiteSpace(target))
        {
            var candidates = endpoints.Take(2).ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
            }

            selected = candidates[0];
        }
        else
        {
            var candidates = endpoints.Where(endpoint => MatchesUsbTarget(endpoint, target)).Take(2).ToArray();
            if (candidates.Length == 0)
            {
                throw new IOException($"No Fastboot endpoint matches target '{target}'.");
            }

            if (candidates.Length > 1)
            {
                throw new IOException($"Fastboot target '{target}' is ambiguous.");
            }

            selected = candidates[0];
        }

        var lease = Complete(_controller.OpenAsync(
            snapshot, selected.Id, DeviceSessionKind.Fastboot, cancellationToken));
        try
        {
            if (!lease.TryGetSession<FastbootSession>(out var session))
            {
                throw new InvalidOperationException("The selected device does not expose Fastboot.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return operation(session);
        }
        finally
        {
            Close(lease);
        }
    }

    /// <summary>
    /// <para>租用清单识别出的 EDL 端点，把单次只读事务交给回调，并在返回前释放租约。</para>
    /// Leases the EDL endpoint the inventory identified, hands one read transaction to the callback, and
    /// releases the lease before returning.</summary>
    /// <typeparam name="TResult"><para>回调产出的值类型。</para>Value the callback produces.</typeparam>
    /// <param name="target"><para>EDL serial 或 USB 设备键；为空时要求恰好存在一个候选端点。</para>
    /// EDL serial or USB device key; a blank value requires exactly one candidate endpoint.</param>
    /// <param name="operation"><para>借用会话执行的读取回调。</para>The read callback using the borrowed session.</param>
    /// <param name="cancellationToken"><para>贯穿发现、租用与回调的令牌。</para>Token threaded through discovery,
    /// leasing and the callback.</param>
    /// <returns><para>回调返回的值。</para>The value returned by <paramref name="operation"/>.</returns>
    public Task<TResult> RunEdlAsync<TResult>(string? target,
        Func<EdlSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        => RunProtocolSessionAsync("edl", DeviceSessionKind.Edl, target, operation, cancellationToken);

    /// <summary>
    /// <para>租用清单识别出的 MTK 端点，把单次只读事务交给回调，并在返回前释放租约；仅有日志接口的端点会被拒绝。</para>
    /// Leases the MTK endpoint the inventory identified, hands one read transaction to the callback, and
    /// releases the lease before returning; a logging-only endpoint is refused.</summary>
    /// <typeparam name="TResult"><para>回调产出的值类型。</para>Value the callback produces.</typeparam>
    /// <param name="target"><para>MTK serial 或 USB 设备键；为空时要求恰好存在一个候选端点。</para>
    /// MTK serial or USB device key; a blank value requires exactly one candidate endpoint.</param>
    /// <param name="operation"><para>借用会话执行的读取回调。</para>The read callback using the borrowed session.</param>
    /// <param name="cancellationToken"><para>贯穿发现、租用与回调的令牌。</para>Token threaded through discovery,
    /// leasing and the callback.</param>
    /// <returns><para>回调返回的值。</para>The value returned by <paramref name="operation"/>.</returns>
    public Task<TResult> RunMtkAsync<TResult>(string? target,
        Func<MtkSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        => RunProtocolSessionAsync("mtk", DeviceSessionKind.Mtk, target, operation, cancellationToken);

    /// <summary>
    /// <para>Leases the descriptor-identified EDL or MTK direct endpoint, hands one read transaction to
    /// the callback, and releases the lease before returning; a refused or already leased endpoint is
    /// never reopened, retried, or routed to another transport.</para>
    /// <para>租用按描述符识别出的 EDL 或 MTK 直连端点，把单次读取事务交给回调，并在返回前释放租约；
    /// 被拒绝或已被租用的端点绝不重开、绝不重试、绝不改走其他传输。</para>
    /// </summary>
    private async Task<TResult> RunProtocolSessionAsync<TSession, TResult>(string protocol,
        DeviceSessionKind kind, string? target,
        Func<TSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        where TSession : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = Refresh(cancellationToken);
        var endpoint = SelectRoutableEndpoint(snapshot, protocol, target);
        await using var lease = await _controller.OpenAsync(snapshot, endpoint.Id, kind, cancellationToken)
            .ConfigureAwait(false);
        if (!lease.TryGetSession<TSession>(out var session))
        {
            throw new InvalidOperationException($"The leased endpoint carries no {protocol} session.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await operation(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>Locates the single endpoint classified as the requested protocol that this host may drive
    /// directly; without a target it requires exactly one candidate and never guesses by enumeration
    /// order.</para>
    /// <para>在本宿主可直接驱动、且按请求协议分类的端点中定位唯一目标；未给出目标时要求恰好存在一个
    /// 候选端点，绝不按枚举顺序猜测。</para>
    /// </summary>
    private DeviceEndpoint SelectRoutableEndpoint(DeviceSnapshot snapshot, string protocol, string? target)
    {
        var candidates = snapshot.Endpoints.Where(endpoint =>
            string.Equals(endpoint.Protocol, protocol, StringComparison.Ordinal) &&
            IsRoutableUsb(snapshot, endpoint));
        if (string.IsNullOrWhiteSpace(target))
        {
            var only = candidates.Take(2).ToArray();
            if (only.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Exactly one {protocol} device must be connected, or pass its serial or device key.");
            }

            return only[0];
        }

        var matches = candidates.Where(endpoint => MatchesUsbTarget(endpoint, target)).Take(2).ToArray();
        if (matches.Length == 0)
        {
            throw new IOException($"No {protocol} endpoint matches target '{target}'.");
        }

        if (matches.Length > 1)
        {
            throw new IOException($"The {protocol} target '{target}' is ambiguous.");
        }

        return matches[0];
    }

    public (string ServiceName, string Password, string Payload) CreateQrPairingSession()
    {
        var credentials = AdbQrPairing.GenerateCredentials();
        return (credentials.ServiceName, credentials.Password, AdbQrPairing.BuildPayload(credentials));
    }

    public async Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds is < 1 or > MaxPairingWaitSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                $"The QR pairing wait must be between 1 and {MaxPairingWaitSeconds} seconds.");
        }

        var outcome = await AdbQrPairingService.PairAsync(
            new AdbQrPairingCredentials(serviceName, password),
            progress: null,
            discoveryTimeout: TimeSpan.FromSeconds(timeoutSeconds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (outcome.Route.ToString(), outcome.Address);
    }

    private const int MaxPairingWaitSeconds = 1800;

    /// <summary>
    /// <para>Decides whether a directly routable USB endpoint is the target the caller selected, matching
    /// either the reported serial or the stable USB device key, which is also the endpoint address.</para>
    /// <para>按 serial 或稳定 USB 设备键判断某个可直接路由的 USB 端点是否就是调用方选定的目标；
    /// 设备键同时也是端点地址。</para>
    /// </summary>
    /// <param name="endpoint"><para>The endpoint under test.</para>待匹配的端点。</param>
    /// <param name="target"><para>The caller-supplied serial or device key.</para>调用方给出的 serial 或设备键。</param>
    /// <returns><para><see langword="true"/> when this endpoint is the selected target.</para>
    /// 该端点即为所选目标时为 <see langword="true"/>。</returns>
    private static bool MatchesUsbTarget(DeviceEndpoint endpoint, string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        (string.Equals(endpoint.Serial ?? endpoint.Address, target, StringComparison.Ordinal) ||
            string.Equals(endpoint.Address, target, StringComparison.Ordinal) ||
            string.Equals(endpoint.UsbInfo?.DeviceKey, target, StringComparison.Ordinal));

    internal bool IsRoutableUsb(DeviceSnapshot snapshot, DeviceEndpoint endpoint)
        => endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            !(endpoint.Protocol == "adb" && ServerOwns(snapshot, endpoint.Serial)) &&
            !(endpoint.Protocol == "hdc" && _hdcServerAvailable());

    private static bool ServerOwns(DeviceSnapshot snapshot, string? serial)
        => snapshot.Endpoints.Any(endpoint => endpoint.Protocol == "adb" && endpoint.Route == "server" &&
            (string.IsNullOrWhiteSpace(serial) ||
                string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal)));

    internal static DeviceEndpoint SelectAdb(DeviceSnapshot snapshot, string serial)
    {
        var serverMatches = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Route == "server" &&
            string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal))
            .Take(2).ToArray();
        if (serverMatches.Length > 1)
        {
            throw new IOException("ADB server target is ambiguous.");
        }

        if (serverMatches.Length == 1)
        {
            return serverMatches[0];
        }

        var matches = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            !ServerOwns(snapshot, endpoint.Serial) &&
            (string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal) ||
                string.Equals(endpoint.Address, serial, StringComparison.Ordinal)))
            .Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new IOException("ADB target is missing or ambiguous.");
        }

        return matches[0];
    }
}