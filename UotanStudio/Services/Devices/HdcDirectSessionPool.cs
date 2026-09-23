using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Services.Devices;

/// <summary>Shares one authenticated USB connection across device details, shell tasks and HiLog channels.</summary>
internal static class HdcDirectSessionPool
{
    private static readonly Lock Sync = new();
    private sealed class Entry(HdcSession session, DetectedDevice device)
    {
        internal HdcSession Session { get; } = session;
        internal DetectedDevice Device { get; } = device;
        internal int Leases { get; set; }
        internal bool Retired { get; set; }
    }

    private static readonly Dictionary<string, Entry> Sessions = new(StringComparer.Ordinal);

    internal static DetectedDevice? FindOwnedDevice(string devicePath)
    {
        lock (Sync)
            return Sessions.Values.Where(entry => !entry.Retired && entry.Session.IsConnected &&
                    entry.Session.Transport is UsbDeviceTransport usb
                    && string.Equals(usb.Session.DeviceInfo.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Device).FirstOrDefault();
    }

    internal static HdcSession Acquire(DetectedDevice device)
    {
        lock (Sync)
        {
            if (DeviceMonitorService.UseHdcServerFor(device))
                throw new IOException("HDC server appeared; direct USB access is prohibited. Retry through the server.");
            device = DeviceMonitorService.ResolveDirectUsbDevice(device);
            if (Sessions.TryGetValue(device.Key, out var existing))
            {
                if (existing.Retired)
                    throw new IOException("An earlier HDC USB operation is still releasing this interface.");
                if (existing.Session.IsConnected) return Lease(existing);
                if (existing.Leases != 0)
                    throw new IOException("An earlier HDC USB operation is still using a disconnected session.");
                Sessions.Remove(device.Key);
                existing.Session.Dispose();
            }
            var usb = DeviceMonitorService.Instance.OpenUsbSession(device)
                ?? throw new IOException("HDC USB interface could not be opened; another host may own it.");
            HdcSession? session = null;
            try
            {
                session = new HdcSession(new UsbDeviceTransport(usb));
                session.Connect();
                var entry = new Entry(session, device);
                Sessions.Add(device.Key, entry);
                return Lease(entry);
            }
            catch { if (session is not null) session.Dispose(); else usb.Dispose(); throw; }
        }
    }

    private static HdcSession Lease(Entry entry)
    {
        var lease = entry.Session.CreateLease(() => Release(entry));
        entry.Leases++;
        return lease;
    }

    private static void Release(Entry entry)
    {
        HdcSession? dispose = null;
        lock (Sync)
        {
            entry.Leases--;
            if (entry.Retired && entry.Leases == 0)
            {
                Sessions.Remove(entry.Device.Key);
                dispose = entry.Session;
            }
        }
        dispose?.Dispose();
    }

    internal static void Retain(IEnumerable<DetectedDevice> devices)
    {
        var keys = devices.Where(d => d.Protocol == DeviceProtocolKind.Hdc).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var dispose = new List<HdcSession>();
        lock (Sync)
        {
            foreach (var key in Sessions.Keys.Where(key => !keys.Contains(key)).ToArray())
            {
                var entry = Sessions[key];
                entry.Retired = true;
                if (entry.Leases != 0) continue;
                Sessions.Remove(key);
                dispose.Add(entry.Session);
            }
        }
        foreach (var session in dispose) session.Dispose();
    }

    internal static void Clear()
    {
        var dispose = new List<HdcSession>();
        lock (Sync)
        {
            foreach (var (key, entry) in Sessions.ToArray())
            {
                entry.Retired = true;
                if (entry.Leases != 0) continue;
                Sessions.Remove(key);
                dispose.Add(entry.Session);
            }
        }
        foreach (var session in dispose) session.Dispose();
    }
}