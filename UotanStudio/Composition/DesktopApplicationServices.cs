using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Composition;

/// <summary>
/// Owns the application-scoped device monitor and releases it, together with any device session
/// already created, when the desktop shuts down.
/// <para>持有应用级设备监视实例，并在桌面关闭时连同已创建的设备会话一并释放。</para>
/// </summary>
internal sealed class DesktopApplicationServices : IDisposable
{
    private readonly DeviceMonitorService _monitor;
    private readonly Func<DeviceViewModel?> _devicePanelAccessor;
    private Task<IReadOnlyList<DetectedDevice>>? _initialScan;
    private bool _disposed;

    /// <summary>
    /// Compatibility entry: forwards to the explicit constructor with the application-scoped monitor
    /// and the default device-session accessor.
    /// <para>兼容入口：以应用级设备监视实例与默认设备会话访问器转发到显式构造。</para>
    /// </summary>
    internal DesktopApplicationServices() : this(App.DeviceMonitor) { }

    /// <summary>
    /// Explicit assembly: receives the application-scoped device monitor and an accessor resolving
    /// the device session to release.
    /// <para>显式装配：接收应用级设备监视实例，以及用于解析待释放设备会话的访问器。</para>
    /// </summary>
    /// <param name="monitor">The device monitor whose release ownership moves to this instance. <para>释放归属转移到本实例的设备监视器。</para></param>
    /// <param name="devicePanelAccessor">Accessor resolving the device session at release time; null falls back to the shared instance. <para>释放时解析设备会话的访问器；为 null 时沿用共享实例。</para></param>
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
        try { _devicePanelAccessor()?.Dispose(); }
        finally { _monitor.Dispose(); }
    }
}