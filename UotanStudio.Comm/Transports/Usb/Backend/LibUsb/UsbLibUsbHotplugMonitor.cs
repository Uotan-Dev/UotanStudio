using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// Monitors USB device arrivals/removals through libusb's native hotplug callback
/// (Linux/macOS only; libusb hotplug is not supported on Windows).
/// <para>通过 libusb 原生热插拔回调监视设备新增/移除（仅 Linux/macOS；
/// Windows 上 libusb 不支持热插拔）。</para>
/// Callers fall back to <see cref="UsbDeviceMonitor"/> polling when the platform does
/// not support hotplug or the native runtime is absent.
/// <para>当平台不支持热插拔或缺少原生运行库时，调用方回退到
/// <see cref="UsbDeviceMonitor"/> 轮询。</para>
/// </summary>
internal sealed class UsbLibUsbHotplugMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<UsbDeviceInfo>> _enumerator;
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private UsbContext? _context;
    private Dictionary<string, UsbDeviceInfo> _lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public UsbLibUsbHotplugMonitor(
        Func<IReadOnlyList<UsbDeviceInfo>> enumerator,
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        Action<Exception>? onError,
        UsbDeviceFilter? filter)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onError = onError;

        var context = new UsbContext();
        try
        {
            context.HotplugOptions.VendorId = filter?.VendorId is ushort vid ? vid : (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.ProductId = filter?.ProductId is ushort pid ? pid : (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.DeviceClass = (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.HotplugEventFlags = HotplugEvent.DeviceArrived | HotplugEvent.DeviceLeft;

            _lastSnapshot = UsbDeviceMonitor.BuildMap(_enumerator());
            context.DeviceEvent += OnDeviceEvent;
            context.RegisterHotPlug();
            _context = context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private void OnDeviceEvent(object? sender, DeviceEventArgs e)
    {
        _ = e;
        _ = Task.Run(() =>
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                UsbTrace.Log($"UsbLibUsbHotplugMonitor poll failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void PollOnce()
    {
        var currentSnapshot = UsbDeviceMonitor.BuildMap(_enumerator());
        List<UsbDeviceChange>? changes;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changes = UsbDeviceMonitor.ComputeChanges(currentSnapshot, _lastSnapshot);
            _lastSnapshot = currentSnapshot;
        }

        if (changes is { Count: > 0 })
        {
            try
            {
                _onChanged(changes);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                UsbTrace.Log($"UsbLibUsbHotplugMonitor change callback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        var context = _context;
        _context = null;
        if (context != null)
        {
            try
            {
                context.UnregisterHotPlug();
            }
            catch (Exception ex)
            {
                UsbTrace.Log($"UsbLibUsbHotplugMonitor unregister failed: {ex.GetType().Name}: {ex.Message}");
            }
            context.Dispose();
        }

        _lastSnapshot.Clear();
    }
}