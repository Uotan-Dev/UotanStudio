using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Contracts;

internal static class UsbInfoCopy
{
    internal static UsbDeviceInfo Copy(UsbDeviceInfo info) => new()
    {
        ApiName = info.ApiName,
        SourceApiKind = info.SourceApiKind,
        SourceDeviceType = info.SourceDeviceType,
        DevicePath = info.DevicePath,
        DeviceKey = info.DeviceKey,
        SerialNumber = info.SerialNumber,
        ContainerId = info.ContainerId,
        PhysicalPortPath = info.PhysicalPortPath,
        DeviceInstanceId = info.DeviceInstanceId,
        VendorId = info.VendorId,
        ProductId = info.ProductId,
        InterfaceClass = info.InterfaceClass,
        InterfaceSubClass = info.InterfaceSubClass,
        InterfaceProtocol = info.InterfaceProtocol,
        InterfaceMetadataObserved = info.InterfaceMetadataObserved,
        Speed = info.Speed,
        Interfaces = info.Interfaces.Select(iface => new UsbInterfaceInfo
        {
            InterfaceNumber = iface.InterfaceNumber,
            Class = iface.Class,
            SubClass = iface.SubClass,
            Protocol = iface.Protocol,
            Endpoints = iface.Endpoints.Select(endpoint => new UsbEndpointInfo
            {
                EndpointAddress = endpoint.EndpointAddress,
                Attributes = endpoint.Attributes,
                MaxPacketSize = endpoint.MaxPacketSize,
                Interval = endpoint.Interval
            }).ToArray()
        }).ToArray()
    };
}