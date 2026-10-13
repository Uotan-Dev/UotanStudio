using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using UotanStudio.Comm.Transports.Usb.Backend.MacOS;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using UotanStudio.Comm.Transports.Usb.Providers;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Provides USB discovery and API registration operations.
/// <para>提供 USB 发现与 API 注册能力。</para>
/// </summary>
/// <remarks>
/// Initializes a new communication layer.
/// <para>初始化新的通信层。</para>
/// </remarks>
/// <param name="registry">Optional registry to use. <para>可选注册表实例。</para></param>
/// <param name="configuration">Optional isolated configuration; otherwise uses the process configuration.</param>
internal sealed class UsbCommunicationLayer(UsbApiRegistry? registry = null, DeviceCommunicationConfiguration? configuration = null)
{
    private readonly UsbApiRegistry _registry = registry ?? UsbApiRegistry.CreateDefault();

    private DeviceCommunicationConfiguration Configuration => configuration ?? DeviceCommunicationConfiguration.Current;

    /// <summary>
    /// Configures the absolute path to the native libusb runtime for the libusb backend. Must be called before the first enumeration or session open; the library is pre-loaded by absolute path so the bare SONAME resolves without relying on the loader's default search path.
    /// <para>为 libusb 后端配置原生 libusb 运行库的绝对路径。必须在首次枚举或会话打开之前调用；通过绝对路径预加载，使裸 SONAME 的解析不依赖加载器的默认搜索路径。</para>
    /// </summary>
    /// <param name="libraryPath">Absolute path to the native libusb runtime.
    /// <para>原生 libusb 运行时的绝对路径。</para></param>
    /// <exception cref="FileNotFoundException">The path does not exist.
    /// <para>路径不存在。</para></exception>
    public static void SetLibusbLibraryPath(string libraryPath) => LibUsbNativeLoader.SetLibraryPath(libraryPath);

    /// <summary>
    /// Gets the names of the APIs that are supported on the current platform.
    /// <para>获取当前平台上受支持的 API 名称列表。</para>
    /// Only providers whose <see cref="IUsbApiProvider.IsSupportedOnCurrentPlatform"/>
    /// is <c>true</c> are listed; providers that are registered but unavailable
    /// (e.g. the opt-in HarmonyOS DDK backend) stay hidden.
    /// <para>仅列出 <see cref="IUsbApiProvider.IsSupportedOnCurrentPlatform"/> 为 <c>true</c>
    /// 的提供器；已注册但在当前平台不可用的提供器（例如需显式开启的 HarmonyOS DDK 后端）不会出现。</para>
    /// </summary>
    /// <returns>A read-only list of names. <para>名称只读列表。</para></returns>
    public IReadOnlyList<string> GetAvailableApis()
        => [.. _registry.GetApiNames().Where(name => _registry.TryCreate(name, out var provider) && provider?.IsSupportedOnCurrentPlatform == true)];

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs.
    /// <para>获取当前已注册 USB API 的能力摘要。</para>
    /// Lists every backend supported on the current platform (mirrors
    /// <see cref="GetAvailableApis"/>), independent of the <see cref="UsbApiKind.Auto"/>
    /// resolution — Auto affects enumeration/session/monitor paths only.
    /// <para>列出当前平台支持的每个后端（与 <see cref="GetAvailableApis"/> 一致），
    /// 与 <see cref="UsbApiKind.Auto"/> 的解析无关——Auto 只影响枚举/会话/监视路径。</para>
    /// </summary>
    /// <returns>A read-only list of capability summaries. <para>能力摘要只读列表。</para></returns>
    public IReadOnlyList<UsbApiCapabilities> GetAvailableApiCapabilities()
        => [.. _registry.GetApiNames()
            .Select(name => _registry.TryCreate(name, out var provider) ? provider : null)
            .Where(static provider => provider is { IsSupportedOnCurrentPlatform: true })
            .Select(static provider => provider!.GetCapabilities())];

    /// <summary>
    /// Enumerates devices for the selected backend.
    /// <para>按选定后端枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A read-only list of matched devices. <para>匹配设备只读列表。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> EnumerateDevices(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null) => EnumerateDevicesCore(apiKind, filter, cancellationToken: default);

    /// <summary>
    /// Enumerates devices for the selected backend with cancellation support.
    /// <para>按选定后端枚举设备，并支持取消。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A read-only list of matched devices. <para>匹配设备只读列表。</para></returns>
    public IReadOnlyList<UsbDeviceInfo> EnumerateDevices(
        UsbApiKind apiKind,
        UsbDeviceFilter? filter,
        CancellationToken cancellationToken) => EnumerateDevicesCore(apiKind, filter, cancellationToken);

    private IReadOnlyList<UsbDeviceInfo> EnumerateDevicesCore(
        UsbApiKind apiKind,
        UsbDeviceFilter? filter,
        CancellationToken cancellationToken)
    {
        var providers = ResolveProviders(apiKind);
        var devices = new List<UsbDeviceInfo>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            foreach (var info in provider.EnumerateDeviceInfos(filter))
            {
                if (seen.Add(UsbDeviceIdentity.BuildPhysicalKey(info)))
                {
                    devices.Add(info);
                }
            }
        }

        return devices;
    }

    /// <summary>
    /// Enumerates devices asynchronously for the selected backend.
    /// <para>按选定后端异步枚举设备。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the matched devices. <para>返回匹配设备列表的任务。</para></returns>
    public async Task<IReadOnlyList<UsbDeviceInfo>> EnumerateDevicesAsync(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return EnumerateDevicesCore(apiKind, filter, cancellationToken);
    }

    /// <summary>
    /// Enumerates devices and invokes a callback for each match.
    /// <para>枚举设备并对每个匹配项执行回调。</para>
    /// </summary>
    /// <param name="onDeviceFound">Callback invoked per device. <para>每个设备匹配时触发的回调。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    public void EnumerateDevices(
        Action<UsbDeviceInfo> onDeviceFound,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(onDeviceFound);

        var devices = EnumerateDevices(apiKind, filter);
        foreach (var device in devices)
        {
            onDeviceFound(device);
        }
    }

    /// <summary>
    /// Monitors USB device additions and removals.
    /// <para>监视 USB 设备新增与移除。</para>
    /// </summary>
    /// <param name="onChanged">Change callback. <para>设备变化回调。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="pollInterval">Polling interval used by the polling fallback. <para>轮询回退使用的轮询间隔。</para></param>
    /// <param name="fireInitialSnapshot">Whether to emit initial Added events. <para>是否触发初始 Added 事件。</para></param>
    /// <param name="onError">Optional error callback invoked when enumeration or callback handling fails. <para>枚举或回调失败时触发的可选错误回调。</para></param>
    /// <param name="cancellationToken">Cancelling it disposes the returned monitor handle. <para>取消该令牌会释放返回的监视句柄。</para></param>
    /// <returns>A disposable monitor handle. <para>可释放的监视句柄。</para></returns>
    public IDisposable MonitorDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        if (apiKind == UsbApiKind.Auto)
        {
            apiKind = PreferredAutoApiKind();
        }

        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        IReadOnlyList<UsbDeviceInfo> Enumerator()
        {
            try
            {
                return EnumerateDevicesCore(apiKind, filter, cancellationToken: default);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                UsbTrace.Log($"MonitorDevices enumerate failed: {ex.GetType().Name}: {ex.Message}");
                return [];
            }
        }

        if (apiKind == UsbApiKind.LibUsbDotNet)
        {
            try
            {
                return new UsbLibUsbHotplugMonitor(Enumerator, onChanged, onError, filter);
            }
            catch (PlatformNotSupportedException)
            {
                UsbTrace.Log("libusb hotplug unavailable on this platform - falling back to polling monitor.");
            }
            catch (DllNotFoundException)
            {
                UsbTrace.Log("libusb native runtime absent - falling back to polling monitor.");
            }
            catch (EntryPointNotFoundException)
            {
                UsbTrace.Log("libusb hotplug entry point absent - falling back to polling monitor.");
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                UsbTrace.Log($"UsbLibUsbHotplugMonitor creation failed: {ex.GetType().Name}: {ex.Message} - falling back to polling monitor.");
            }
        }

        if (apiKind == UsbApiKind.Native && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return new UsbWindowsHotplugMonitor(Enumerator, onChanged, onError);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                UsbTrace.Log($"UsbWindowsHotplugMonitor creation failed: {ex.GetType().Name}: {ex.Message} - falling back to polling monitor.");
            }
        }

        if (apiKind == UsbApiKind.Native && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return new UdevHotplugMonitor(Enumerator, onChanged, onError);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                UsbTrace.Log($"UdevHotplugMonitor creation failed: {ex.GetType().Name}: {ex.Message} - falling back to polling monitor.");
            }
        }

        return new UsbDeviceMonitor(Enumerator, onChanged, onError, interval, fireInitialSnapshot, cancellationToken);
    }

    /// <summary>
    /// Waits until at least one device matching the filter appears, polling with a 250 ms interval.
    /// <para>以 250 ms 间隔轮询，直到至少一个匹配过滤条件的设备出现。</para>
    /// Useful for mode-switch workflows (e.g. adb reboot bootloader → wait for the fastboot device).
    /// <para>适用于模式切换工作流（例如 adb reboot bootloader 后等待 fastboot 设备出现）。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when a matching device appeared before the timeout. <para>超时前出现匹配设备时返回 <c>true</c>。</para></returns>
    public Task<bool> WaitForDeviceAppearAsync(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = NormalizeWaitTimeout(timeout);
        return WaitAsync(
            () => EnumerateDevicesCore(apiKind, filter, cancellationToken).Count > 0,
            effectiveTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Waits until no device matching the filter remains, polling with a 250 ms interval.
    /// <para>以 250 ms 间隔轮询，直到不再存在匹配过滤条件的设备。</para>
    /// Useful when waiting for a device to be unplugged or to leave a mode.
    /// <para>适用于等待设备被拔出或退出某模式。</para>
    /// </summary>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when no matching device remains before the timeout. <para>超时前不再存在匹配设备时返回 <c>true</c>。</para></returns>
    public Task<bool> WaitForDeviceDisappearAsync(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = NormalizeWaitTimeout(timeout);
        return WaitAsync(
            () => EnumerateDevicesCore(apiKind, filter, cancellationToken).Count == 0,
            effectiveTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Waits until the <paramref name="removedFilter"/> devices are gone AND at least one
    /// <paramref name="appearedFilter"/> device is present - the classic reboot-into-bootloader
    /// / mode-switch pattern (e.g. adb → fastboot, fastboot → EDL).
    /// <para>等待 <paramref name="removedFilter"/> 设备消失且至少一个
    /// <paramref name="appearedFilter"/> 设备出现——经典的"重启进 bootloader/模式切换"模式
    /// （例如 adb → fastboot、fastboot → EDL）。</para>
    /// </summary>
    /// <param name="removedFilter">Filter for devices expected to disappear; pass <c>null</c> to skip this half. <para>预期消失的设备过滤条件；传 <c>null</c> 跳过该半边。</para></param>
    /// <param name="appearedFilter">Filter for devices expected to appear; pass <c>null</c> to skip this half. <para>预期出现的设备过滤条件；传 <c>null</c> 跳过该半边。</para></param>
    /// <param name="apiKind">The USB API selection mode. <para>USB API 选择模式。</para></param>
    /// <param name="timeout">Maximum wait Time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the mode switch completed before the timeout. <para>超时前完成模式切换时返回 <c>true</c>。</para></returns>
    public Task<bool> WaitForModeSwitchAsync(
        UsbDeviceFilter? removedFilter,
        UsbDeviceFilter? appearedFilter,
        UsbApiKind apiKind = UsbApiKind.Auto,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = NormalizeWaitTimeout(timeout);
        return removedFilter == null && appearedFilter == null
            ? throw new ArgumentException("At least one of removedFilter/appearedFilter must be provided.")
            : WaitAsync(
            () =>
            {
                var removedCount = removedFilter == null ? 0 : EnumerateDevicesCore(apiKind, removedFilter, cancellationToken).Count;
                var appearedCount = appearedFilter == null ? 0 : EnumerateDevicesCore(apiKind, appearedFilter, cancellationToken).Count;
                return removedCount == 0 && appearedCount > 0;
            },
            effectiveTimeout,
            cancellationToken);
    }

    private static TimeSpan NormalizeWaitTimeout(TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        return effectiveTimeout <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.")
            : effectiveTimeout;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        const int pollIntervalMs = 250;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var delay = remaining < TimeSpan.FromMilliseconds(pollIntervalMs) ? remaining : TimeSpan.FromMilliseconds(pollIntervalMs);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens matching device sessions for the selected backend.
    /// <para>为选定后端打开匹配的设备会话。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>A wrapped collection of sessions. <para>封装后的会话集合。</para></returns>
    public UsbSessionCollection OpenDeviceSessions(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null)
    {
        var providers = ResolveProviders(apiKind);
        var sessions = new List<IUsbDeviceSession>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            foreach (var session in provider.EnumerateDeviceSessions(filter))
            {
                if (seen.Add(UsbDeviceIdentity.BuildPhysicalKey(session.DeviceInfo)))
                {
                    sessions.Add(session);
                }
                else
                {
                    session.Dispose();
                }
            }
        }

        return new UsbSessionCollection(sessions);
    }

    /// <summary>
    /// Opens the first matching USB device session for the selected backend.
    /// <para>打开选定后端中第一个匹配的 USB 设备会话。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>返回第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind = UsbApiKind.Auto,
        UsbDeviceFilter? filter = null)
        => OpenDeviceSession(apiKind, filter, options: null);

    /// <summary>
    /// Opens the first matching USB device session for the selected backend, applying
    /// per-session tuning options.
    /// <para>打开选定后端中第一个匹配的 USB 设备会话，并应用按会话生效的调优选项。</para>
    /// Options (timeouts, RAW_IO) are forwarded to the backend before the session handle is
    /// created; backends that cannot honour them ignore the options.
    /// <para>选项（超时、RAW_IO）在会话句柄创建前透传给后端；无法兑现选项的后端会忽略它们。</para>
    /// </summary>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options; <c>null</c> keeps backend defaults. <para>可选的会话调优选项；<c>null</c> 保持后端默认值。</para></param>
    /// <returns>The first matching session, or <c>null</c> if none was found. <para>返回第一个匹配会话；如果没有则返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenDeviceSession(
        UsbApiKind apiKind,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options)
    {
        var providers = ResolveProviders(apiKind);

        foreach (var provider in providers)
        {
            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            foreach (var info in provider.EnumerateDeviceInfos(filter))
            {
                var selectedFilter = UsbDeviceIdentity.TryParseKeyFilter(info.DeviceKey);
                if (selectedFilter is null || string.IsNullOrEmpty(selectedFilter.DevicePathEquals)) continue;
                selectedFilter.InterfaceNumber = filter?.InterfaceNumber;
                selectedFilter.InterfaceNumbers = filter?.InterfaceNumbers;
                selectedFilter.EndpointAddressIn = filter?.EndpointAddressIn;
                selectedFilter.EndpointAddressOut = filter?.EndpointAddressOut;
                var sessions = provider is UsbApiProviderBase baseProvider
                    ? baseProvider.EnumerateDeviceSessions(selectedFilter, options)
                    : provider.EnumerateDeviceSessions(selectedFilter);
                IUsbDeviceSession? selected = null;
                try
                {
                    selected = sessions.FirstOrDefault(candidate => UsbDeviceIdentity.MatchesOpenedKey(info.DeviceKey, candidate.DeviceInfo.DeviceKey));
                    if (selected is not null) return selected;
                }
                finally
                {
                    foreach (var session in sessions)
                        if (!ReferenceEquals(session, selected)) session.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the session whose <see cref="UsbDeviceInfo.DeviceKey"/> matches the specified key.
    /// <para>打开 <see cref="UsbDeviceInfo.DeviceKey"/> 与指定键匹配的设备会话。</para>
    /// Non-matching opened sessions are disposed.
    /// <para>不匹配的已打开会话会被释放。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key from <see cref="UsbDeviceInfo.DeviceKey"/>. <para>来自 <see cref="UsbDeviceInfo.DeviceKey"/> 的稳定设备键。</para></param>
    /// <param name="apiKind">The backend selection mode. <para>后端选择模式。</para></param>
    /// <returns>The matching session, or <c>null</c> when no device matches. <para>匹配的会话；无匹配设备时返回 <c>null</c>。</para></returns>
    public IUsbDeviceSession? OpenDeviceSessionByKey(
        string deviceKey,
        UsbApiKind apiKind = UsbApiKind.Auto)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            throw new ArgumentException("Device key cannot be null or whitespace.", nameof(deviceKey));
        }

        var keyFilter = UsbDeviceIdentity.TryParseKeyFilter(deviceKey);
        if (keyFilter is null || string.IsNullOrEmpty(keyFilter.DevicePathEquals))
            throw new ArgumentException("A physical USB key with an exact path is required.", nameof(deviceKey));

        var providers = ResolveProviders(apiKind);
        if (apiKind == UsbApiKind.Auto)
        {
            var keyBackend = deviceKey.Split('|')[0];
            if (!string.IsNullOrEmpty(keyBackend) &&
                _registry.TryCreate(keyBackend, out var keyProvider) &&
                keyProvider is { IsSupportedOnCurrentPlatform: true })
            {
                providers = [keyProvider];
            }
        }

        foreach (var provider in providers)
        {
            if (!provider.IsSupportedOnCurrentPlatform)
            {
                continue;
            }

            var sessions = provider.EnumerateDeviceSessions(keyFilter);
            IUsbDeviceSession? selected = null;
            try
            {
                selected = sessions.FirstOrDefault(session => UsbDeviceIdentity.MatchesOpenedKey(deviceKey, session.DeviceInfo.DeviceKey));
                if (selected is not null) return selected;
            }
            finally
            {
                foreach (var session in sessions)
                    if (!ReferenceEquals(session, selected)) session.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Registers a custom USB API provider.
    /// <para>注册自定义 USB API 提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="providerFactory">The provider factory. <para>提供器工厂方法。</para></param>
    /// <returns><c>true</c> when the provider is registered. <para>注册成功时返回 <c>true</c>。</para></returns>
    public bool RegisterApi(string apiName, Func<IUsbApiProvider> providerFactory)
    {
        if (string.IsNullOrWhiteSpace(apiName))
        {
            throw new ArgumentException("API name cannot be null or whitespace.", nameof(apiName));
        }

        ArgumentNullException.ThrowIfNull(providerFactory);

        _registry.Register(apiName, providerFactory);
        return true;
    }

    private IReadOnlyList<IUsbApiProvider> ResolveProviders(UsbApiKind apiKind)
    {
        if (apiKind == UsbApiKind.Auto)
        {
            apiKind = Configuration.UsbBackend;
        }
        if (apiKind == UsbApiKind.Auto)
        {
            var preferredName = PreferredAutoApiName();
            return preferredName is not null &&
                _registry.TryCreate(preferredName, out var preferred) &&
                preferred is { IsSupportedOnCurrentPlatform: true }
                ? [preferred]
                : [.. _registry.GetApiNames()
                .Select(name => _registry.TryCreate(name, out var provider) ? provider : null)
                .Where(static provider => provider is { IsSupportedOnCurrentPlatform: true })
                .Cast<IUsbApiProvider>()
                .Take(1)];
        }

        var apiName = apiKind switch
        {
            UsbApiKind.Native => NativeUsbApiProvider.ApiNameConst,
            UsbApiKind.LibUsbDotNet => LibUsbApiProvider.ApiNameConst,
            UsbApiKind.HarmonyOS => HarmonyOSUsbApiProvider.ApiNameConst,
            UsbApiKind.Auto => string.Empty,
            UsbApiKind.Custom => string.Empty,
            _ => string.Empty
        };

        return string.IsNullOrEmpty(apiName)
            ? []
            : _registry.TryCreate(apiName, out var provider) && provider != null ? [provider] : [];
    }

    /// <summary>
    /// Returns the default backend that <see cref="UsbApiKind.Auto"/> resolves to on the
    /// current platform. Every supported desktop platform prefers its native backend
    /// (Windows → winusb, Linux → usbfs, macOS → IOKit/IOUSBHost); libusb is an explicit
    /// opt-in fallback via <see cref="DeviceCommunicationConfiguration.UsbBackend"/>.
    /// <para>返回 <see cref="UsbApiKind.Auto"/> 在当前平台解析到的默认后端。所有受支持桌面
    /// 平台均默认原生后端（Windows → winusb、Linux → usbfs、macOS → IOKit/IOUSBHost）；
    /// libusb 仅作为经 <see cref="DeviceCommunicationConfiguration.UsbBackend"/> 显式选择的回退。</para>
    /// </summary>
    private UsbApiKind PreferredAutoApiKind()
    {
        if (Configuration.UsbBackend != UsbApiKind.Auto)
        {
            return Configuration.UsbBackend;
        }
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? UsbApiKind.Native
            : UsbApiKind.Auto;
    }

    /// <summary>
    /// Returns the default backend name that <see cref="UsbApiKind.Auto"/> resolves to on the
    /// current platform (Windows/Linux/macOS → native; see <see cref="PreferredAutoApiKind"/>).
    /// <para>返回 <see cref="UsbApiKind.Auto"/> 在当前平台上解析到的默认后端名称
    /// （Windows/Linux/macOS 均为原生；见 <see cref="PreferredAutoApiKind"/>）。</para>
    /// </summary>
    private string? PreferredAutoApiName() => PreferredAutoApiKind() switch
    {
        UsbApiKind.Native => NativeUsbApiProvider.ApiNameConst,
        UsbApiKind.LibUsbDotNet => LibUsbApiProvider.ApiNameConst,
        _ => null
    };

    /// <summary>
    /// Returns a compact diagnostic summary of the last enumeration on the current platform, exposing per-backend observability state so a device-less environment can assert that enumeration actually ran instead of silently returning empty.
    /// <para>返回当前平台最近一次枚举的紧凑诊断摘要，暴露各后端的可观测状态，使无设备环境可断言枚举确实执行过，而非静默返回空。</para>
    /// </summary>
    /// <returns>A <c>key=value; ...</c> diagnostic string. <para><c>key=value; ...</c> 形式的诊断字符串。</para></returns>
    public string GetEnumerationDiagnostics()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"setupdi-succeeded={WinUSBFinder.LastSetupDiSucceeded}; nodes={WinUSBFinder.LastScannedNodeCount}; matched={WinUSBFinder.LastMatchedDeviceCount}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"usbfs-root={LinuxUsbFinder.LastUsbfsRootExists}; nodes={LinuxUsbFinder.LastScannedNodes}; matched={LinuxUsbFinder.LastMatchedDeviceCount}; perm-denied={LinuxUsbFinder.PermissionDeniedCount}; busy={LinuxUsbFinder.BusyCount}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"copy-devices={IOKitUsbFinder.LastCopyDevicesSucceeded}; scanned={IOKitUsbFinder.LastScannedDeviceCount}; matched={IOKitUsbFinder.LastMatchedDeviceCount}";
        }

        return "unsupported-platform";
    }
}