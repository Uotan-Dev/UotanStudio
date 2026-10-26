using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Apple.Lockdown;
using UotanStudio.Comm.Protocols.Apple.Usbmuxd;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// A device-port connection factory over one Apple multiplexing transport, shared by the usbmuxd
/// daemon route and the direct-USB multiplexer route.
/// <para>单一 Apple 复用传输之上的设备端口连接工厂，由 usbmuxd 守护进程路由与直连 USB 复用路由共享。</para>
/// </summary>
internal interface IAppleMuxChannel : IDisposable
{
    /// <summary>Gets the device serial (UDID) the channel is bound to. <para>获取通道绑定的设备序列号（UDID）。</para></summary>
    string Serial { get; }

    /// <summary>Gets the mux-reported connection type, <see cref="UsbmuxdDevice.ConnectionTypeUsb"/> or network.
    /// <para>获取复用层上报的连接类型，<see cref="UsbmuxdDevice.ConnectionTypeUsb"/> 或网络。</para></summary>
    string ConnectionType { get; }

    /// <summary>
    /// Verifies the device is currently attached and returns its mux device identifier.
    /// <para>确认设备当前在线并返回其复用层设备标识。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe. <para>取消探测。</para></param>
    /// <returns>The mux device identifier. <para>复用层设备标识。</para></returns>
    /// <exception cref="IOException">The device is not attached. <para>设备不在线时抛出。</para></exception>
    int ResolveDeviceId(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a raw stream to a device TCP port.
    /// <para>打开到设备 TCP 端口的原始流。</para>
    /// </summary>
    /// <param name="port">The device port. <para>设备端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    Stream ConnectDevicePort(int port, CancellationToken cancellationToken);
}

/// <summary>
/// Apple (usbmuxd/lockdown) protocol session; implements the public contract consumed by the host.
/// <para>Apple（usbmuxd/lockdown）协议会话；实现宿主消费的公开契约。</para>
/// </summary>
internal sealed class AppleSession : IProtocolSession, IAppleDeviceSession
{
    private readonly LockdownPairingRecordStore _recordStore;
    private readonly Lock _lock = new();
    private IAppleMuxChannel? _channel;
    private LockdownClient? _lockdown;
    private int _deviceId;
    private bool _disposed;

    private AppleSession(IAppleMuxChannel channel, LockdownPairingRecordStore recordStore)
    {
        _channel = channel;
        _recordStore = recordStore;
    }

    /// <summary>
    /// Creates a session routed through the host usbmuxd daemon.
    /// <para>创建经由主机 usbmuxd 守护进程路由的会话。</para>
    /// </summary>
    /// <param name="serial">The device serial (UDID). <para>设备序列号（UDID）。</para></param>
    /// <param name="options">Optional daemon endpoint options. <para>可选的守护进程端点配置。</para></param>
    /// <param name="recordStore">Optional pairing record store. <para>可选的配对记录存储。</para></param>
    /// <returns>The session bound to the daemon route. <para>绑定到守护进程路由的会话。</para></returns>
    public static AppleSession ForUsbmuxd(string serial, UsbmuxdClientOptions? options = null,
        LockdownPairingRecordStore? recordStore = null) =>
        new(new UsbmuxdMuxChannel(serial, options ?? UsbmuxdClientOptions.Default),
            recordStore ?? new LockdownPairingRecordStore());

    /// <summary>
    /// Creates a session speaking the mux protocol directly over the Apple USB interface.
    /// <para>创建经 Apple USB 接口直接讲复用协议的会话。</para>
    /// </summary>
    /// <param name="usb">The freshly opened USB device session. <para>新打开的 USB 设备会话。</para></param>
    /// <param name="serial">The device serial (UDID). <para>设备序列号（UDID）。</para></param>
    /// <returns>The session bound to the direct-USB route. <para>绑定到直连 USB 路由的会话。</para></returns>
    public static AppleSession ForDirectUsb(IUsbDeviceSession usb, string serial) =>
        new(new AppleUsbDirectMuxChannel(usb, serial), new LockdownPairingRecordStore());

    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    /// <value>The fixed tag <c>apple</c>. <para>固定标记 <c>apple</c>。</para></value>
    public string ProtocolName => "apple";

    /// <summary>Gets the underlying transport; Apple sessions carry their own connections.
    /// <para>获取底层传输；Apple 会话持有自己的连接。</para></summary>
    /// <exception cref="NotSupportedException">Always; Apple sessions do not expose a raw transport.
    /// <para>总是抛出；Apple 会话不暴露原始传输。</para></exception>
    public ICommTransport Transport => throw new NotSupportedException(
        "Apple sessions own their multiplexer connections and do not expose a raw transport.");

    /// <summary>Gets a value indicating whether the session can reach the device. <para>获取会话能否到达设备。</para></summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _deviceId != 0;
            }
        }
    }

    /// <summary>Gets the device serial (UDID). <para>获取设备序列号（UDID）。</para></summary>
    public string Serial
    {
        get
        {
            lock (_lock)
            {
                return Channel().Serial;
            }
        }
    }

    /// <inheritdoc/>
    public Task<int> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _deviceId = Channel().ResolveDeviceId(cancellationToken);
            return Task.FromResult(_deviceId);
        }
    }

    /// <inheritdoc/>
    public Task<AppleDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = Channel();
            _deviceId = channel.ResolveDeviceId(cancellationToken);
            var lockdown = EnsureLockdown(channel, cancellationToken);
            var values = lockdown.GetAllValues(cancellationToken);
            var trustState = _recordStore.Load(channel.Serial) is null ? AppleTrustState.Unpaired
                : lockdown.PairRecord is null ? AppleTrustState.Untrusted
                : AppleTrustState.Trusted;
            int? battery = lockdown.GetValue("com.apple.mobile.battery", "BatteryCurrentCapacity", cancellationToken)
                is long level ? (int)level : null;
            var info = new AppleDeviceInfo(
                Serial: channel.Serial,
                DeviceId: _deviceId,
                Name: values.TryGetValue("DeviceName", out var name) && name is string nameText ? nameText : null,
                ProductType: values.TryGetValue("ProductType", out var productType) && productType is string typeText ? typeText : null,
                ProductVersion: values.TryGetValue("ProductVersion", out var version) && version is string versionText ? versionText : null,
                ModelNumber: values.TryGetValue("ModelNumber", out var model) && model is string modelText ? modelText : null,
                CpuArchitecture: values.TryGetValue("CPUArchitecture", out var cpu) && cpu is string cpuText ? cpuText : null,
                WifiAddress: values.TryGetValue("WiFiAddress", out var wifi) && wifi is string wifiText ? wifiText : null,
                ConnectionType: channel.ConnectionType,
                ActivationState: values.TryGetValue("ActivationState", out var activation) && activation is string activationText ? activationText : null,
                BatteryPercent: battery,
                TrustState: trustState);
            return Task.FromResult(info);
        }
    }

    /// <inheritdoc/>
    public Stream OpenService(string serviceName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = Channel();
            _deviceId = channel.ResolveDeviceId(cancellationToken);
            var lockdown = EnsureLockdown(channel, cancellationToken);
            if (lockdown.PairRecord is null)
            {
                PairDevice(lockdown, channel, cancellationToken);
            }

            return lockdown.StartService(serviceName, cancellationToken);
        }
    }

    /// <summary>
    /// Opens a raw device port, for example for a port forward; pairing is not required.
    /// <para>打开原始设备端口（例如端口转发）；无需配对。</para>
    /// </summary>
    /// <param name="port">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect. <para>取消连接。</para></param>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    public Stream OpenDevicePort(int port, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var channel = Channel();
            channel.ResolveDeviceId(cancellationToken);
            return channel.ConnectDevicePort(port, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lockdown?.Dispose();
            _channel?.Dispose();
        }
    }

    private IAppleMuxChannel Channel() => _disposed
        ? throw new ObjectDisposedException(nameof(AppleSession))
        : _channel ?? throw new InvalidOperationException("The Apple session channel is not initialized.");

    private LockdownClient EnsureLockdown(IAppleMuxChannel channel, CancellationToken cancellationToken)
    {
        if (_lockdown is not null)
        {
            return _lockdown;
        }

        var stream = channel.ConnectDevicePort(AppleConstants.LockdownPort, cancellationToken);
        _lockdown = new LockdownClient(stream, channel.ConnectDevicePort, _recordStore.Load(channel.Serial));
        return _lockdown;
    }

    private void PairDevice(LockdownClient lockdown, IAppleMuxChannel channel, CancellationToken cancellationToken)
    {
        var devicePublicKey = lockdown.GetValue(null, "DevicePublicKey", cancellationToken) as byte[]
            ?? throw new LockdownException("The device did not report its public key.");
        var wifiMac = lockdown.GetValue(null, "WiFiAddress", cancellationToken) as string;
        var record = LockdownPairingRecord.CreatePending(
            Guid.NewGuid().ToString("N").ToUpperInvariant(),
            _recordStore.GetSystemBuid(),
            devicePublicKey,
            wifiMac);
        lockdown.Pair(record, cancellationToken);
        _recordStore.Save(channel.Serial, record);
    }
}

/// <summary>
/// Mux channel routed through the host usbmuxd daemon.
/// <para>经由主机 usbmuxd 守护进程路由的复用通道。</para>
/// </summary>
internal sealed class UsbmuxdMuxChannel : IAppleMuxChannel
{
    private readonly UsbmuxdClient _client;
    private int? _deviceId;
    private string _connectionType = UsbmuxdDevice.ConnectionTypeUsb;

    public UsbmuxdMuxChannel(string serial, UsbmuxdClientOptions options) =>
        (_client, Serial) = (new UsbmuxdClient(options), serial);

    public string Serial { get; }

    public string ConnectionType => _connectionType;

    public int ResolveDeviceId(CancellationToken cancellationToken)
    {
        if (_deviceId is { } cached)
        {
            return cached;
        }

        foreach (var device in _client.ListDevices(cancellationToken))
        {
            if (string.Equals(device.SerialNumber.Replace("-", string.Empty, StringComparison.Ordinal),
                    Serial.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                _deviceId = device.DeviceId;
                _connectionType = device.ConnectionType;
                return device.DeviceId;
            }
        }

        throw new IOException($"The usbmuxd daemon does not know a device with serial {Serial}.");
    }

    public Stream ConnectDevicePort(int port, CancellationToken cancellationToken) =>
        _client.Connect(_deviceId ?? ResolveDeviceId(cancellationToken), port, cancellationToken);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Mux channel speaking the Apple USB multiplexer protocol directly over the device USB interface
/// (implemented in the direct-USB backend).
/// <para>经设备 USB 接口直接讲 Apple USB 复用协议的复用通道（由直连 USB 后端实现）。</para>
/// </summary>
internal sealed class AppleUsbDirectMuxChannel : IAppleMuxChannel
{
    private readonly IUsbDeviceSession _usb;
    private readonly Lock _lock = new();
    private AppleUsbMuxDirect? _mux;

    public AppleUsbDirectMuxChannel(IUsbDeviceSession usb, string serial) => (_usb, Serial) = (usb, serial);

    public string Serial { get; }

    public string ConnectionType => UsbmuxdDevice.ConnectionTypeUsb;

    public int ResolveDeviceId(CancellationToken cancellationToken)
    {
        EnsureMux(cancellationToken);
        return 1;
    }

    public Stream ConnectDevicePort(int port, CancellationToken cancellationToken) =>
        EnsureMux(cancellationToken).Connect(port, cancellationToken);

    private AppleUsbMuxDirect EnsureMux(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _mux ??= AppleUsbMuxDirect.Attach(_usb, cancellationToken);
            return _mux;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _mux?.Dispose();
            _usb.Dispose();
        }
    }
}