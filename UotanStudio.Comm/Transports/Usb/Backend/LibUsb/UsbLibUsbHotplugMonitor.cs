using System.Collections.Concurrent;
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
    private readonly Func<IReadOnlyList<UsbDeviceProbeHint>, IReadOnlyList<UsbDeviceChange>> _pass;
    private readonly ConcurrentQueue<UsbDeviceProbeHint> _hints = new();
    private readonly Action<IReadOnlyList<UsbDeviceChange>> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private UsbContext? _context;
    private bool _disposed;
    private int _polling, _pending;

    public UsbLibUsbHotplugMonitor(
        Func<IReadOnlyList<UsbDeviceProbeHint>, IReadOnlyList<UsbDeviceChange>> pass,
        IReadOnlyList<UsbDeviceInfo> initialDevices,
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        Action<Exception>? onError,
        UsbDeviceFilter? filter,
        bool fireInitialSnapshot = false)
    {
        _pass = pass ?? throw new ArgumentNullException(nameof(pass));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onError = onError;

        var context = LibUsbContextFactory.Create();
        try
        {
            context.HotplugOptions.VendorId = filter?.VendorId is ushort vid ? vid : (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.ProductId = filter?.ProductId is ushort pid ? pid : (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.DeviceClass = (int)HotplugOptionFlag.LibusbHotplugMatchAny;
            context.HotplugOptions.HotplugEventFlags = HotplugEvent.DeviceArrived | HotplugEvent.DeviceLeft;

            context.DeviceEvent += OnDeviceEvent;
            context.RegisterHotPlug();
            _context = context;
        }
        catch
        {
            context.Dispose();
            throw;
        }

        RaiseInitialSnapshot(initialDevices, fireInitialSnapshot);
    }

    /// <summary>
    /// Reports the baseline inventory as additions once hot-plug registration is live.
    /// <para>在热插拔注册生效后把基线清单作为新增上报一次。</para>
    /// </summary>
    private void RaiseInitialSnapshot(IReadOnlyList<UsbDeviceInfo> initialDevices, bool fireInitialSnapshot)
    {
        if (!fireInitialSnapshot || initialDevices.Count == 0)
        {
            return;
        }

        var changes = initialDevices
            .Select(device => new UsbDeviceChange { Kind = UsbDeviceChangeKind.Added, Device = device })
            .ToList();
        try
        {
            _onChanged(changes);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
            UsbTrace.Log($"UsbLibUsbHotplugMonitor initial snapshot callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnDeviceEvent(object? sender, DeviceEventArgs e)
    {
        UsbDeviceProbeHint hint;
        if (e is DeviceArrivedEventArgs arrived)
        {
            var device = arrived.Device;
            hint = new UsbDeviceProbeHint
            {
                Kind = UsbDeviceChangeKind.Added,
                DevicePath = BuildPath(device.BusNumber, device.Address, device.VendorId, device.ProductId),
                VendorId = device.VendorId,
                ProductId = device.ProductId
            };
        }
        else if (e is DeviceLeftEventArgs left)
        {
            var descriptor = left.DeviceInfo.Descriptor;
            hint = new UsbDeviceProbeHint
            {
                Kind = UsbDeviceChangeKind.Removed,
                PhysicalPortPath = left.DeviceInfo.PortInfo.ToString(),
                VendorId = descriptor.VendorId,
                ProductId = descriptor.ProductId
            };
        }
        else
        {
            hint = new UsbDeviceProbeHint { Kind = UsbDeviceChangeKind.Changed };
        }

        _hints.Enqueue(hint);
        TriggerPoll();
    }

    private static string BuildPath(int bus, int address, ushort vendorId, ushort productId) =>
        $"Bus {bus} Device {address}: {vendorId:X4}:{productId:X4}";

    private void TriggerPoll()
    {
        Interlocked.Exchange(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(PollLoop);
    }

    private void PollLoop()
    {
        try
        {
            do
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
            }
            while (Interlocked.Exchange(ref _pending, 0) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            if (Volatile.Read(ref _pending) != 0)
            {
                TriggerPoll();
            }
        }
    }

    private void PollOnce()
    {
        var hints = DrainHints();
        if (hints.Count == 0)
        {
            return;
        }

        IReadOnlyList<UsbDeviceChange> changes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            changes = _pass(hints);
        }

        if (changes.Count > 0)
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

    private List<UsbDeviceProbeHint> DrainHints()
    {
        var hints = new List<UsbDeviceProbeHint>();
        while (_hints.TryDequeue(out var hint))
        {
            hints.Add(hint);
        }

        return hints;
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

            try
            {
                context.Dispose();
            }
            catch (Exception ex)
            {
                UsbTrace.LogErrorFormatted(
                    $"UsbLibUsbHotplugMonitor context dispose failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        while (_hints.TryDequeue(out _))
        {
        }
    }
}