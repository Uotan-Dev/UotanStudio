using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Contracts;

/// <summary>Immutable observed USB identity used to validate a selected by-key open.</summary>
public sealed record UsbEndpointDetails(
    UsbApiKind Backend, string Path, ushort VendorId, ushort ProductId,
    byte? InterfaceClass, byte? InterfaceSubClass, byte? InterfaceProtocol);
