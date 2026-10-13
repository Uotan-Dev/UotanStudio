using UotanStudio.Comm.Protocols.Fastboot.Transport;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm;

/// <summary>
/// Exposes the entry points for device discovery, communication, and API registration.
/// Implementations provide both USB transport access and a dedicated protocol layer.
/// <para>暴露设备发现、通信与 API 注册的入口。实现类同时提供 USB 传输访问与专用协议层。</para>
/// </summary>
internal interface ICommKit
{
    /// <summary>
    /// Gets the names of the registered USB APIs.
    /// <para>获取已注册的 USB API 名称列表。</para>
    /// </summary>
    /// <returns>A read-only list of available API names. <para>可用 API 名称只读列表。</para></returns>
    IReadOnlyList<string> GetAvailableUsbApis();

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs.
    /// <para>获取当前已注册 USB API 的能力摘要。</para>
    /// </summary>
    /// <returns>A read-only list of capability summaries. <para>能力摘要只读列表。</para></returns>
    IReadOnlyList<UsbApiCapabilities> GetAvailableUsbApiCapabilities();

    /// <summary>
    /// Enumerates USB devices for the specified API and filter.
    /// <para>按指定 API 与过滤条件枚举 USB 设备。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A read-only list of matched USB device descriptors. <para>匹配到的 USB 设备描述信息只读列表。</para></returns>
    IReadOnlyList<UsbDeviceInfo> EnumerateUsbDevices(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null);

    /// <summary>
    /// Enumerates USB devices asynchronously for the specified API and filter.
    /// <para>按指定 API 与过滤条件异步枚举 USB 设备。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the matched USB device descriptors. <para>返回匹配 USB 设备描述信息的任务。</para></returns>
    Task<IReadOnlyList<UsbDeviceInfo>> EnumerateUsbDevicesAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Monitors USB device additions and removals by polling snapshots.
    /// <para>通过轮询快照监视 USB 设备新增与移除。</para>
    /// </summary>
    /// <param name="onChanged">Change callback. <para>设备变化回调。</para></param>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="pollInterval">Polling interval. <para>轮询间隔。</para></param>
    /// <param name="fireInitialSnapshot">Whether to emit initial Added events. <para>是否触发初始 Added 事件。</para></param>
    /// <param name="onError">Optional error callback invoked when enumeration or callback handling fails. <para>枚举或回调失败时触发的可选错误回调。</para></param>
    /// <returns>A disposable monitor handle. <para>可释放的监视句柄。</para></returns>
    IDisposable MonitorUsbDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null);

    /// <summary>
    /// Opens matching USB device sessions for direct read/write operations.
    /// <para>打开匹配的 USB 设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A disposable collection of opened sessions. <para>已打开会话的可释放集合。</para></returns>
    UsbSessionCollection OpenUsbDeviceSessions(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null);

    /// <summary>
    /// Opens the first matching USB device session for direct read/write operations.
    /// <para>打开第一个匹配的 USB 设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null);

    /// <summary>
    /// Opens the first matching USB device session for direct read/write operations, applying
    /// per-session tuning options. Backends that cannot honour an option ignore it.
    /// <para>打开第一个匹配的 USB 设备会话用于直接读写操作，并应用按会话生效的调优选项；
    /// 无法兑现某选项的后端会忽略它。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind, UsbDeviceFilter? filter, UsbSessionOptions? options);

    /// <summary>
    /// Opens the session whose <see cref="UsbDeviceInfo.DeviceKey"/> matches the specified key.
    /// A stashed key reopens the same physical device after a mode switch changes its VID/PID.
    /// <para>打开设备键与指定键匹配的会话；保存的键可在模式切换改变 VID/PID 后重开同一物理设备。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key from <see cref="UsbDeviceInfo.DeviceKey"/>. <para>来自 <see cref="UsbDeviceInfo.DeviceKey"/> 的稳定设备键。</para></param>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <returns>The matching session, or <c>null</c> when no device matches. <para>匹配的会话；无匹配设备时返回 <c>null</c>。</para></returns>
    IUsbDeviceSession? OpenUsbDeviceSessionByKey(string deviceKey, UsbApiKind apiKind = UsbApiKind.Auto);

    /// <summary>
    /// Waits until at least one device matching the filter appears (250 ms polling).
    /// <para>等待至少一个匹配过滤条件的设备出现（250 ms 轮询）。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when a matching device appeared before the timeout. <para>超时前出现匹配设备时返回 <c>true</c>。</para></returns>
    Task<bool> WaitForDeviceAppearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until no device matching the filter remains (250 ms polling).
    /// <para>等待不再存在匹配过滤条件的设备（250 ms 轮询）。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when no matching device remains before the timeout. <para>超时前不再存在匹配设备时返回 <c>true</c>。</para></returns>
    Task<bool> WaitForDeviceDisappearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the <paramref name="removedFilter"/> devices are gone AND at least one
    /// <paramref name="appearedFilter"/> device is present (mode-switch pattern).
    /// <para>等待 <paramref name="removedFilter"/> 设备消失且至少一个
    /// <paramref name="appearedFilter"/> 设备出现（模式切换模式）。</para>
    /// </summary>
    /// <param name="removedFilter">Filter for devices expected to disappear; pass <c>null</c> to skip this half. <para>预期消失的设备过滤条件；传 <c>null</c> 跳过该半边。</para></param>
    /// <param name="appearedFilter">Filter for devices expected to appear; pass <c>null</c> to skip this half. <para>预期出现的设备过滤条件；传 <c>null</c> 跳过该半边。</para></param>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the mode switch completed before the timeout. <para>超时前完成模式切换时返回 <c>true</c>。</para></returns>
    Task<bool> WaitForModeSwitchAsync(UsbDeviceFilter? removedFilter, UsbDeviceFilter? appearedFilter, UsbApiKind apiKind = UsbApiKind.Auto, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a custom USB API provider.
    /// <para>注册自定义 USB API 提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name to register. <para>要注册的 API 名称。</para></param>
    /// <param name="providerFactory">Factory that creates the provider. <para>创建提供器实例的工厂方法。</para></param>
    /// <returns><c>true</c> when the provider is registered. <para>注册成功时返回 <c>true</c>。</para></returns>
    bool RegisterUsbApi(string apiName, Func<IUsbApiProvider> providerFactory);

    /// <summary>
    /// Enumerates the serial ports currently available on the system.
    /// <para>枚举系统当前可用的串口。</para>
    /// </summary>
    /// <returns>A read-only list of port names such as <c>COM3</c> or <c>/dev/ttyUSB0</c>. <para>端口名称只读列表，如 <c>COM3</c> 或 <c>/dev/ttyUSB0</c>。</para></returns>
    IReadOnlyList<string> GetAvailableSerialPorts();

    /// <summary>
    /// Creates a serial-port transport such as for EDL over UART; the default is 3 Mbaud with
    /// RTS/CTS flow control.
    /// <para>创建串口传输，例如用于 UART 上的 EDL；默认 3 Mbaud 并采用 RTS/CTS 流控。</para>
    /// </summary>
    /// <param name="portName">The serial port name. <para>串口名称。</para></param>
    /// <param name="baudRate">The baud rate. <para>波特率。</para></param>
    /// <returns>An unconnected serial transport; call <c>Connect</c> before use. <para>未连接的串口传输；使用前调用 <c>Connect</c>。</para></returns>
    SerialTransport CreateSerialTransport(string portName, int baudRate = 3000000);

    /// <summary>
    /// Creates a TCP transport that speaks ADB directly to a device's adbd (port 5555),
    /// bypassing any adb server.
    /// <para>创建直接与设备 adbd（端口 5555）通信的 ADB TCP 传输，绕过任何 adb server。</para>
    /// </summary>
    /// <param name="host">The device host name or address. <para>设备主机名或地址。</para></param>
    /// <param name="port">The adbd TCP port. <para>adbd TCP 端口。</para></param>
    /// <param name="connectTimeoutMs">Connect timeout in milliseconds. <para>连接超时（毫秒）。</para></param>
    /// <returns>An unconnected TCP transport. <para>未连接的 TCP 传输。</para></returns>
    AdbTcpTransport CreateAdbTcpTransport(string host, int port = 5555, int connectTimeoutMs = 5000);

    /// <summary>
    /// Creates a TCP transport for fastboot-over-TCP (length-prefixed framing, port 5554).
    /// <para>创建 fastboot-over-TCP 的 TCP 传输（长度前缀帧，端口 5554）。</para>
    /// </summary>
    /// <param name="host">The device host name or address. <para>设备主机名或地址。</para></param>
    /// <param name="port">The fastboot TCP port. <para>fastboot TCP 端口。</para></param>
    /// <param name="connectTimeoutMs">Connect/handshake timeout in milliseconds. <para>连接/握手超时（毫秒）。</para></param>
    /// <returns>An unconnected TCP transport. <para>未连接的 TCP 传输。</para></returns>
    FastbootTcpTransport CreateFastbootTcpTransport(string host, int port = 5554, int connectTimeoutMs = 30000);

    /// <summary>
    /// Creates a retransmitting UDP transport for fastboot-over-UDP.
    /// <para>创建带重传的 fastboot-over-UDP UDP 传输。</para>
    /// </summary>
    /// <param name="host">The device host name or address. <para>设备主机名或地址。</para></param>
    /// <param name="port">The fastboot UDP port. <para>fastboot UDP 端口。</para></param>
    /// <param name="timeoutMs">Per-transmission timeout in milliseconds. <para>单次传输超时（毫秒）。</para></param>
    /// <param name="maxTransmissionAttempts">Maximum transmission attempts. <para>最大传输尝试次数。</para></param>
    /// <returns>An unconnected UDP transport. <para>未连接的 UDP 传输。</para></returns>
    FastbootUdpTransport CreateFastbootUdpTransport(string host, int port = 5554, int timeoutMs = 1000, int maxTransmissionAttempts = 10);
}