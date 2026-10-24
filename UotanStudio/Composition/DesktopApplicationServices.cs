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

    /// <summary>
    /// Releases the device monitor's open leases — direct USB interfaces included — so the device is handed back to
    /// the operating system before the process disappears. Runs at most once; the later <see cref="Dispose"/> call
    /// reuses the same result.
    /// <para>释放设备监视持有的未完成租约（含直连 USB 接口），使设备在进程消失之前交还操作系统。最多执行一次；
    /// 随后的 <see cref="Dispose"/> 调用复用同一结果。</para>
    /// </summary>
    /// <param name="timeout">Upper bound on the wait. <para>等待上限。</para></param>
    /// <returns><see langword="true"/> when every lease was released within the budget.
    /// <para>全部租约在预算内释放完成时为 <see langword="true"/>。</para></returns>
    internal bool ReleaseUsbOccupancy(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _usbOccupancyReleased, 1) != 0)
        {
            return true;
        }

        var released = _monitor.ReleaseUsbOccupancy(timeout);
        LogService.Instance.Write(LogLevel.Info, "Lifecycle",
            released
                ? $"Device occupancy released before shutdown within {timeout.TotalMilliseconds:0} ms."
                : $"Device occupancy release exceeded {timeout.TotalMilliseconds:0} ms; the remainder continues in the background.");
        return released;
    }

    /// <summary>Releases USB occupancy, then disposes the device panel and the monitor.
    /// <para>先解除 USB 占用，再释放设备面板与监视实例。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ReleaseUsbOccupancy(DeviceLeaseReleaseBudget);
        }
        finally
        {
            try { _devicePanelAccessor()?.Dispose(); }
            finally { _monitor.Dispose(); }
        }
    }

    /// <summary>Default budget for the shutdown device release.
    /// <para>关闭时设备释放的默认预算。</para>
    /// </summary>
    private static readonly TimeSpan DeviceLeaseReleaseBudget =
        Comm.Devices.Services.DeviceController.ShutdownReleaseBudget;

    private int _usbOccupancyReleased;
}