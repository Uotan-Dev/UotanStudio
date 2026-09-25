using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>Guards debug interfaces before a native USB handle is opened.</summary>
internal static class UsbAccessPolicy
{
    private static Action<byte?, byte?, byte?, string?>? _check;

    internal static void Configure(Action<byte?, byte?, byte?, string?> check)
        => Volatile.Write(ref _check, check ?? throw new ArgumentNullException(nameof(check)));

    internal static void RequireDirect(UsbDeviceInfo info)
        => RequireDirect(info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol, info.SerialNumber);

    internal static void RequireDirect(byte? interfaceClass, byte? subclass, byte? protocol, string? serial = null)
    {
        if (Volatile.Read(ref _check) is { } check)
        {
            check(interfaceClass, subclass, protocol, serial);
            return;
        }

        // A backend can be used before the service composition root is initialized.
        if (interfaceClass == 0xff && protocol == 1 && subclass is 0x42 or 0x50)
        {
            throw new IOException("Debug USB ownership cannot be checked before device services are initialized.");
        }
    }
}