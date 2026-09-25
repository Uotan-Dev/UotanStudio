using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

public static class UsbProtocolClassifier
{
    public static string? Classify(UsbDeviceInfo device)
    {
        if (device.VendorId == 0x05C6 && device.ProductId is 0x9008 or 0x9006 or 0x900E or 0x901D or 0x9307)
            return "edl";
        // MediaTek boot chain over USB (mtkclient): BROM 0E8D:0003, preloader/DA 0E8D:2000|2001.
        if (device.VendorId == 0x0E8D && device.ProductId is 0x0003 or 0x2000 or 0x2001)
            return "mtk";
        if (device.VendorId == RkdlConstants.RockchipVid &&
            ((device.ProductId >> 8) != 0 || RkdlConstants.KnownLoaderProductIds.Contains(device.ProductId)))
            return "rkdl";

        foreach (var iface in device.Interfaces)
        {
            var protocol = FromInterface(iface.Class, iface.SubClass, iface.Protocol);
            if (protocol is not null) return protocol;
        }
        return device.InterfaceMetadataObserved
            ? FromInterface(device.InterfaceClass, device.InterfaceSubClass, device.InterfaceProtocol)
            : null;
    }

    private static string? FromInterface(byte? cls, byte? sub, byte? protocol) =>
        (cls, sub, protocol) switch
        {
            (0xff, 0x50, 1) => "hdc",
            (0xff, 0x42, 1) => "adb",
            (0xff, 0x42, 3) => "fastboot",
            _ => null
        };
}