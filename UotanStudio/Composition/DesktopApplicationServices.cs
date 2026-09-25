using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Composition;

/// <summary>
/// <para>应用级设备监视作用域的持有者：保留 <see cref="DeviceMonitorService"/> 与设备面板单例的现有身份，
/// 在桌面退出时统一释放。创建与释放见架构评估 2026-09-10 的「创建者—持有者—释放者」表。</para>
/// Owns the application-scoped device monitoring while retaining the existing singleton identity of
/// <see cref="DeviceMonitorService"/> and the device panel; releases both on desktop exit.
/// </summary>
internal sealed class DesktopApplicationServices : IDisposable
{
    private readonly DeviceMonitorService _monitor;
    private readonly Func<DeviceViewModel?> _devicePanelAccessor;
    private Task<IReadOnlyList<DetectedDevice>>? _initialScan;
    private bool _disposed;

    /// <summary>
    /// <para>兼容入口：转发到显式构造，使用应用级单例监视器与设备面板访问器。</para>
    /// Compatibility entry: forwards to the explicit constructor using the application-wide
    /// singleton monitor and the default device-panel accessor.
    /// </summary>
    internal DesktopApplicationServices() : this(App.DeviceMonitor) { }

    /// <summary>
    /// <para>显式装配：接收桌面设备监视服务（应用级作用域，不由窗口拥有）。设备面板在释放时经
    /// <paramref name="devicePanelAccessor"/> 读取，保持原有的惰性语义：仅释放会话期间实际创建过的
    /// 面板，绝不只为关闭应用而创建设备视图模型。</para>
    /// Explicit assembly: receives the desktop device monitor service (application scope, never a
    /// window-owned resource). The device panel is resolved through
    /// <paramref name="devicePanelAccessor"/> at dispose time to keep the original lazy semantics:
    /// only a panel actually created during the session is released, and a device view model is
    /// never created just to shut the application down.
    /// </summary>
    /// <param name="monitor">
    /// <para>桌面设备监视服务；释放归属转移到本实例。</para>
    /// The desktop device monitor service; its release ownership moves to this instance.
    /// </param>
    /// <param name="devicePanelAccessor">
    /// <para>释放时读取设备面板单例的访问器；为 null 时沿用 <c>DeviceViewModel.Instance</c>。</para>
    /// Accessor that resolves the device panel singleton at dispose time; null falls back to
    /// <c>DeviceViewModel.Instance</c>.
    /// </param>
    internal DesktopApplicationServices(DeviceMonitorService monitor, Func<DeviceViewModel?>? devicePanelAccessor = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _devicePanelAccessor = devicePanelAccessor ?? (static () => DeviceViewModel.Instance);
    }

    internal Task<IReadOnlyList<DetectedDevice>> StartAndScanAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _initialScan ??= Task.Run(() =>
        {
            try { DebugBridgeIdentityService.EnsureDefaults(); }
            catch (AggregateException ex)
            {
                foreach (var failure in ex.InnerExceptions)
                    LogService.Instance.Write(LogLevel.Error, "Authentication", failure.ToString());
            }
            _monitor.Start();
            return _monitor.Scan();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Startup completes before the splash transitions to the closable main window.
        // Never create a device view model just to shut down an application.
        try { _devicePanelAccessor()?.Dispose(); }
        finally { _monitor.Dispose(); }
    }
}