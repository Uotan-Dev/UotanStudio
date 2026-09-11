using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Services.Devices;

/// <summary>Shares one authenticated USB connection across device details, shell tasks and HiLog channels.</summary>
internal static class HdcDirectSessionPool
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, HdcSession> Sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DetectedDevice> Devices = new(StringComparer.Ordinal);

    internal static DetectedDevice? FindOwnedDevice(string devicePath)
    {
        lock (Sync)
            return Sessions.Where(pair => pair.Value.IsConnected && pair.Value.Transport is UsbDeviceTransport usb
                    && string.Equals(usb.Session.DeviceInfo.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                .Select(pair => Devices[pair.Key]).FirstOrDefault();
    }

    internal static HdcSession Acquire(DetectedDevice device)
    {
        lock (Sync)
        {
            if (DebugBridgeRoute.UseHdcServer())
                throw new IOException("HDC server appeared; direct USB access is prohibited. Retry through the server.");
            if (Sessions.TryGetValue(device.Key, out var existing) && existing.IsConnected)
                return existing.CreateLease();
            if (existing is not null) { Sessions.Remove(device.Key); existing.Dispose(); }
            var usb = CommKit.Default.OpenUsbDeviceSessionByKey(device.Key)
                ?? throw new IOException("HDC USB interface could not be opened; another host may own it.");
            HdcSession? session = null;
            try
            {
                session = new HdcSession(new UsbDeviceTransport(usb));
                session.Connect();
                Sessions.Add(device.Key, session);
                Devices[device.Key] = device;
                return session.CreateLease();
            }
            catch { if (session is not null) session.Dispose(); else usb.Dispose(); throw; }
        }
    }

    internal static void Retain(IEnumerable<DetectedDevice> devices)
    {
        var keys = devices.Where(d => d.Protocol == DeviceProtocolKind.Hdc).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        lock (Sync)
        {
            foreach (var key in Sessions.Keys.Where(key => !keys.Contains(key)).ToArray())
            {
                var session = Sessions[key];
                Sessions.Remove(key);
                Devices.Remove(key);
                session.Dispose();
            }
        }
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            foreach (var session in Sessions.Values) session.Dispose();
            Sessions.Clear();
            Devices.Clear();
        }
    }
}