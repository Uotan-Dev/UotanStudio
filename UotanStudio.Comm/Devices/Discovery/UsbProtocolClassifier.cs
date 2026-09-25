using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Maps a USB device to its download or debug-bridge protocol from VID/PID bootrom signatures and
/// interface descriptor triples.
/// <para>依据 VID/PID 的 bootrom 特征与接口描述符三元组，把 USB 设备映射到其下载或调试桥协议。</para>
/// </summary>
public static class UsbProtocolClassifier
{
    /// <summary>
    /// Classifies the device as <c>edl</c>, <c>mtk</c> or <c>rkdl</c> from its bootrom VID/PID pair,
    /// otherwise from the first interface matching a known debug-bridge triple
    /// (<c>ff/42/1</c> adb, <c>ff/42/3</c> fastboot, <c>ff/50/1</c> hdc); when no per-interface
    /// enumeration took place it falls back to the flattened interface descriptor.
    /// <para>先按 bootrom 的 VID/PID 对分类为 <c>edl</c>、<c>mtk</c> 或 <c>rkdl</c>；否则按首个匹配已知
    /// 调试桥三元组（<c>ff/42/1</c> adb、<c>ff/42/3</c> fastboot、<c>ff/50/1</c> hdc）的接口分类；
    /// 未做逐接口枚举时回退到扁平化的接口描述符。</para>
    /// </summary>
    /// <param name="device">
    /// The enumerated USB device. <para>已枚举的 USB 设备。</para>
    /// </param>
    /// <returns>The protocol name, or <see langword="null"/> when nothing identifies a known protocol.
    /// <para>协议名；没有任何已知协议特征时返回 <see langword="null"/>。</para></returns>
    public static string? Classify(UsbDeviceInfo device)
    {
        if (device.VendorId == 0x05C6 && device.ProductId is 0x9008 or 0x9006 or 0x900E or 0x901D or 0x9307)
            return "edl";
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