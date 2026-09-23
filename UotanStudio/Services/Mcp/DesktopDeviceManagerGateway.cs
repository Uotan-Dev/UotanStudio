using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Mcp;

/// <summary>Routes embedded MCP device tools through the desktop device manager.</summary>
public sealed class DesktopDeviceManagerGateway : IDeviceManagerGateway
{
    private readonly DeviceMonitorService _monitor = DeviceMonitorService.Instance;

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => _monitor.ListRoutableUsbDevices();

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => _monitor.ListAdbTargets();

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
        => _monitor.RunAdbService(serial, service, operation);

    public void PushAdbFile(string serial, string localPath, string remotePath)
        => _monitor.PushAdbFile(serial, localPath, remotePath);

    public TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation)
        => _monitor.RunSingleFastboot(operation);
}