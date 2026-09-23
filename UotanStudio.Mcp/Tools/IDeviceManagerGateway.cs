using UotanStudio.Comm.Protocols.Fastboot;
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
