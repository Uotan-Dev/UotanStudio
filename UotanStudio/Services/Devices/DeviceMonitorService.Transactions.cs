using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private const int UsbInventoryCacheMilliseconds = 500;

    private readonly Lock _usbInventoryLock = new();
    private UsbDeviceInfo[]? _usbInventory;
    private long _usbInventoryTimestamp;
    private long _usbInventoryVersion;
    private UsbDeviceInfo[]? _routableUsbInventory;
    private long _routableUsbInventoryTimestamp;
    private long _routableUsbInventoryVersion;

    /// <summary>
    /// Enumerates the USB devices visible through the current discovery snapshot.
    /// <para>枚举当前发现快照中可见的 USB 设备。</para>
    /// </summary>
    /// <returns>The USB device inventory. <para>USB 设备清单。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices()
    {
        if (TryGetCachedUsbInventory(out var cached))
        {
            return cached;
        }

        var version = Interlocked.Read(ref _scanVersion);
        return CacheUsbInventory(_controller.EnumerateUsb(), version);
    }

    /// <summary>
    /// Lists the direct-USB interfaces left in the snapshot; debug bridge servers own their
    /// protocol's interfaces, and discovery already removed those endpoints.
    /// <para>列出快照中剩余的直连 USB 接口；调试桥服务持有自身协议的接口，这些端点已在发现阶段被移除。</para>
    /// </summary>
    /// <returns>The routable direct-USB interfaces. <para>可路由的直连 USB 接口。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> ListRoutableUsbDevices()
    {
        if (TryGetCachedRoutableUsbInventory(out var cached))
        {
            return cached;
        }

        var version = Interlocked.Read(ref _scanVersion);
        var snapshot = _controller.RefreshAsync().GetAwaiter().GetResult();
        var routable = snapshot.Endpoints.Where(endpoint =>
            endpoint.Source == Comm.Devices.Discovery.UsbDiscoverySource.SourceName &&
            endpoint.Route == "direct-usb" && endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!)
            .ToArray();
        return CacheRoutableUsbInventory(routable, version);
    }

    private bool TryGetCachedUsbInventory(out IReadOnlyList<UsbDeviceInfo> devices)
    {
        lock (_usbInventoryLock)
        {
            if (IsUsbInventoryFresh(_usbInventory, _usbInventoryTimestamp, _usbInventoryVersion))
            {
                devices = [.. _usbInventory!];
                return true;
            }
        }

        devices = [];
        return false;
    }

    private bool TryGetCachedRoutableUsbInventory(out IReadOnlyList<UsbDeviceInfo> devices)
    {
        lock (_usbInventoryLock)
        {
            if (IsUsbInventoryFresh(_routableUsbInventory, _routableUsbInventoryTimestamp, _routableUsbInventoryVersion))
            {
                devices = [.. _routableUsbInventory!];
                return true;
            }
        }

        devices = [];
        return false;
    }

    private IReadOnlyList<UsbDeviceInfo> CacheUsbInventory(IReadOnlyList<UsbDeviceInfo> devices, long version)
    {
        var snapshot = devices.ToArray();
        lock (_usbInventoryLock)
        {
            _usbInventory = snapshot;
            _usbInventoryTimestamp = Environment.TickCount64;
            _usbInventoryVersion = version;
        }

        return [.. snapshot];
    }

    private IReadOnlyList<UsbDeviceInfo> CacheRoutableUsbInventory(IReadOnlyList<UsbDeviceInfo> devices, long version)
    {
        var snapshot = devices.ToArray();
        lock (_usbInventoryLock)
        {
            _routableUsbInventory = snapshot;
            _routableUsbInventoryTimestamp = Environment.TickCount64;
            _routableUsbInventoryVersion = version;
        }

        return [.. snapshot];
    }

    /// <summary>Drops the cached USB inventory so a direct open can re-resolve a device whose key changed
    /// after a server stop, driver rebind or re-enumeration.
    /// <para>丢弃缓存的 USB 清单，使直连打开能在 server 停止、驱动重绑定或重枚举导致键变化后重新解析设备。</para>
    /// </summary>
    internal void InvalidateUsbInventoryCache()
    {
        lock (_usbInventoryLock)
        {
            _usbInventory = null;
            _usbInventoryTimestamp = 0;
            _routableUsbInventory = null;
            _routableUsbInventoryTimestamp = 0;
        }
    }

    private bool IsUsbInventoryFresh(UsbDeviceInfo[]? cached, long timestamp, long version)
        => cached is not null
            && Environment.TickCount64 - timestamp < UsbInventoryCacheMilliseconds
            && version == Interlocked.Read(ref _scanVersion);

    /// <summary>
    /// Lists the routed server and direct USB targets.
    /// <para>列出已路由的 server 目标与直连 USB 目标。</para>
    /// </summary>
    /// <returns>Each ADB target with its serial and state. <para>每个 ADB 目标的序列号与状态。</para></returns>
    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
        => [.. Scan().Where(device => device.Protocol == DeviceProtocolKind.Adb)
            .Select(device => (device.Serial, device.Key.StartsWith("adb:", StringComparison.Ordinal)
                ? device.State : ProbeAdb(device) ? "device" : "offline"))];

    /// <summary>
    /// Forwards a service to the exact routed ADB target.
    /// <para>把服务转发到指定的已路由 ADB 目标。</para>
    /// </summary>
    /// <param name="serial">Serial of the routed ADB target. <para>已路由 ADB 目标的序列号。</para></param>
    /// <param name="service">Name of the service stream to open. <para>要打开的服务流名称。</para></param>
    /// <param name="operation">Function run over the opened service stream. <para>在打开的服务流上执行的函数。</para></param>
    /// <returns>The value produced by the operation. <para>操作返回的值。</para></returns>
    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(operation);
        return RunAdb(RequireUniqueAdbTarget(serial), session =>
        {
            using var stream = session.OpenServiceStream(service);
            return operation(stream);
        });
    }

    /// <summary>
    /// Uploads through the exact routed ADB target without parking a thread on the transfer.
    /// <para>通过指定的已路由 ADB 目标上传，不会为传输占用线程。</para>
    /// </summary>
    /// <param name="device">Target device receiving the file. <para>接收文件的设备。</para></param>
    /// <param name="localPath">Path of the local source file. <para>本地源文件路径。</para></param>
    /// <param name="remotePath">Path on the device to write. <para>设备上要写入的路径。</para></param>
    /// <param name="cancellationToken">Token cancelling the transfer. <para>用于取消传输的令牌。</para></param>
    public Task PushAdbFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        return RunAdbAsync(device, async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var sync = session.CreateSyncClient();
            await sync.PushAsync(localPath, remotePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads through the exact routed ADB target without parking a thread on the transfer.
    /// <para>通过指定的已路由 ADB 目标下载，不会为传输占用线程。</para>
    /// </summary>
    /// <param name="device">Target device the file is read from. <para>读取文件的设备。</para></param>
    /// <param name="remotePath">Path on the device to read. <para>设备上要读取的路径。</para></param>
    /// <param name="localPath">Path of the local destination file. <para>本地目标文件路径。</para></param>
    /// <param name="cancellationToken">Token cancelling the transfer. <para>用于取消传输的令牌。</para></param>
    public Task PullAdbFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return RunAdbAsync(device, async session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var sync = session.CreateSyncClient();
            await sync.PullAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private DetectedDevice RequireUniqueAdbTarget(string serial)
    {
        var matches = Scan().Where(device => device.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(device.Serial, serial, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
            throw new IOException("The selected ADB target is missing or ambiguous.");
        return matches[0];
    }

    /// <summary>
    /// Runs a Fastboot operation only when exactly one Fastboot device is present.
    /// <para>仅在恰好连接一个 Fastboot 设备时执行该 Fastboot 操作。</para>
    /// </summary>
    /// <param name="operation">Function run against the connected Fastboot session. <para>在已连接的 Fastboot 会话上执行的函数。</para></param>
    /// <returns>The value produced by the operation. <para>操作返回的值。</para></returns>
    public TResult RunSingleFastboot<TResult>(Func<FastbootSession, TResult> operation)
    {
        var devices = Scan().Where(device => device.Protocol == DeviceProtocolKind.Fastboot).ToArray();
        if (devices.Length != 1)
            throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
        return RunFastboot(devices[0], operation);
    }

    internal TResult RunFastboot<TResult>(DetectedDevice device, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Fastboot)
            throw new ArgumentException("A Fastboot target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Fastboot, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<FastbootSession>(out var session))
                throw new IOException("The selected device does not expose Fastboot.");
            cancellationToken.ThrowIfCancellationRequested();
            return operation(session);
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal void RunFastboot(DetectedDevice device, Action<FastbootSession> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = RunFastboot(device, session =>
        {
            operation(session);
            return true;
        }, cancellationToken);
    }

    internal Task<TResult> RunEdlAsync<TResult>(DetectedDevice device,
        Func<EdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        EnsureEdlLivenessProbe();
        return _edlConnections.RunAsync(device, operation, cancellationToken);
    }

    internal TResult RunAdb<TResult>(DetectedDevice device, Func<IAdbDeviceSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var (lease, session) = LeaseAdb(device, cancellationToken);
        try
        {
            using (session)
            {
                session.Connect();
                if (!session.IsConnected)
                    throw new InvalidOperationException("ADB session is not connected.");
                cancellationToken.ThrowIfCancellationRequested();
                return operation(session);
            }
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunAdbAsync<TResult>(DetectedDevice device,
        Func<IAdbDeviceSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var (lease, session) = LeaseAdb(device, cancellationToken);
        try
        {
            using (session)
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!session.IsConnected)
                    throw new InvalidOperationException("ADB session is not connected.");
                cancellationToken.ThrowIfCancellationRequested();
                return await operation(session).ConfigureAwait(false);
            }
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunRkdlAsync<TResult>(DetectedDevice device,
        Func<RkdlSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await RunRkdlOnLeaseAsync(device,
            (session, _, _) => operation(session), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an RKDL loader-download phase and, when the device left maskrom mode, releases that lease,
    /// waits for the re-enumerated loader endpoint and runs the flash phase on a freshly opened RKDL
    /// session.
    /// <para>执行 RKDL loader 下载阶段；当设备离开 maskrom 模式时释放该租约，等待重枚举出的 loader
    /// 端点，并在重新打开的 RKDL 会话上执行刷写阶段。</para>
    /// </summary>
    /// <remarks>
    /// <para>The loader-download lease is always released before the reconnection wait, so no bulk
    /// command is ever sent over the old maskrom handle. A loader-download phase that returns
    /// <see langword="null"/> means the device was already in loader mode, and the flash phase runs on
    /// the same lease without a reconnection. The reconnection uses
    /// <see cref="RkdlLoaderTransitionOffer"/> evidence captured from the released lease; a timeout or
    /// missing USB container evidence stops the operation with an error instead of falling back to the
    /// old session.</para>
    /// <para>loader 下载租约总是在等待重连前释放，因此绝不会在旧的 maskrom 句柄上发送批量命令。loader
    /// 下载阶段返回 <see langword="null"/> 表示设备已处于 loader 模式，刷写阶段在同一租约上执行而无需
    /// 重连。重连使用从已释放租约捕获的 <see cref="RkdlLoaderTransitionOffer"/> 证据；超时或缺少 USB
    /// 容器证据时以错误停止，而不会回退到旧会话。</para>
    /// <para>The RKDL session cannot observe its own mode transition: its device-info snapshot is taken
    /// when the session opens. Re-observation therefore happens only through the device layer, and the
    /// PID-switching transition still needs real-device confirmation.</para>
    /// <para>RKDL 会话无法观测自身的模式迁移：其设备信息快照在会话打开时拍摄。因此重新观测只通过设备层
    /// 进行，切换 PID 的迁移仍需真机确认。</para>
    /// </remarks>
    /// <param name="device">The RKDL device to operate on. <para>要操作的 RKDL 设备。</para></param>
    /// <param name="loaderPhase">The loader phase; returns a transition offer when a loader was
    /// downloaded, or <see langword="null"/> when no transition is expected.
    /// <para>loader 阶段；下载了 loader 时返回迁移提议，不需要迁移时返回 <see langword="null"/>。</para></param>
    /// <param name="flashPhase">The flash phase run on either the original or the re-enumerated session.
    /// <para>在原始会话或重枚举后的会话上执行的刷写阶段。</para></param>
    /// <param name="cancellationToken">Cancellation for discovery, waiting and both phases.
    /// <para>用于发现、等待与两个阶段的取消令牌。</para></param>
    /// <returns>The value produced by the flash phase. <para>刷写阶段产生的值。</para></returns>
    /// <exception cref="InvalidOperationException">The device did not re-enumerate into loader mode
    /// within the offer timeout, the previous endpoint lacks the USB evidence the controller requires
    /// to identify its replacement, or the replacement still reports a maskrom/bootrom product ID.
    /// <para>设备未在提议超时内重枚举为 loader 模式、旧端点缺少控制器识别替代端点所需的 USB 证据，
    /// 或替代端点仍报告 maskrom/bootrom 产品 ID。</para></exception>
    /// <exception cref="IOException">The re-enumerated endpoint does not expose an RKDL session.
    /// <para>重枚举出的端点未提供 RKDL 会话。</para></exception>
    internal async Task<TResult> RunRkdlLoaderTransitionAsync<TResult>(DetectedDevice device,
        Func<RkdlSession, CancellationToken, Task<RkdlLoaderTransitionOffer?>> loaderPhase,
        Func<RkdlSession, CancellationToken, Task<TResult>> flashPhase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loaderPhase);
        ArgumentNullException.ThrowIfNull(flashPhase);
        RkdlLoaderTransitionOffer? observedOffer = null;
        var transitionExpected = await RunRkdlOnLeaseAsync(device, async (session, lease, token) =>
        {
            var observed = await loaderPhase(session, token).ConfigureAwait(false);
            observedOffer = observed is null
                ? null
                : observed with
                {
                    PreviousEndpoint = lease.Endpoint,
                    ObservedGeneration = lease.Generation,
                };
            return observed is not null;
        }, cancellationToken).ConfigureAwait(false);

        if (!transitionExpected)
        {
            return await RunRkdlOnLeaseAsync(device,
                (session, _, token) => flashPhase(session, token), cancellationToken).ConfigureAwait(false);
        }

        var offer = observedOffer!;
        var previous = offer.PreviousEndpoint
            ?? throw new InvalidOperationException("The loader transition offer carries no previous RKDL endpoint.");
        DeviceLease replacement;
        try
        {
            var timeoutMs = (int)Math.Clamp(offer.Timeout.TotalMilliseconds, 1, 86_400_000);
            try
            {
                replacement = await _controller.WaitForUsbReconnectAsync(previous,
                    DeviceSessionKind.Bootrom, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException) when (previous.UsbInfo?.ContainerId is null)
            {
                replacement = await _controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Bootrom,
                    UsbReconnectIdentityPolicy.PhysicalPort, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is TimeoutException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The device did not re-enumerate as a Rockchip loader after the loader download; the maskrom lease was released and the flash was stopped instead of reusing that handle. 下载 loader 后设备未以 Rockchip loader 模式重枚举；已释放 maskrom 租约并停止刷写，不会复用旧句柄。",
                error);
        }

        try
        {
            if (!replacement.TryGetSession<RkdlSession>(out var session))
            {
                throw new IOException("The re-enumerated device does not expose an RKDL session.");
            }

            if (!session.IsRockusbMode)
            {
                throw new InvalidOperationException(
                    $"The re-enumerated Rockchip device reports product ID 0x{session.ProductId:X4}, which the PID-only loader table classifies as maskrom/bootrom, so bulk flash commands are not sent over that handle. 重新枚举出的 Rockchip 设备报告的产品 ID 被仅按 PID 的 loader 表归类为 maskrom/bootrom，因此不会在该句柄上发送批量刷写命令。");
            }

            return await flashPhase(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(replacement);
        }
    }

    private async Task<TResult> RunRkdlOnLeaseAsync<TResult>(DetectedDevice device,
        Func<RkdlSession, DeviceLease, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Rkdl)
            throw new ArgumentException("An RKDL target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Bootrom, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<RkdlSession>(out var session))
                throw new IOException("The selected device does not expose an RKDL session.");
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(lease);
        }
    }

    internal async Task<TResult> RunMtkAsync<TResult>(DetectedDevice device,
        Func<MtkSession, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Mtk)
            throw new ArgumentException("An MTK target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Mtk, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<MtkSession>(out var session))
                throw new IOException("The selected device does not expose an MTK session.");
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(lease);
        }
    }
}