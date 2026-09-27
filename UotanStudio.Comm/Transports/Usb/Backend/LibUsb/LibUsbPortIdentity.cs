using System.Globalization;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

internal static class LibUsbPortIdentity
{
    internal static string? Observe(LibUsbDotNet.LibUsb.UsbDevice device)
    {
        try
        {
            return Format(device.BusNumber, device.PortNumbers);
        }
        catch (Exception error)
        {
            UsbTrace.Log($"LibUsb port topology unavailable: {error.GetType().Name}.");
            return null;
        }
    }

    internal static string? Format(byte bus, IEnumerable<byte> ports)
    {
        var chain = ports.Take(8).ToArray();
        if (chain.Length is < 1 or > 7 || chain.Contains((byte)0))
        {
            return null;
        }
        return "libusb-port:" + bus.ToString(CultureInfo.InvariantCulture) + ":"
            + string.Join(".", chain.Select(port => port.ToString(CultureInfo.InvariantCulture)));
    }
}