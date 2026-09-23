using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Projects exact CommKit USB keys without merging backends or serials.</summary>
public sealed class UsbDiscoverySource(ICommKit kit, UsbApiKind backend) : IDeviceDiscoverySource
{
    public const string SourceName = "usb";
    public string Name => SourceName;

    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = await kit.EnumerateUsbDevicesAsync(backend, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Select(device =>
        {
            if (string.IsNullOrWhiteSpace(device.DeviceKey) || string.IsNullOrWhiteSpace(device.DevicePath))
            {
                throw new InvalidDataException("USB endpoint has no exact device key or path.");
            }

            return new DeviceEndpoint
            {
                Source = Name,
                Transport = device.SourceApiKind.ToString(),
                Address = device.DeviceKey,
                DisplayName = device.SerialNumber,
                Serial = device.SerialNumber,
                Route = "direct-usb",
                Protocol = (device.InterfaceClass, device.InterfaceSubClass, device.InterfaceProtocol) switch
                {
                    (0xff, 0x42, 1) => "adb",
                    (0xff, 0x50, 1) => "hdc",
                    _ => null
                }
            };
        }).ToArray();
    }
}
