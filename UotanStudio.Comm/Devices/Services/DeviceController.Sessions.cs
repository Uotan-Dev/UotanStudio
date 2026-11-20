using System.Globalization;
using System.IO.Ports;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;

namespace UotanStudio.Comm.Devices.Services;

public sealed partial class DeviceController
{
    /// <summary>Leases the exact endpoint and builds the requested protocol session on top of the opened transport.
    /// <para>租用精确端点，并在已打开的传输之上构建所请求的协议会话。</para>The kind selects only the session factory; route decisions and identity validation happened in discovery and the transport opener, and a factory failure never falls back to another route.<para>会话类型仅选择会话工厂；路由决策与身份校验已在发现和传输开启阶段完成， 工厂失败绝不会回退到其他路由。</para></summary>
    /// <param name="snapshot">The live snapshot the endpoint was taken from; a stale snapshot is rejected.
    /// <para>端点所来自的当前快照；过期快照会被拒绝。</para></param>
    /// <param name="id">Exact endpoint identity to lease.<para>待租用的精确端点身份。</para></param>
    /// <param name="kind">Session kind selecting the protocol factory.
    /// <para>用于选择协议工厂的会话类型。</para></param>
    /// <param name="cancellationToken">Cancels the open before the transport is acquired; a leased endpoint is released on cancellation.
    /// <para>在传输取得之前取消开启；取消时已租用的端点会被归还。</para></param>
    /// <returns>The lease owning the transport and the built session.
    /// <para>持有传输与已构建会话的租约。</para></returns>
    /// <exception cref="InvalidOperationException">The snapshot is stale, the endpoint is already leased, or the opener returned no transport.
    /// <para>快照过期、端点已被租用，或开启器未返回传输。</para></exception>
    /// <exception cref="KeyNotFoundException">The endpoint is not present in the snapshot.
    /// <para>快照中不存在该端点。</para></exception>
    /// <exception cref="NotSupportedException">No session factory is registered for <paramref name="kind"/>.
    /// <para>未为 <paramref name="kind"/> 注册会话工厂。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        DeviceSessionKind kind, CancellationToken cancellationToken = default)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_factories.TryGetValue(kind, out var factory))
            {
                throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(id, lease!), generation);
            lock (_gate)
            {
                _openLeases[id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(id, null);
            }

            throw;
        }
    }

    /// <summary>Opens the USB device by its stable key and leases it with the requested protocol session; returns null when the key cannot be opened. The server-ownership guard fails closed inside the native open, and sessions are built only for the protocol observed on the endpoint.
    /// <para>按稳定键打开 USB 设备并以指定协议会话租用，键无法打开时返回 null。server 所有权守卫在原生打开内部失败即关闭，且仅对端点上观测到的协议构建会话。</para></summary>
    /// <param name="deviceKey">Nonempty stable device key of the physical node to open.
    /// <para>待打开物理节点的非空稳定设备键。</para></param>
    /// <param name="kind">Session kind selecting the protocol factory.
    /// <para>用于选择协议工厂的会话类型。</para></param>
    /// <param name="cancellationToken">Checked before opening and passed to the session factory.
    /// <para>开启前检查，并传递给会话工厂。</para></param>
    /// <returns>The lease, or <see langword="null"/> when no device matches the key.
    /// <para>租约；没有设备匹配该键时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="deviceKey"/> is null, empty, or whitespace.
    /// <para><paramref name="deviceKey"/> 为 null、空白或仅含空白字符。</para></exception>
    /// <exception cref="NotSupportedException">The controller has no communication kit, or no factory is registered for <paramref name="kind"/>.
    /// <para>控制器没有通信套件，或未为 <paramref name="kind"/> 注册工厂。</para></exception>
    /// <exception cref="InvalidOperationException">The synthesized endpoint is already leased.
    /// <para>合成的端点已被租用。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease?> OpenLeaseByKeyAsync(string deviceKey, DeviceSessionKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_kit is null)
        {
            throw new NotSupportedException("This controller was composed without a communication kit and cannot open by key.");
        }

        var usb = _kit.OpenUsbDeviceSessionByKey(deviceKey);
        if (usb is null)
        {
            return null;
        }

        var info = usb.DeviceInfo;
        var endpoint = new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = info.SourceApiKind.ToString(),
            Address = deviceKey,
            DisplayName = info.SerialNumber,
            Serial = info.SerialNumber,
            Route = "direct-usb",
            Protocol = UsbProtocolClassifier.Classify(info),
            RouteEpoch = DebugBridgeRoute.Current.Epoch,
            UsbInfo = info,
            Usb = new(info.SourceApiKind, info.DevicePath, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };
        lock (_gate)
        {
            try
            {
                ValidateRouteForOpen(endpoint);
            }
            catch
            {
                usb.Dispose();
                throw;
            }

            if (!_leased.Add(endpoint.Id))
            {
                usb.Dispose();
                throw new DeviceEndpointBusyException(endpoint);
            }
        }

        IAsyncDisposable transport = new OwnedDeviceTransport<IUsbDeviceSession>(usb);
        try
        {
            if (!_factories.TryGetValue(kind, out var factory))
            {
                throw new NotSupportedException($"No session factory is registered for {kind} sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!), CurrentGenerationFor(info));
            lock (_gate)
            {
                _openLeases[endpoint.Id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(endpoint.Id, null);
            }

            throw;
        }
    }

    /// <summary>
    /// Handshakes an MTK boot session over the exact device-interface path an OS arrival broadcast reported,
    /// trying the VCOM serial port first and the USB interfaces second. Both shapes exist in the field: a
    /// preloader driven by a vendor VCOM driver publishes a COM port, while one bound to WinUSB (Class
    /// <c>USBDevice</c>, service <c>WINUSB</c>) publishes none. A full inventory walk costs more than the few
    /// seconds such a port accepts the handshake for, so this path skips it entirely.
    /// <para>在操作系统到达广播给出的精确设备接口路径上握手 MTK boot 会话，先试 VCOM 串口再试 USB 接口。
    /// 两种形态都存在于实际环境：由厂商 VCOM 驱动驱动的 preloader 会公布 COM 口，而绑定 WinUSB
    ///（Class <c>USBDevice</c>、服务 <c>WINUSB</c>）的不会公布。整棵清单遍历比这类端口接受握手的数秒更慢，
    /// 因此本路径完全跳过遍历。</para>
    /// </summary>
    /// <param name="devicePath">The exact device-interface path from the arrival broadcast.
    /// <para>到达广播携带的精确设备接口路径。</para></param>
    /// <param name="baudRate">The serial line speed used on the VCOM attempt. <para>VCOM 尝试使用的串口波特率。</para></param>
    /// <param name="cancellationToken">Cancels the open and handshake. <para>取消打开与握手。</para></param>
    /// <returns>The lease holding the handshaken session, or <see langword="null"/> when the device answers
    /// neither shape. <para>持有已握手会话的租约；两种形态都无应答时为 <see langword="null"/>。</para></returns>
    public async Task<DeviceLease?> HandshakeMtkArrivalPathAsync(string devicePath, int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var serial = MtkSerialPortFallback.TryOpenConnected(devicePath, baudRate);
        if (serial is not null)
        {
            var lease = await BuildSerialLeaseAsync(devicePath, serial, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }
        }

        return await OpenLeaseByKeyAsync(BuildArrivalKey(devicePath), DeviceSessionKind.Mtk, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps an already-opened serial transport in an MTK lease and runs the boot handshake on it.
    /// <para>把已打开的串口传输包装为 MTK 租约，并在其上执行 boot 握手。</para>
    /// </summary>
    private async Task<DeviceLease?> BuildSerialLeaseAsync(string devicePath, SerialTransport serial,
        CancellationToken cancellationToken)
    {
        var (vid, pid) = TryParsePathVidPid(devicePath);
        return await BuildSerialLeaseCoreAsync(devicePath, devicePath, serial, vid, pid,
            new UsbEndpointDetails(UsbApiKind.Native, devicePath, vid, pid, null, null, null),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handshakes an MTK boot session over an explicitly named serial port, without any device-path resolution or
    /// USB inventory walk. This is the arrival path for a watcher that only knows the port name — a VCOM preloader
    /// publishes its COM port for a few seconds and every step between the port appearing and the handshake costs
    /// part of that window.
    /// <para>在显式指定的串口名上握手 MTK boot 会话，不做任何设备路径解析或 USB 清单遍历。这是只知端口名的
    /// 监视器所用的到达路径——VCOM preloader 只公布数秒 COM 口，端口出现与握手之间的每一步都在消耗该窗口。</para>
    /// </summary>
    /// <param name="portName">The exact COM/TTY port to open. <para>要打开的确切 COM/TTY 端口名。</para></param>
    /// <param name="baudRate">The serial line speed. <para>串口线路速度。</para></param>
    /// <param name="cancellationToken">Cancels the open and handshake. <para>取消打开与握手。</para></param>
    /// <returns>The lease owning the serial transport and the handshaken MTK session, or <see langword="null"/>
    /// when the endpoint is already leased. <para>持有串口传输与已握手 MTK 会话的租约；端点已被租用时为
    /// <see langword="null"/>。</para></returns>
    public async Task<DeviceLease?> HandshakeMtkSerialPortAsync(string portName, int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var serial = new SerialTransport(portName, baudRate, Parity.None, 8, StopBits.One, Handshake.None);
        try
        {
            await serial.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            serial.Dispose();
            throw;
        }

        return await BuildSerialLeaseCoreAsync(portName, portName, serial, vid: 0, pid: 0, usb: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps an already-opened serial transport in an MTK lease, runs the boot handshake on it and registers the
    /// lease. The endpoint identity is built from the given address; USB details are optional because a watcher
    /// that only knows a port name has none, and a lease without them selects the preloader wake-up handshake.
    /// <para>把已打开的串口传输包装为 MTK 租约，在其上执行 boot 握手并登记。端点身份由给定地址构建；
    /// USB 详情可选——只知端口名的监视器没有这些详情，缺少它们的租约会选中 preloader 唤醒握手。</para>
    /// </summary>
    private async Task<DeviceLease> BuildSerialLeaseCoreAsync(string address, string displayName,
        SerialTransport serial, ushort vid, ushort pid, UsbEndpointDetails? usb,
        CancellationToken cancellationToken)
    {
        var endpoint = new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = "Serial",
            Address = address,
            DisplayName = displayName,
            Serial = displayName,
            Route = "direct-usb",
            Protocol = "mtk",
            RouteEpoch = DebugBridgeRoute.Current.Epoch,
            Usb = usb
        };

        lock (_gate)
        {
            ValidateRouteForOpen(endpoint);
            if (!_leased.Add(endpoint.Id))
            {
                serial.Dispose();
                throw new DeviceEndpointBusyException(endpoint);
            }
        }

        IAsyncDisposable transport = new OwnedDeviceTransport<SerialTransport>(serial);
        try
        {
            if (!_factories.TryGetValue(DeviceSessionKind.Mtk, out var factory))
            {
                throw new NotSupportedException("No session factory is registered for MTK sessions.");
            }

            var session = (MtkSession)await factory.CreateAsync(endpoint, transport, cancellationToken)
                .ConfigureAwait(false);
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!));
            lock (_gate)
            {
                _openLeases[endpoint.Id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(endpoint.Id, null);
            }

            throw;
        }
    }
    /// <summary>
    /// Builds the stable key shape <see cref="UsbDeviceIdentity.BuildKey"/> produces, from an arrival path alone.
    /// The broadcast carries no VID/PID of its own, so they are parsed out of the path itself.
    /// <para>仅凭到达路径构造 <see cref="UsbDeviceIdentity.BuildKey"/> 产出的稳定键形态。广播本身不携带 VID/PID，
    /// 因此从路径中解析。</para>
    /// </summary>
    private static string BuildArrivalKey(string devicePath)
    {
        var (vid, pid) = TryParsePathVidPid(devicePath);
        return string.Join('|', string.Empty, string.Empty,
            vid.ToString("X4"), pid.ToString("X4"), string.Empty, string.Empty, string.Empty, string.Empty, devicePath);
    }

    private static (ushort Vid, ushort Pid) TryParsePathVidPid(string path)
    {
        var vid = ReadHexSegment(path, "vid_");
        var pid = ReadHexSegment(path, "pid_");
        return (vid, pid);
    }

    private static ushort ReadHexSegment(string path, string marker)
    {
        var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + marker.Length + 4 > path.Length)
        {
            return 0;
        }

        return ushort.TryParse(path.AsSpan(start + marker.Length, 4), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var value) ? value : (ushort)0;
    }
    /// <summary>
    /// Opens an MTK boot lease directly over the VCOM serial port bound to an already-discovered USB device,
    /// skipping the USB inventory walk and the WinUSB open attempt that a serial-bound preloader cannot answer.
    /// <para>在已发现的 USB 设备所绑定的 VCOM 串口上直接打开 MTK boot 租约，跳过串口绑定的 preloader 无法应答的
    /// USB 清单遍历与 WinUSB 打开尝试。</para>
    /// </summary>
    /// <param name="info">The USB descriptors from the topology event. <para>拓扑事件携带的 USB 描述符。</para></param>
    /// <param name="baudRate">The serial line speed. <para>串口波特率。</para></param>
    /// <param name="cancellationToken">Cancellation for the open and handshake. <para>打开与握手的取消令牌。</para></param>
    /// <returns>The lease owning the serial transport and the handshaken MTK session, or <see langword="null"/>
    /// when the path resolves to no COM port or the port cannot be opened.
    /// <para>持有串口传输与已握手 MTK 会话的租约；路径无法解析出 COM 口或端口无法打开时为 <see langword="null"/>。</para></returns>
    public async Task<DeviceLease?> OpenVcomLeaseAsync(UsbDeviceInfo info, int baudRate = 115200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(info.DeviceKey))
        {
            throw new ArgumentException("The device must carry an exact USB key.", nameof(info));
        }

        var serial = MtkSerialPortFallback.TryOpenConnected(info.DevicePath, baudRate);
        if (serial is null)
        {
            return null;
        }

        var endpoint = new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = info.SourceApiKind.ToString(),
            Address = info.DeviceKey,
            DisplayName = info.SerialNumber,
            Serial = info.SerialNumber,
            Route = "direct-usb",
            Protocol = "mtk",
            RouteEpoch = DebugBridgeRoute.Current.Epoch,
            UsbInfo = info,
            Usb = new(info.SourceApiKind, info.DevicePath, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };

        lock (_gate)
        {
            ValidateRouteForOpen(endpoint);
            if (!_leased.Add(endpoint.Id))
            {
                serial.Dispose();
                throw new DeviceEndpointBusyException(endpoint);
            }
        }

        IAsyncDisposable transport = new OwnedDeviceTransport<SerialTransport>(serial);
        try
        {
            if (!_factories.TryGetValue(DeviceSessionKind.Mtk, out var factory))
            {
                throw new NotSupportedException("No session factory is registered for MTK sessions.");
            }

            var session = await factory.CreateAsync(endpoint, transport, cancellationToken).ConfigureAwait(false);
            var handshake = (MtkSession)session;
            await handshake.InitializeAsync(cancellationToken).ConfigureAwait(false);
            DeviceLease? lease = null;
            lease = new DeviceLease(endpoint, transport, session, () => Release(endpoint.Id, lease!),
                CurrentGenerationFor(info));
            lock (_gate)
            {
                _openLeases[endpoint.Id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(endpoint.Id, null);
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves the serial port an MTK boot endpoint is bound to without opening it. The resolution walks the
    /// platform's own topology and never touches the device, so callers can key a parked connection by the exact
    /// port the endpoint owns before deciding to open anything.
    /// <para>解析 MTK 启动端点绑定的串口名但不打开它。解析只遍历平台自身拓扑、绝不触碰设备，使调用方在决定
    /// 是否打开任何东西之前就能用端点实际拥有的端口作为暂存连接的键。</para>
    /// </summary>
    /// <param name="info">The USB descriptors of the endpoint. <para>端点的 USB 描述符。</para></param>
    /// <returns>The resolved port name, or <see langword="null"/> when the endpoint publishes no serial port.
    /// <para>解析出的端口名；端点不公布串口时为 <see langword="null"/>。</para></returns>
    public string? ResolveMtkSerialPort(UsbDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return MtkSerialPortFallback.TryResolvePortName(info.DevicePath);
    }
}