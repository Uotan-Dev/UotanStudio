using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Mcp.Tools;

/// <summary>Device enumeration and transaction ownership for MCP hosts.</summary>
public interface IDeviceManagerGateway
{
    IReadOnlyList<UsbDeviceInfo> ListUsbDevices();
    IReadOnlyList<(string Serial, string State)> ListAdbTargets();
    TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation);
    void PushAdbFile(string serial, string localPath, string remotePath);
    TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation);
}

/// <summary>Owns device access when MCP runs without the desktop host.</summary>
internal sealed class HeadlessDeviceManagerGateway : IDeviceManagerGateway
{
    private readonly UotanStudio.Comm.CommKit _comm = new();

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices()
    {
        var infos = _comm.EnumerateUsbDevices();
        var hdcServer = DebugBridgeRoute.UseHdcServer();
        return infos.Where(info => !IsAdbInterface(info) || !DebugBridgeRoute.AdbServerOwns(info.SerialNumber))
            .Where(info => !hdcServer || !IsHdcInterface(info)).ToArray();
    }

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
    {
        var serverTargets = DebugBridgeRoute.UseAdbServer()
            ? new AdbServerServices("unused").ListTargets() : [];
        var directTargets = ListUsbDevices().Where(IsAdbInterface).Select(info =>
        {
            var identity = info.SerialNumber ?? info.DeviceKey;
            try
            {
                return (identity, RunDirectAdb(identity, _ => "device"));
            }
            catch (Exception)
            {
                return (identity, "offline");
            }
        });
        return serverTargets.Concat(directTargets).ToArray();
    }

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (DebugBridgeRoute.AdbServerOwns(serial))
        {
            using var stream = new AdbServerServices(serial).Open(service);
            return operation(stream);
        }
        return RunDirectAdb(serial, session =>
        {
            using var stream = new AdbServiceStream(session.OpenStream(service));
            return operation(stream);
        });
    }

    public void PushAdbFile(string serial, string localPath, string remotePath)
    {
        if (DebugBridgeRoute.AdbServerOwns(serial))
        {
            new AdbServerServices(serial).Push(localPath, remotePath);
            return;
        }
        _ = RunDirectAdb(serial, session =>
        {
            using var sync = session.CreateSyncClient();
            sync.Push(localPath, remotePath);
            return true;
        });
    }

    private TResult RunDirectAdb<TResult>(string identity, Func<AdbSession, TResult> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var matches = ListUsbDevices().Where(info => IsAdbInterface(info) &&
            string.Equals(info.SerialNumber ?? info.DeviceKey, identity, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
            throw new IOException("ADB USB target is missing or ambiguous.");
        var usb = _comm.OpenUsbDeviceSessionByKey(matches[0].DeviceKey)
            ?? throw new IOException("ADB USB interface could not be opened.");
        AdbSession session;
        try { session = new AdbSession(new UsbDeviceTransport(usb)); }
        catch { usb.Dispose(); throw; }
        using (session)
        {
            session.Connect();
            if (!session.IsConnected)
                throw new IOException("ADB USB authentication did not complete.");
            return operation(session);
        }
    }

    private static bool IsAdbInterface(UsbDeviceInfo info)
        => info.Interfaces.Any(iface => iface.Class == 0xFF && iface.SubClass == 0x42 && iface.Protocol == 0x01) ||
            info.InterfaceMetadataObserved && info.InterfaceClass == 0xFF &&
            info.InterfaceSubClass == 0x42 && info.InterfaceProtocol == 0x01;

    private static bool IsHdcInterface(UsbDeviceInfo info)
        => info.Interfaces.Any(iface => iface.Class == 0xFF && iface.SubClass == 0x50 && iface.Protocol == 0x01) ||
            info.InterfaceMetadataObserved && info.InterfaceClass == 0xFF &&
            info.InterfaceSubClass == 0x50 && info.InterfaceProtocol == 0x01;

    public TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var targets = ListUsbDevices().Where(info =>
            info.Interfaces.Any(iface => iface.Class == 0xFF &&
                iface.SubClass == 0x42 && iface.Protocol == 0x03) ||
            info.InterfaceMetadataObserved && info.InterfaceClass == 0xFF &&
                info.InterfaceSubClass == 0x42 && info.InterfaceProtocol == 0x03).ToArray();
        if (targets.Length != 1)
            throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
        var usb = _comm.OpenUsbDeviceSessionByKey(targets[0].DeviceKey)
            ?? throw new InvalidOperationException("The selected Fastboot device is unavailable.");
        var factory = new FastbootFactory();
        if (!factory.TryCreate(usb, out var protocol) || protocol is not FastbootSession session)
        {
            usb.Dispose();
            throw new InvalidOperationException("The selected device does not expose Fastboot.");
        }
        using (session)
            return operation(session);
    }
}