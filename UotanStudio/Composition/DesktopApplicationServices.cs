using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Composition;

/// <summary>Owns desktop device monitoring while retaining the existing singleton identity.</summary>
internal sealed class DesktopApplicationServices : IDisposable
{
    private readonly DeviceMonitorService _monitor = DeviceMonitorService.Instance;
    private Task<IReadOnlyList<DetectedDevice>>? _initialScan;
    private bool _disposed;

    internal Task<IReadOnlyList<DetectedDevice>> StartAndScanAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _initialScan ??= Task.Run(() =>
        {
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
        try { DeviceViewModel.Instance?.Dispose(); }
        finally { _monitor.Dispose(); }
    }
}