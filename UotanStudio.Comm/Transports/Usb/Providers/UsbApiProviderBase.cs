using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Providers;

/// <summary>
/// Shared template for USB API providers: discovery → enumeration → capabilities.
/// <para>USB API 提供器的共享模板：发现 → 枚举 → 能力。</para>
/// Subclasses supply the backend identity, support check, backend enumeration and the
/// capability profile; the common session/info projection is implemented here once.
/// <para>子类提供后端身份、支持判定、后端枚举与能力轮廓；通用的会话/信息投影在此实现一次。</para>
/// </summary>
internal abstract class UsbApiProviderBase : IUsbApiProvider
{
    /// <inheritdoc/>
    public abstract string ApiName { get; }

    /// <inheritdoc/>
    public abstract UsbApiKind ApiKind { get; }

    /// <inheritdoc/>
    public abstract bool IsSupportedOnCurrentPlatform { get; }

    /// <summary>
    /// Enumerates backend devices applying the optional filter.
    /// <para>使用可选过滤器枚举后端设备。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <returns>The discovered backend devices. <para>发现的后端设备。</para></returns>
    protected abstract List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter);

    /// <summary>
    /// Enumerates backend devices applying the optional filter and session options.
    /// <para>使用可选过滤器与会话选项枚举后端设备。</para>
    /// The default implementation ignores the options (backends that cannot honour them
    /// keep the previous behaviour); WinUSB-capable backends override this to forward
    /// timeouts/RAW_IO into newly created devices.
    /// <para>默认实现忽略选项（无法兑现选项的后端保持原有行为）；支持 WinUSB 的后端
    /// 重写此方法，将超时/RAW_IO 透传给新建设备。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <returns>The discovered backend devices. <para>发现的后端设备。</para></returns>
    protected virtual List<UsbDevice> EnumerateBackendDevices(UsbDeviceFilter? filter, UsbSessionOptions? options)
        => EnumerateBackendDevices(filter);

    /// <inheritdoc/>
    public IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null)
        => EnumerateDeviceSessions(filter, options: null);

    /// <summary>
    /// Enumerates device sessions applying the optional filter and session options.
    /// <para>使用可选过滤器与会话选项枚举设备会话。</para>
    /// This internal overload lets the communication layer forward
    /// <see cref="UsbSessionOptions"/> into the backend without changing the public
    /// <see cref="IUsbApiProvider"/> contract.
    /// <para>此内部重载让通信层可在不改变公开 <see cref="IUsbApiProvider"/> 契约的前提下，
    /// 将 <see cref="UsbSessionOptions"/> 透传给后端。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <returns>The discovered device sessions. <para>发现的设备会话。</para></returns>
    internal IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter, UsbSessionOptions? options)
    {
        if (!IsSupportedOnCurrentPlatform)
        {
            return [];
        }

        var devices = EnumerateBackendDevices(filter, options);
        return UsbProviderProjection.ToSessions(ApiName, ApiKind, devices, filter);
    }

    /// <inheritdoc/>
    public IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null)
    {
        if (!IsSupportedOnCurrentPlatform)
        {
            return [];
        }

        var devices = EnumerateBackendDevices(filter);
        return UsbProviderProjection.ToInfos(ApiName, ApiKind, devices, filter);
    }

    /// <inheritdoc/>
    public abstract UsbApiCapabilities GetCapabilities();

    /// <summary>
    /// Runs a backend enumeration swallowing expected availability failures
    /// (missing native runtime, init failure) and logging them instead of throwing.
    /// <para>执行后端枚举，吞掉预期的可用性失败（缺少原生运行库、初始化失败）并记录日志。</para>
    /// </summary>
    /// <param name="enumerate">The enumeration delegate. <para>枚举委托。</para></param>
    /// <param name="backendTag">Backend tag used in log messages. <para>日志中使用的后端标签。</para></param>
    /// <returns>The enumerated devices, or an empty list on availability failure. <para>枚举到的设备；可用性失败时返回空列表。</para></returns>
    protected static List<UsbDevice> SafeEnumerate(Func<List<UsbDevice>> enumerate, string backendTag)
    {
        try
        {
            return enumerate();
        }
        catch (DllNotFoundException ex)
        {
            UsbTrace.Log($"{backendTag} backend unavailable: {ex.Message}");
            return [];
        }
        catch (TypeInitializationException ex)
        {
            UsbTrace.Log($"{backendTag} initialization failed: {ex.Message}");
            return [];
        }
        catch
        {
            UsbTrace.Log($"{backendTag} enumeration failed with unknown exception.");
            return [];
        }
    }
}