using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm;

/// <summary>
/// Provides a unified entry point for USB discovery, device communication, and API registration
/// through static and instance facades.
/// <para>通过静态与实例门面统一提供 USB 发现、设备通信与 API 注册入口。</para>
/// </summary>
internal sealed class CommKit : ICommKit
{
    private readonly UsbCommunicationLayer _usb;

    static CommKit()
    {
        UsbAccessPolicy.Configure(Devices.Services.DebugBridgeSessionOpener.RequireDirectUsb);
    }

    /// <summary>
    /// Gets the default facade instance backed by the default USB communication layer.
    /// <para>获取由默认 USB 通信层支撑的默认门面实例。</para>
    /// </summary>
    /// <value>The shared default instance. <para>共享的默认实例。</para></value>
    public static CommKit Default { get; } = new();

    /// <summary>
    /// Gets the names of the APIs that are supported on the current platform.
    /// <para>获取当前平台上受支持的 API 名称列表。</para>
    /// </summary>
    /// <returns>A read-only list of names. <para>名称只读列表。</para></returns>
    public static IReadOnlyList<string> GetAvailableApis() => Default._usb.GetAvailableApis();

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs.
    /// <para>获取当前已注册 USB API 的能力摘要。</para>
    /// </summary>
    /// <returns>A read-only list of capability summaries. <para>能力摘要只读列表。</para></returns>
    public static IReadOnlyList<UsbApiCapabilities> GetAvailableApiCapabilities() => Default._usb.GetAvailableApiCapabilities();

    /// <summary>
    /// Enumerates devices for the selected backend.
    /// <para>按选定后端枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A read-only list of matched devices. <para>匹配设备只读列表。</para></returns>
    public static IReadOnlyList<UsbDeviceInfo> EnumerateDevices(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => Default._usb.EnumerateDevices(apiKind, filter);

    /// <summary>
    /// Enumerates devices asynchronously for the selected backend.
    /// <para>按选定后端异步枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the matched devices. <para>返回匹配设备列表的任务。</para></returns>
    public static Task<IReadOnlyList<UsbDeviceInfo>> EnumerateDevicesAsync(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        CancellationToken cancellationToken = default) => Default._usb.EnumerateDevicesAsync(apiKind, filter, cancellationToken);

    /// <summary>
    /// Monitors USB device additions and removals by polling snapshots.
    /// <para>通过轮询快照监视 USB 设备新增与移除。</para>
    /// </summary>
    /// <param name="onChanged">Change callback. <para>设备变化回调。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="pollInterval">Polling interval. <para>轮询间隔。</para></param>
    /// <param name="fireInitialSnapshot">Whether to emit initial Added events. <para>是否触发初始 Added 事件。</para></param>
    /// <param name="onError">Optional error callback invoked when enumeration or callback handling fails. <para>枚举或回调失败时触发的可选错误回调。</para></param>
    /// <returns>A disposable monitor handle. <para>可释放的监视句柄。</para></returns>
    public static IDisposable MonitorDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null) => Default._usb.MonitorDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);

    /// <summary>
    /// Opens matching device sessions for direct read/write operations.
    /// <para>打开匹配设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A wrapped collection of opened sessions. <para>封装后的已打开会话集合。</para></returns>
    public static UsbSessionCollection OpenDeviceSessions(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => Default._usb.OpenDeviceSessions(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => Default._usb.OpenDeviceSession(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations, applying
    /// per-session tuning options.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作，并应用按会话生效的调优选项。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options) => Default._usb.OpenDeviceSession(apiKind, filter, options);

    /// <summary>
    /// Opens the session whose <see cref="UsbDeviceInfo.DeviceKey"/> matches the specified key.
    /// <para>打开 <see cref="UsbDeviceInfo.DeviceKey"/> 与指定键匹配的设备会话。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key from <see cref="UsbDeviceInfo.DeviceKey"/>. <para>来自 <see cref="UsbDeviceInfo.DeviceKey"/> 的稳定设备键。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <returns>The matching session, or <c>null</c> when no device matches. <para>匹配的会话；无匹配设备时返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSessionByKey(
        string deviceKey,
        UsbApiKind apiKind = UsbApiKind.Auto) => Default._usb.OpenDeviceSessionByKey(deviceKey, apiKind);

    /// <summary>
    /// Registers a custom USB API provider.
    /// <para>注册自定义 USB API 提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="providerFactory">The provider factory. <para>提供器工厂方法。</para></param>
    /// <returns><c>true</c> when the provider is registered. <para>注册成功时返回 <c>true</c>。</para></returns>
    public static bool RegisterApi(string apiName, Func<IUsbApiProvider> providerFactory) => Default._usb.RegisterApi(apiName, providerFactory);

    /// <summary>
    /// Configures the absolute path to the native libusb runtime for the libusb backend.
    /// <para>为 libusb 后端配置原生 libusb 运行时的绝对路径。</para>
    /// </summary>
    /// <param name="libraryPath">Absolute path to the native libusb runtime. <para>原生 libusb 运行时的绝对路径。</para></param>
    public static void SetLibusbLibraryPath(string libraryPath) => UsbCommunicationLayer.SetLibusbLibraryPath(libraryPath);

    /// <summary>
    /// Initializes a new facade with the default USB communication layer.
    /// <para>使用默认 USB 通信层初始化门面。</para>
    /// </summary>
    public CommKit()
    {
        _usb = new UsbCommunicationLayer();
    }

    /// <summary>
    /// Initializes a new facade with a caller-provided USB communication layer.
    /// <para>使用外部提供的 USB 通信层初始化门面。</para>
    /// </summary>
    /// <param name="usb">The USB communication layer. <para>USB 通信层。</para></param>
    public CommKit(UsbCommunicationLayer usb)
    {
        _usb = usb ?? throw new ArgumentNullException(nameof(usb));
    }

    /// <summary>
    /// Gets the names of the USB APIs that are supported on the current platform.
    /// <para>获取当前平台上受支持的 USB API 名称列表。</para>
    /// </summary>
    /// <returns>A read-only list of names. <para>名称只读列表。</para></returns>
    public IReadOnlyList<string> GetAvailableUsbApis() => _usb.GetAvailableApis();

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs.
    /// <para>获取当前已注册 USB API 的能力摘要。</para>
    /// </summary>
    /// <returns>A read-only list of capability summaries. <para>能力摘要只读列表。</para></returns>
    public IReadOnlyList<UsbApiCapabilities> GetAvailableUsbApiCapabilities() => _usb.GetAvailableApiCapabilities();

    /// <summary>
    /// Enumerates devices for the selected backend.
    /// <para>按选定后端枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A read-only list of matched devices. <para>匹配设备只读列表。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> EnumerateUsbDevices(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.EnumerateDevices(apiKind, filter);

    /// <summary>
    /// Enumerates devices asynchronously for the selected backend.
    /// <para>按选定后端异步枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the matched devices. <para>返回匹配设备列表的任务。</para></returns>
    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateUsbDevicesAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, CancellationToken cancellationToken = default) => _usb.EnumerateDevicesAsync(apiKind, filter, cancellationToken);

    /// <summary>
    /// Monitors USB device additions and removals by polling snapshots.
    /// <para>通过轮询快照监视 USB 设备新增与移除。</para>
    /// </summary>
    /// <param name="onChanged">Change callback. <para>设备变化回调。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="pollInterval">Polling interval. <para>轮询间隔。</para></param>
    /// <param name="fireInitialSnapshot">Whether to emit initial Added events. <para>是否触发初始 Added 事件。</para></param>
    /// <param name="onError">Optional error callback invoked when enumeration or callback handling fails. <para>枚举或回调失败时触发的可选错误回调。</para></param>
    /// <returns>A disposable monitor handle. <para>可释放的监视句柄。</para></returns>
    public IDisposable MonitorUsbDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null) => _usb.MonitorDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);

    /// <summary>
    /// Opens matching device sessions for direct read/write operations.
    /// <para>打开匹配设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A wrapped collection of opened sessions. <para>封装后的已打开会话集合。</para></returns>
    public UsbSessionCollection OpenUsbDeviceSessions(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.OpenDeviceSessions(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.OpenDeviceSession(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations, applying
    /// per-session tuning options.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作，并应用按会话生效的调优选项。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind, UsbDeviceFilter? filter, UsbSessionOptions? options) => _usb.OpenDeviceSession(apiKind, filter, options);

    /// <summary>
    /// Opens the session whose device key matches the specified key.
    /// <para>打开设备键与指定键匹配的会话。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key. <para>稳定的设备键。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <returns>The matching session, or <c>null</c> when no device matches. <para>匹配的会话；无匹配设备时返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenUsbDeviceSessionByKey(string deviceKey, UsbApiKind apiKind = UsbApiKind.Auto) => _usb.OpenDeviceSessionByKey(deviceKey, apiKind);

    /// <summary>
    /// Waits until a device matching the filter appears.
    /// <para>等待出现匹配过滤器的设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait before giving up. <para>放弃前的最长等待时间。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task whose result is <c>true</c> when a matching device appeared in time. <para>在时限内出现匹配设备时结果为 <c>true</c> 的任务。</para></returns>
    public Task<bool> WaitForDeviceAppearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForDeviceAppearAsync(apiKind, filter, timeout, cancellationToken);

    /// <summary>
    /// Waits until a device matching the filter disappears.
    /// <para>等待匹配过滤器的设备消失。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait before giving up. <para>放弃前的最长等待时间。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task whose result is <c>true</c> when the device disappeared in time. <para>在时限内设备消失时结果为 <c>true</c> 的任务。</para></returns>
    public Task<bool> WaitForDeviceDisappearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForDeviceDisappearAsync(apiKind, filter, timeout, cancellationToken);

    /// <summary>
    /// Waits for a device removal followed by an appearance, as on a bootloader mode switch.
    /// <para>等待设备移除后再次出现，对应引导模式切换。</para>
    /// </summary>
    /// <param name="removedFilter">Filter identifying the device expected to disappear. <para>标识预期消失设备的过滤器。</para></param>
    /// <param name="appearedFilter">Filter identifying the device expected to appear. <para>标识预期出现设备的过滤器。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="timeout">Maximum wait before giving up. <para>放弃前的最长等待时间。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task whose result reports whether the switch completed in time. <para>结果指示切换是否在时限内完成的任务。</para></returns>
    public Task<bool> WaitForModeSwitchAsync(UsbDeviceFilter? removedFilter, UsbDeviceFilter? appearedFilter, UsbApiKind apiKind = UsbApiKind.Auto, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForModeSwitchAsync(removedFilter, appearedFilter, apiKind, timeout, cancellationToken);

    /// <summary>
    /// Registers a custom USB API provider.
    /// <para>注册自定义 USB API 提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="providerFactory">The provider factory. <para>提供器工厂方法。</para></param>
    /// <returns><c>true</c> when the provider is registered. <para>注册成功时返回 <c>true</c>。</para></returns>
    public bool RegisterUsbApi(string apiName, Func<IUsbApiProvider> providerFactory) => _usb.RegisterApi(apiName, providerFactory);

    /// <summary>
    /// Gets the serial ports currently available on the machine.
    /// <para>获取当前机器上可用的串口。</para>
    /// </summary>
    /// <returns>A read-only list of port names. <para>端口名称只读列表。</para></returns>
    public IReadOnlyList<string> GetAvailableSerialPorts() => Transports.Serial.SerialTransport.GetAvailablePorts();

    /// <summary>
    /// Creates a serial transport for the given port and baud rate.
    /// <para>为给定端口与波特率创建串口传输。</para>
    /// </summary>
    /// <param name="portName">The serial port name. <para>串口名称。</para></param>
    /// <param name="baudRate">The baud rate for the transport. <para>传输的波特率。</para></param>
    /// <returns>A serial transport bound to the port. <para>绑定到该端口的串口传输。</para></returns>
    public Transports.Serial.SerialTransport CreateSerialTransport(string portName, int baudRate = 3000000) => new(portName, baudRate);

    /// <summary>
    /// Creates an ADB TCP transport for the given endpoint.
    /// <para>为给定端点创建 ADB TCP 传输。</para>
    /// </summary>
    /// <param name="host">The target host address. <para>目标主机地址。</para></param>
    /// <param name="port">The target TCP port. <para>目标 TCP 端口。</para></param>
    /// <param name="connectTimeoutMs">The connect timeout in milliseconds. <para>连接超时毫秒数。</para></param>
    /// <returns>An ADB TCP transport for the endpoint. <para>面向该端点的 ADB TCP 传输。</para></returns>
    public Transports.Tcp.AdbTcpTransport CreateAdbTcpTransport(string host, int port = 5555, int connectTimeoutMs = 5000) => new(host, port, connectTimeoutMs);

    /// <summary>
    /// Creates a Fastboot TCP transport for the given endpoint.
    /// <para>为给定端点创建 Fastboot TCP 传输。</para>
    /// </summary>
    /// <param name="host">The target host address. <para>目标主机地址。</para></param>
    /// <param name="port">The target TCP port. <para>目标 TCP 端口。</para></param>
    /// <param name="connectTimeoutMs">The connect timeout in milliseconds. <para>连接超时毫秒数。</para></param>
    /// <returns>A Fastboot TCP transport for the endpoint. <para>面向该端点的 Fastboot TCP 传输。</para></returns>
    public Protocols.Fastboot.Transport.FastbootTcpTransport CreateFastbootTcpTransport(string host, int port = 5554, int connectTimeoutMs = 30000) => new(host, port, connectTimeoutMs);

    /// <summary>
    /// Creates a Fastboot UDP transport for the given endpoint.
    /// <para>为给定端点创建 Fastboot UDP 传输。</para>
    /// </summary>
    /// <param name="host">The target host address. <para>目标主机地址。</para></param>
    /// <param name="port">The target UDP port. <para>目标 UDP 端口。</para></param>
    /// <param name="timeoutMs">The per-transmission timeout in milliseconds. <para>单次传输的超时毫秒数。</para></param>
    /// <param name="maxTransmissionAttempts">The maximum number of transmission attempts. <para>最大传输尝试次数。</para></param>
    /// <returns>A Fastboot UDP transport for the endpoint. <para>面向该端点的 Fastboot UDP 传输。</para></returns>
    public Protocols.Fastboot.Transport.FastbootUdpTransport CreateFastbootUdpTransport(string host, int port = 5554, int timeoutMs = 1000, int maxTransmissionAttempts = 10) => new(host, port, timeoutMs, maxTransmissionAttempts);
}