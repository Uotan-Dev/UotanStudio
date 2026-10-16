using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Enumerates the USB devices visible through the current discovery snapshot.
    /// <para>枚举当前发现快照中可见的 USB 设备。</para>
    /// </summary>
    /// <returns>The USB device inventory. <para>USB 设备清单。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => _controller.EnumerateUsb();

    /// <summary>
    /// Lists the direct-USB interfaces left in the snapshot; debug bridge servers own their
    /// protocol's interfaces, and discovery already removed those endpoints.
    /// <para>列出快照中剩余的直连 USB 接口；调试桥服务持有自身协议的接口，这些端点已在发现阶段被移除。</para>
    /// </summary>
    /// <returns>The routable direct-USB interfaces. <para>可路由的直连 USB 接口。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> ListRoutableUsbDevices()
    {
        var snapshot = _controller.RefreshAsync().GetAwaiter().GetResult();
        return [.. snapshot.Endpoints.Where(endpoint =>
            endpoint.Source == Comm.Devices.Discovery.UsbDiscoverySource.SourceName &&
            endpoint.Route == "direct-usb" && endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!)];
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Rkdl)
            throw new ArgumentException("An RKDL target is required.", nameof(device));
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Bootrom, cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<RkdlSession>(out var session))
                throw new IOException("The selected device does not expose an RKDL session.");
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session).ConfigureAwait(false);
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