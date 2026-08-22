using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm;

/// <summary>
/// Provides a unified entry point for the UotanStudio.Comm library, exposing USB discovery,
/// device communication, and API registration through both static and instance facades.
/// <para>为 UotanStudio.Comm 库提供统一入口，通过静态与实例门面暴露 USB 发现、设备通信和 API 注册能力。</para>
/// Use the static members for quick access with the default USB communication layer, or
/// instantiate <see cref="CommKit"/> for a configurable, testable facade.
/// <para>使用静态成员可通过默认 USB 通信层快速访问，或实例化 <see cref="CommKit"/> 获得可配置、可测试的门面。</para>
/// </summary>
public sealed class CommKit : ICommKit
{
    private static readonly CommKit _default = new();

    private readonly UsbCommunicationLayer _usb;

    /// <summary>
    /// Gets the default facade instance backed by the default USB communication layer.
    /// <para>获取由默认 USB 通信层支撑的默认门面实例。</para>
    /// </summary>
    public static CommKit Default => _default;

    /// <summary>
    /// Gets the names of the APIs that are supported on the current platform.
    /// <para>获取当前平台上受支持的 API 名称列表。</para>
    /// Only providers whose <see cref="IUsbApiProvider.IsSupportedOnCurrentPlatform"/>
    /// is <c>true</c> are listed; unavailable providers (e.g. the opt-in HarmonyOS
    /// DDK backend) stay hidden.
    /// <para>仅列出 <see cref="IUsbApiProvider.IsSupportedOnCurrentPlatform"/> 为 <c>true</c>
    /// 的提供器；不可用的提供器（例如需显式开启的 HarmonyOS DDK 后端）不会出现。</para>
    /// </summary>
    /// <returns>A read-only list of names. <para>名称只读列表。</para></returns>
    public static IReadOnlyList<string> GetAvailableApis() => _default._usb.GetAvailableApis();

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs.
    /// <para>获取当前已注册 USB API 的能力摘要。</para>
    /// </summary>
    /// <returns>A read-only list of capability summaries. <para>能力摘要只读列表。</para></returns>
    public static IReadOnlyList<UsbApiCapabilities> GetAvailableApiCapabilities() => _default._usb.GetAvailableApiCapabilities();

    /// <summary>
    /// Enumerates devices for the selected backend.
    /// <para>按选定后端枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A read-only list of matched devices. <para>匹配设备只读列表。</para></returns>
    public static IReadOnlyList<UsbDeviceInfo> EnumerateDevices(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => _default._usb.EnumerateDevices(apiKind, filter);

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
        CancellationToken cancellationToken = default) => _default._usb.EnumerateDevicesAsync(apiKind, filter, cancellationToken);

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
        Action<Exception>? onError = null) => _default._usb.MonitorDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);

    /// <summary>
    /// Opens matching device sessions for direct read/write operations.
    /// <para>打开匹配设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A wrapped collection of opened sessions. <para>封装后的已打开会话集合。</para></returns>
    public static UsbSessionCollection OpenDeviceSessions(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => _default._usb.OpenDeviceSessions(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => _default._usb.OpenDeviceSession(apiKind, filter);

    /// <summary>
    /// Opens the first matching device session for direct read/write operations, applying
    /// per-session tuning options.
    /// <para>打开第一个匹配的设备会话，用于直接读写操作，并应用按会话生效的调优选项。</para>
    /// Options (timeouts, RAW_IO) are forwarded to the backend before the session handle is
    /// created; backends that cannot honour them ignore the options.
    /// <para>选项（超时、RAW_IO）在会话句柄创建前透传给后端；无法兑现选项的后端会忽略它们。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options) => _default._usb.OpenDeviceSession(apiKind, filter, options);

    /// <summary>
    /// Opens the session whose <see cref="UsbDeviceInfo.DeviceKey"/> matches the specified key.
    /// <para>打开 <see cref="UsbDeviceInfo.DeviceKey"/> 与指定键匹配的设备会话。</para>
    /// Protocol layers that stash a device's key (e.g. after a mode switch like adb →
    /// fastboot → EDL where VID/PID change) can reopen the same physical device by key.
    /// <para>协议层保存设备键后（例如 adb → fastboot → EDL 模式切换导致 VID/PID 变化），
    /// 可按键重开同一物理设备。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key from <see cref="UsbDeviceInfo.DeviceKey"/>. <para>来自 <see cref="UsbDeviceInfo.DeviceKey"/> 的稳定设备键。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <returns>The matching session, or <c>null</c> when no device matches. <para>匹配的会话；无匹配设备时返回 <c>null</c>。</para></returns>
    public static IUsbDeviceSession? OpenDeviceSessionByKey(
        string deviceKey,
        UsbApiKind apiKind = UsbApiKind.Auto) => _default._usb.OpenDeviceSessionByKey(deviceKey, apiKind);

    /// <summary>
    /// Registers a custom USB API provider.
    /// <para>注册自定义 USB API 提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="providerFactory">The provider factory. <para>提供器工厂方法。</para></param>
    /// <returns><c>true</c> when the provider is registered. <para>注册成功时返回 <c>true</c>。</para></returns>
    public static bool RegisterApi(string apiName, Func<IUsbApiProvider> providerFactory) => _default._usb.RegisterApi(apiName, providerFactory);

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
    /// <para>使用调用方提供的 USB 通信层初始化门面。</para>
    /// </summary>
    /// <param name="usb">The USB communication layer. <para>USB 通信层。</para></param>
    public CommKit(UsbCommunicationLayer usb)
    {
        _usb = usb ?? throw new ArgumentNullException(nameof(usb));
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetAvailableUsbApis() => _usb.GetAvailableApis();

    /// <inheritdoc/>
    public IReadOnlyList<UsbApiCapabilities> GetAvailableUsbApiCapabilities() => _usb.GetAvailableApiCapabilities();

    /// <inheritdoc/>
    public IReadOnlyList<UsbDeviceInfo> EnumerateUsbDevices(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.EnumerateDevices(apiKind, filter);

    /// <inheritdoc/>
    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateUsbDevicesAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, CancellationToken cancellationToken = default) => _usb.EnumerateDevicesAsync(apiKind, filter, cancellationToken);

    /// <inheritdoc/>
    public IDisposable MonitorUsbDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null) => _usb.MonitorDevices(onChanged, apiKind, filter, pollInterval, fireInitialSnapshot, onError);

    /// <inheritdoc/>
    public UsbSessionCollection OpenUsbDeviceSessions(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.OpenDeviceSessions(apiKind, filter);

    /// <inheritdoc/>
    public IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null) => _usb.OpenDeviceSession(apiKind, filter);

    /// <inheritdoc/>
    public IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind, UsbDeviceFilter? filter, UsbSessionOptions? options) => _usb.OpenDeviceSession(apiKind, filter, options);

    /// <inheritdoc/>
    public IUsbDeviceSession? OpenUsbDeviceSessionByKey(string deviceKey, UsbApiKind apiKind = UsbApiKind.Auto) => _usb.OpenDeviceSessionByKey(deviceKey, apiKind);

    /// <inheritdoc/>
    public Task<bool> WaitForDeviceAppearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForDeviceAppearAsync(apiKind, filter, timeout, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> WaitForDeviceDisappearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForDeviceDisappearAsync(apiKind, filter, timeout, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> WaitForModeSwitchAsync(UsbDeviceFilter? removedFilter, UsbDeviceFilter? appearedFilter, UsbApiKind apiKind = UsbApiKind.Auto, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => _usb.WaitForModeSwitchAsync(removedFilter, appearedFilter, apiKind, timeout, cancellationToken);

    /// <inheritdoc/>
    public bool RegisterUsbApi(string apiName, Func<IUsbApiProvider> providerFactory) => _usb.RegisterApi(apiName, providerFactory);
}
