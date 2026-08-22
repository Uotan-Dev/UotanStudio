using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm;

/// <summary>
/// Exposes the public UotanStudio.Comm entry points for device discovery, communication, and API registration.
/// <para>暴露 UotanStudio.Comm 的公共入口，用于设备发现、通信与 API 注册。</para>
/// Implementations provide both USB transport access and a dedicated protocol layer; consumers
/// code against this interface to remain testable and swappable.
/// <para>实现类同时提供 USB 传输层与专用协议层；消费者针对该接口编码，以保持可测试性与可替换性。</para>
/// </summary>
public interface ICommKit
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
    /// Opens the first matching USB device session for direct read/write operations,
    /// applying per-session tuning options.
    /// <para>打开第一个匹配的 USB 设备会话，用于直接读写操作，并应用按会话生效的调优选项。</para>
    /// Options (timeouts, RAW_IO) are forwarded to the backend before the session handle is
    /// created; backends that cannot honour them ignore the options.
    /// <para>选项（超时、RAW_IO）在会话句柄创建前透传给后端；无法兑现选项的后端会忽略它们。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    IUsbDeviceSession? OpenUsbDeviceSession(UsbApiKind apiKind, UsbDeviceFilter? filter, UsbSessionOptions? options);

    /// <summary>
    /// Opens the session whose <see cref="UsbDeviceInfo.DeviceKey"/> matches the specified key.
    /// <para>打开 <see cref="UsbDeviceInfo.DeviceKey"/> 与指定键匹配的设备会话。</para>
    /// Protocol layers that stash a device's key (e.g. after a mode switch like adb →
    /// fastboot → EDL where VID/PID change) can reopen the same physical device by key.
    /// <para>协议层保存设备键后（例如 adb → fastboot → EDL 模式切换导致 VID/PID 变化），
    /// 可按键重开同一物理设备。</para>
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
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when a matching device appeared before the timeout. <para>超时前出现匹配设备时返回 <c>true</c>。</para></returns>
    Task<bool> WaitForDeviceAppearAsync(UsbApiKind apiKind = UsbApiKind.Auto, UsbDeviceFilter? filter = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until no device matching the filter remains (250 ms polling).
    /// <para>等待不再存在匹配过滤条件的设备（250 ms 轮询）。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
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
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
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
}
